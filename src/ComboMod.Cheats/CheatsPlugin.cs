using System;
using System.Collections.Generic;
using BepInEx;
using ComboMod.Editor;
using Entities;
using UnityEngine;

namespace ComboMod.Cheats
{
    /// <summary>
    /// ComboMod.Cheats: editing the current run, and handing yourself items.
    /// <para>
    /// Kept out of ComboMod.Editor on purpose. Editor is a tuning tool whose changes cannot
    /// touch a save; everything here <b>writes to <c>GameState.save</c></b> and is permanent for
    /// the run. Someone who wants to rebalance buildings should not have to install a money
    /// editor to do it.
    /// </para>
    /// <para>
    /// Contributes its tabs through <see cref="PanelTabs"/>, so the dependency runs one way:
    /// Cheats knows about Editor, never the reverse.
    /// </para>
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(ComboMod.Plugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(EditorPlugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    public sealed class CheatsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.combolands.combomod.cheats";
        public const string PluginName = "ComboMod Cheats";
        public const string PluginVersion = "0.2.0";

        private void Awake()
        {
            // Register the members only this assembly reflects on, so an unknown-build report
            // names them when they move rather than staying silent about the Run tab.
            SafetyGate.AddCheck("GameController._weeksAllowed (weeks editing)",
                () => SafetyGate.HasInstanceField(typeof(GameState.GameController), "_weeksAllowed"));
            SafetyGate.AddCheck("ScoreController.Money backing field (money editing)",
                () => SafetyGate.HasInstanceField(typeof(GameState.ScoreController), "<Money>k__BackingField"));
            SafetyGate.AddCheck("GameController.DebugChangeWeeks",
                () => SafetyGate.HasInstanceMethod(typeof(GameState.GameController), "DebugChangeWeeks"));
            SafetyGate.AddCheck("ScorePanel.UpdateGoalText (HUD refresh)",
                () => SafetyGate.HasInstanceMethod(typeof(UI.ScorePanel), "UpdateGoalText"));

            RunTab.Register();
            GiveTab.Register();

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. Run and Give tabs added to the panel.");
        }
    }

    /// <summary>The Run tab: money, weeks, score, target, counters and slots.</summary>
    internal static class RunTab
    {
        private static readonly Dictionary<string, string> Buffer = new Dictionary<string, string>();

        internal static void Register() => PanelTabs.Register("Run", Draw);

        private static void Draw(PanelContext c)
        {
            if (!RunState.Available)
            {
                GUILayout.Label("No run loaded. Start or continue a game first.", c.Muted);
                return;
            }

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Some of these persist, some do not", c.Header);
            GUILayout.Label(
                "Saved: money and the four consumable counters. Editing those changes GameState.save "
                + "and removing ComboMod will not undo it (nothing is corrupted - they are plain integers).",
                c.Muted);
            GUILayout.Label(
                "Not saved: weeks remaining, score, and milestone target. Those reset on reload.",
                c.Muted);
            GUILayout.EndVertical();

            GUILayout.Space(c.S(6f));

            GUILayout.Label("Saved with the run", c.Section);
            Field(c, "Money", RunState.Money, v => RunState.SetMoney((int)v));

            GUILayout.Space(c.S(6f));
            GUILayout.Label("Runtime only", c.Section);
            Field(c, "Weeks remaining", RunState.WeeksRemaining, v => RunState.SetWeeksRemaining((int)v));
            Field(c, "Score", RunState.Score_, v => RunState.SetScore(v));
            Field(c, "Milestone target", RunState.ScoreRequired, v => RunState.SetScoreRequired((int)v));

            GUILayout.Space(c.S(6f));
            GUILayout.Label("Consumables (saved)", c.Section);
            Field(c, "Rerolls", RunState.Rerolls, v => RunState.SetRerolls((int)v));
            Field(c, "Removes", RunState.Removes, v => RunState.SetRemoves((int)v));
            Field(c, "Dismisses", RunState.Dismisses, v => RunState.SetDismisses((int)v));
            Field(c, "Rewinds", RunState.Rewinds, v => RunState.SetRewinds((int)v));

            GUILayout.Space(c.S(6f));
            GUILayout.Label("Inventory slots (saved, add-only)", c.Section);
            Slots(c, "Heirloom slots", RunState.HeirloomSlots, RunState.HeirloomSlotSoftCap,
                RunState.SetHeirloomSlots, RunState.AddHeirloomSlots);
            Slots(c, "Consumable slots", RunState.ConsumableSlots, int.MaxValue,
                RunState.SetConsumableSlots, RunState.AddConsumableSlots);

            GUILayout.Space(c.S(4f));
            GUILayout.Label(
                "Money is written directly, bypassing the lifetime gold counters in Unlocks.save.",
                c.Muted);
            GUILayout.Label(
                "Slots cannot be removed - the game has no RemoveSlot, so a count only goes up.",
                c.Muted);
        }

        private static void Field(PanelContext c, string label, long current, Action<long> apply)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, c.Body, GUILayout.Width(c.S(140f)));
            GUILayout.Label(current.ToString(), c.Highlight, GUILayout.Width(c.S(90f)));

            string key = label;
            if (!Buffer.ContainsKey(key))
                Buffer[key] = current.ToString();

            Buffer[key] = GUILayout.TextField(Buffer[key], GUILayout.Width(c.S(100f)));

            if (GUILayout.Button("Set", GUILayout.Width(c.S(46f))))
            {
                long parsed;
                if (long.TryParse(Buffer[key], out parsed))
                    apply(parsed);
                else
                    Buffer[key] = current.ToString();
            }

            if (GUILayout.Button("-10", GUILayout.Width(c.S(40f))))
            {
                apply(current - 10);
                Buffer.Remove(key);
            }

            if (GUILayout.Button("+10", GUILayout.Width(c.S(40f))))
            {
                apply(current + 10);
                Buffer.Remove(key);
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Slot counts get their own row: they are add-only, so a decrement button would be a lie.
        /// </summary>
        private static void Slots(
            PanelContext c, string label, int current, int softCap,
            Func<int, bool> setTo, Action<int> add)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, c.Body, GUILayout.Width(c.S(140f)));
            GUILayout.Label(
                current + (current > softCap ? " (past cap)" : string.Empty),
                current > softCap ? c.Highlight : c.Body,
                GUILayout.Width(c.S(90f)));

            string key = "slot:" + label;
            if (!Buffer.ContainsKey(key))
                Buffer[key] = current.ToString();

            Buffer[key] = GUILayout.TextField(Buffer[key], GUILayout.Width(c.S(100f)));

            if (GUILayout.Button("Set", GUILayout.Width(c.S(46f))))
            {
                int parsed;
                if (int.TryParse(Buffer[key], out parsed))
                    setTo(parsed);
                Buffer.Remove(key);
            }

            if (GUILayout.Button("+1", GUILayout.Width(c.S(40f))))
            {
                add(1);
                Buffer.Remove(key);
            }

            GUILayout.EndHorizontal();
        }
    }

    /// <summary>The Give tab: search anything givable and hand it to the player.</summary>
    internal static class GiveTab
    {
        private static readonly string[] ModeNames = { "Items", "Consumables", "Blueprints" };

        private static int _mode;
        private static string _search = string.Empty;
        private static Vector2 _scroll;
        private static string _result = string.Empty;

        internal static void Register() => PanelTabs.Register("Give", Draw);

        private static void Draw(PanelContext c)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Given items are saved with the run", c.Header);
            GUILayout.Label(
                "Stored by GameTag in GameState.save. Vanilla tags, so an unmodded game still "
                + "reads the save - but an item given here is permanent for this run.",
                c.Muted);
            GUILayout.EndVertical();

            GUILayout.Space(c.S(4f));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", c.Body, GUILayout.Width(c.S(48f)));
            _search = GUILayout.TextField(_search, GUILayout.Width(c.S(180f)));

            int wanted = GUILayout.Toolbar(_mode, ModeNames, GUILayout.Width(c.S(240f)));
            if (wanted != _mode)
            {
                _mode = wanted;
                _result = string.Empty;
            }
            GUILayout.EndHorizontal();

            if (_result.Length > 0)
                GUILayout.Label(_result, c.Body);

            List<GameTag> tags = TagsForMode();
            if (tags.Count == 0)
            {
                GUILayout.Label("Nothing available. Start or continue a run first.", c.Muted);
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll, GUI.skin.box, GUILayout.Height(c.S(340f)));

            int shown = 0;
            foreach (GameTag tag in tags)
            {
                string name = tag.ToString();
                if (_search.Length > 0 && name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string reason;
                bool canGive = Inventory.CanGive(tag, out reason);

                GUILayout.BeginHorizontal();
                GUILayout.Label(name, c.Body, GUILayout.Width(c.S(220f)));

                GUI.enabled = canGive;
                if (GUILayout.Button("Give", GUILayout.Width(c.S(60f))))
                {
                    string why;
                    _result = Inventory.Give(tag, out why)
                        ? "Gave " + tag + "."
                        : "Could not give " + tag + ": " + why;
                }
                GUI.enabled = true;

                if (!canGive)
                    GUILayout.Label(reason, c.Muted);

                GUILayout.EndHorizontal();
                shown++;
            }

            GUILayout.EndScrollView();
            GUILayout.Label(shown + " of " + tags.Count + " shown", c.Muted);
        }

        private static List<GameTag> TagsForMode()
        {
            switch (_mode)
            {
                case 0: return Inventory.GetAllItemTags();
                case 1: return Inventory.GetAllConsumableTags();
                // A building handed to the consumables panel arrives as a blueprint card.
                default: return ComboModApi.GetTunableTags(isItem: false);
            }
        }
    }
}
