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

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(BehaviourInitPatches));
            _harmony.PatchAll(typeof(EconomyPatches));

            // After Harmony is in place, so the first apply happens through the normal path.
            PackLoader.LoadAll();

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. "
                           + ComboModApi.Registrations.Count + " tune(s) registered.");
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
