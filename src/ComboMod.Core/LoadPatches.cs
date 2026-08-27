using Entities;
using Environment;
using HarmonyLib;
using Library;

namespace ComboMod
{
    /// <summary>
    /// Collapses the redundant spatial-index rebuilds that happen while a map is being built.
    /// <para>
    /// <c>BuildingController.BuildBuildingAt</c> ends by calling <c>RefreshBuildings</c>, which
    /// throws away <c>_buildingsTilesRangeIndex</c> and reconstructs it by walking every placed
    /// building and every tile in its range. That is reasonable when one building is added to a
    /// settled board. It is not reasonable while a board is being populated from a save, because
    /// it happens once per building: loading 640 buildings performs 640 full rebuilds of an index
    /// that grows as it goes.
    /// </para>
    /// <para>
    /// Measured on a 1,188-building board: 640 calls totalling <b>33 seconds</b>, which is the
    /// entire load time.
    /// </para>
    /// <para>
    /// The reason this is safe rather than merely faster: <c>InitializeGridFromSave</c> already
    /// finishes with its own <c>RefreshBuildings(GameTag.None)</c> call. Every intermediate
    /// rebuild is superseded by that final one, so suppressing them and performing a single
    /// rebuild at the end produces the same index the game would have produced anyway. Nothing
    /// reads the index during the load — no triggers run, and the buildings are placed with
    /// <c>triggerOnBuild: false</c>.
    /// </para>
    /// </summary>
    internal static class LoadPatches
    {
        /// <summary>Set from config, so the whole thing can be switched off.</summary>
        internal static bool Enabled = true;

        /// <summary>True while a map is being populated and rebuilds should be skipped.</summary>
        private static bool _populating;

        /// <summary>How many rebuilds were skipped, for the log.</summary>
        private static int _skipped;

        // --- suppress during the two map-population paths ---

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MapController), "InitializeGridFromSave")]
        internal static void LoadSavePre() => Begin();

        // Finalizer, not postfix: a postfix is skipped when the original method throws, which
        // would leave suppression latched on and silently disable every index rebuild for the
        // rest of the session. A finalizer runs either way.
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(MapController), "InitializeGridFromSave")]
        internal static void LoadSaveDone() => End("save load");

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MapController), "InitializeGridFromGeneratedMap")]
        internal static void NewMapPre() => Begin();

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(MapController), "InitializeGridFromGeneratedMap")]
        internal static void NewMapDone() => End("map generation");

        /// <summary>
        /// Skip the rebuild while populating. Returning false skips the original entirely; the
        /// single rebuild in <see cref="End"/> replaces all of them.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildingController), nameof(BuildingController.RefreshBuildings))]
        internal static bool SkipRefreshWhilePopulating()
        {
            if (!Enabled || !_populating)
                return true;

            _skipped++;
            return false;
        }

        private static void Begin()
        {
            if (!Enabled)
                return;

            _populating = true;
            _skipped = 0;
        }

        private static void End(string what)
        {
            if (!_populating)
                return;

            // Cleared first and unconditionally. If anything below throws, suppression must
            // still be off -- a stuck flag is far worse than a missed log line.
            _populating = false;

            // Do the one rebuild the whole load actually needed. Must happen after _populating
            // is cleared, or the prefix above would skip this call too.
            if (MonoSingleton<BuildingController>.HasInstance)
                MonoSingleton<BuildingController>.Instance.RefreshBuildings(GameTag.None);

            if (_skipped > 0)
                ComboModApi.Log?.LogInfo(
                    "Collapsed " + _skipped + " redundant index rebuilds during " + what + " into one.");

            _skipped = 0;
        }
    }
}
