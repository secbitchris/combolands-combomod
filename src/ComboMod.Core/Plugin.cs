using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using BuildingBehavioursMap = Entities.BuildingBehaviours.BuildingBehaviours;
using ItemBehavioursMap = Entities.ItemBehaviours.ItemBehaviours;

namespace ComboMod
{
    /// <summary>
    /// ComboMod.Core: a save-safe rebalancing framework for Combolands.
    /// <para>
    /// The whole framework is two Harmony postfixes. The game rebuilds its behaviour
    /// dictionaries on every scene load, so registered tunes are re-applied at exactly that
    /// point rather than once at startup.
    /// </para>
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.combolands.combomod.core";
        public const string PluginName = "ComboMod Core";
        public const string PluginVersion = "0.1.0";

        internal static Plugin Instance { get; private set; }

        private ConfigEntry<bool> _refuseOnVersionMismatch;
        private ConfigEntry<bool> _suppressAchievements;
        private ConfigEntry<KeyCode> _panelKey;
        private ConfigEntry<KeyCode> _abKey;
        private ConfigEntry<float> _uiScale;
        private Harmony _harmony;
        private bool _guardPending;

        private void Awake()
        {
            Instance = this;
            ComboModApi.Log = Logger;

            _refuseOnVersionMismatch = Config.Bind(
                "Safety", "RefuseOnVersionMismatch", false,
                "Refuse to patch when Assembly-CSharp.dll does not match the audited build. " +
                "Off by default: a mismatch usually still works, and any field that genuinely " +
                "moved is reported by name at tune time.");

            _suppressAchievements = Config.Bind(
                "Safety", "SuppressAchievements", true,
                "Stop Steam achievements unlocking while tunes are active. Leave this on " +
                "unless you specifically want a modded run to count.");

            if (!SafetyGate.Verify(Logger, _refuseOnVersionMismatch.Value))
            {
                Logger.LogError(PluginName + " is inactive. Set RefuseOnVersionMismatch to false to override.");
                return;
            }

            _panelKey = Config.Bind(
                "UI", "PanelKey", KeyCode.F6,
                "Key that opens the ComboMod panel, where tunes can be switched on and off live.");

            _abKey = Config.Bind(
                "UI", "AbToggleKey", KeyCode.F7,
                "Key that flips every tune on or off at once, for comparing against vanilla.");

            _uiScale = Config.Bind(
                "UI", "Scale", 0f,
                "Panel size multiplier. 0 means derive it from screen height, which is usually "
                + "what you want: IMGUI draws at a fixed pixel size, so a panel sized for 1080p is "
                + "unreadably small on 1440p or 4K. Any other value pins it. Adjustable in the "
                + "panel, and changing it there writes the resolved number back here.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(BehaviourInitPatches));

            // Lives on the plugin's own GameObject, which BepInEx already keeps across scenes.
            var panel = gameObject.AddComponent<ModPanel>();
            panel.ToggleKey = _panelKey.Value;
            panel.AbToggleKey = _abKey.Value;
            // Leave 0 as-is: the panel resolves it on first paint, because Screen.height is not
            // meaningful yet during chainloader startup.
            panel.UiScale = _uiScale.Value > 0f
                ? Mathf.Clamp(_uiScale.Value, ModPanel.MinScale, ModPanel.MaxScale)
                : ModPanel.AutoScale;

            Logger.LogInfo(_uiScale.Value > 0f
                ? "Panel UI scale pinned to " + panel.UiScale.ToString("0.00")
                : "Panel UI scale will be derived from screen height on first open.");

            // Persist immediately so a scale chosen in game survives the next launch.
            panel.OnScaleChanged = scale =>
            {
                _uiScale.Value = scale;
                Config.Save();
            };

            // After Harmony is in place, so the first apply happens through the normal path.
            PackLoader.LoadAll();

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. " + _panelKey.Value + " = panel, " + _abKey.Value + " = toggle all tunes.");
        }

        private void Update()
        {
            // AchievementsHandler is a scene singleton that enables itself in Start(), so the
            // swap cannot happen during Awake. Retry until the handler exists, then stop.
            if (!_guardPending || !_suppressAchievements.Value)
                return;

            if (AchievementGuard.Engage(Logger))
                _guardPending = false;
        }

        /// <summary>Called by the postfix once behaviours have been rebuilt and tunes applied.</summary>
        internal void OnTunesApplied()
        {
            if (_suppressAchievements.Value && ComboModApi.AnyTunesRegistered && !AchievementGuard.IsEngaged)
                _guardPending = true;
        }
    }

    /// <summary>
    /// The two hooks the framework needs.
    /// <para>
    /// BehavioursController.Awake calls both Init methods, discarding and rebuilding the
    /// dictionaries. Anything written into them before that point is lost, which is why a
    /// one-shot registration at plugin startup would appear to work and then quietly stop.
    /// </para>
    /// </summary>
    internal static class BehaviourInitPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildingBehavioursMap), nameof(BuildingBehavioursMap.InitBuildingBehaviours))]
        internal static void AfterBuildingInit()
        {
            Guarded(ComboModApi.ApplyBuildings);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemBehavioursMap), nameof(ItemBehavioursMap.InitItemBehaviours))]
        internal static void AfterItemInit()
        {
            Guarded(ComboModApi.ApplyItems);
        }

        private static void Guarded(Action apply)
        {
            try
            {
                apply();
                Plugin.Instance?.OnTunesApplied();
            }
            catch (Exception ex)
            {
                // Throwing out of a postfix would take the game's init path down with it.
                ComboModApi.Log?.LogError("Applying tunes failed: " + ex);
            }
        }
    }
}
