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
    /// <para>
    /// Headless by design: this assembly loads packs and applies tunes with no UI. The in-game
    /// editor lives in ComboMod.Editor, so installing a balance pack does not force a cheat
    /// panel on someone who only wanted the balance.
    /// </para>
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.combolands.combomod.core";
        public const string PluginName = "ComboMod Core";
        public const string PluginVersion = "0.2.0";

        internal static Plugin Instance { get; private set; }

        private ConfigEntry<bool> _refuseOnVersionMismatch;
        private ConfigEntry<bool> _suppressAchievements;
        private Harmony _harmony;

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

            _harmony = new Harmony(PluginGuid);

            // Each group patched separately and guarded: a bad patch target should cost that
            // feature, not the whole mod. An ambiguous overload in one diagnostic probe
            // previously threw out of Awake and silently disabled everything after it.
            ApplyPatches("behaviour hooks", typeof(BehaviourInitPatches));
            ApplyPatches("economy", typeof(EconomyPatches));

            PerformancePatches.Enabled = Config.Bind(
                "Performance", "OptimiseScoring", true,
                "Replace two hot paths in the game with provably equivalent versions. "
                + "UpdateSumOnScreen computes a private field with no readers anywhere, once per "
                + "scorer tick over every live scorer, which is quadratic during a big combo. "
                + "ProcessTrigger walks a dictionary comparing keys instead of looking one up. "
                + "Both matter only on large boards. Turn off if you suspect them of anything.").Value;

            ApplyPatches("performance", typeof(PerformancePatches));

            LoadPatches.Enabled = Config.Bind(
                "Performance", "FastMapLoad", true,
                "Collapse the redundant spatial-index rebuilds that happen while a map is being "
                + "populated. The game rebuilds the whole index once per building placed, then "
                + "rebuilds it again at the end of the load anyway - so the intermediate ones are "
                + "discarded work. Measured at 33 seconds on a full board.").Value;

            if (LoadPatches.Enabled)
                ApplyPatches("fast map load", typeof(LoadPatches));

            Profiler.Enabled = Config.Bind(
                "Performance", "Profile", false,
                "Log where frame time goes during play, every 5 seconds. Diagnostic only: it "
                + "adds a timestamp pair per call on several hot paths. Turn on when chasing a "
                + "slowdown, off otherwise.").Value;

            if (Profiler.Enabled)
            {
                if (ApplyPatches("profiler", typeof(ProfilerPatches)))
                    Logger.LogWarning("Profiler ON - reports every 5s. Turn off Performance.Profile when done.");
                else
                    Profiler.Enabled = false;
            }
            Logger.LogInfo("Scoring optimisations " + (PerformancePatches.Enabled ? "enabled" : "disabled") + ".");

            // After Harmony is in place, so the first apply happens through the normal path.
            PackLoader.LoadAll();

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. "
                           + ComboModApi.Registrations.Count + " tune(s) registered.");
        }

        /// <summary>
        /// Patch one group, reporting rather than throwing. Returns whether it succeeded.
        /// </summary>
        private bool ApplyPatches(string label, Type container)
        {
            try
            {
                _harmony.PatchAll(container);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not apply " + label + " patches; that feature is off. " + ex.Message);
                return false;
            }
        }

        private void LateUpdate()
        {
            if (Profiler.Enabled)
                Profiler.Frame(Time.unscaledDeltaTime * 1000f);

            // One cache walk per frame at most, however many tunes were applied.
            ComboModApi.FlushCacheInvalidation();
        }

        private void Update()
        {
            if (!_suppressAchievements.Value || !ComboModApi.AnyTunesRegistered)
                return;

            // Re-arm whenever the guard is not currently in force. A scene change destroys the
            // AchievementsHandler and builds a new one holding the real Steam platform, so
            // engaging once at startup is not enough -- it has to be rechecked or achievements
            // silently come back after the first trip to the menu.
            if (!AchievementGuard.IsEngaged)
                AchievementGuard.Engage(Logger);
        }

        /// <summary>Called by the postfix once behaviours have been rebuilt and tunes applied.</summary>
        internal void OnTunesApplied()
        {
            // Arming is handled continuously in Update; this remains as a stable hook point.
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
