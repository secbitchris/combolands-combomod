using Entities;
using GameState;
using HarmonyLib;
using UI;
using BuildingBehavioursMap = Entities.BuildingBehaviours.BuildingBehaviours;
using ItemBehavioursMap = Entities.ItemBehaviours.ItemBehaviours;

namespace ComboMod
{
    /// <summary>
    /// Timing probes on everything plausibly hot during a scoring cascade.
    /// <para>
    /// Deliberately broad rather than aimed: the point is to let the numbers pick the culprit
    /// instead of picking one first and measuring only that. Each probe is a prefix that stamps
    /// a timestamp into <c>__state</c> and a postfix that records the delta.
    /// </para>
    /// <para>
    /// The set covers three different hypotheses at once — arithmetic in trigger dispatch, work
    /// proportional to board size in the behaviour helpers, and GameObject churn from spawning a
    /// scorer per scoring piece. Whichever dominates will be obvious in the totals.
    /// </para>
    /// </summary>
    internal static class ProfilerPatches
    {
        // --- trigger dispatch ---

        [HarmonyPatch(typeof(BuildingBehavioursMap), nameof(BuildingBehavioursMap.ProcessTrigger))]
        [HarmonyPrefix]
        internal static void BuildingTriggerPre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(BuildingBehavioursMap), nameof(BuildingBehavioursMap.ProcessTrigger))]
        [HarmonyPostfix]
        internal static void BuildingTriggerPost(long __state) => Profiler.Record("Building.ProcessTrigger", __state);

        [HarmonyPatch(typeof(ItemBehavioursMap), nameof(ItemBehavioursMap.ProcessTrigger))]
        [HarmonyPrefix]
        internal static void ItemTriggerPre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(ItemBehavioursMap), nameof(ItemBehavioursMap.ProcessTrigger))]
        [HarmonyPostfix]
        internal static void ItemTriggerPost(long __state) => Profiler.Record("Item.ProcessTrigger", __state);

        // --- scoring, including the per-piece GameObject spawn ---

        [HarmonyPatch(typeof(ScoreController), nameof(ScoreController.ScorePoints))]
        [HarmonyPrefix]
        internal static void ScorePointsPre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(ScoreController), nameof(ScoreController.ScorePoints))]
        [HarmonyPostfix]
        internal static void ScorePointsPost(long __state) => Profiler.Record("ScoreController.ScorePoints", __state);

        /// <summary>Counts scorer prefabs created — one per scoring piece, each a live GameObject.</summary>
        [HarmonyPatch(typeof(PointsScorer), nameof(PointsScorer.Initialize))]
        [HarmonyPostfix]
        internal static void ScorerCreated() => Profiler.Count("PointsScorer spawned");

        [HarmonyPatch(typeof(PointsScorer), nameof(PointsScorer.ExecuteScoring))]
        [HarmonyPrefix]
        internal static void ExecuteScoringPre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(PointsScorer), nameof(PointsScorer.ExecuteScoring))]
        [HarmonyPostfix]
        internal static void ExecuteScoringPost(long __state) => Profiler.Record("PointsScorer.ExecuteScoring", __state);

        // --- helpers that walk every building, called from inside behaviours ---

        [HarmonyPatch(typeof(_GamePieceBehaviour), "CurrentTagCount")]
        [HarmonyPrefix]
        internal static void TagCountPre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(_GamePieceBehaviour), "CurrentTagCount")]
        [HarmonyPostfix]
        internal static void TagCountPost(long __state) => Profiler.Record("CurrentTagCount (all buildings)", __state);

        [HarmonyPatch(typeof(_GamePieceBehaviour), "CurrentCategoryCount")]
        [HarmonyPrefix]
        internal static void CategoryCountPre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(_GamePieceBehaviour), "CurrentCategoryCount")]
        [HarmonyPostfix]
        internal static void CategoryCountPost(long __state) => Profiler.Record("CurrentCategoryCount (all buildings)", __state);

        // --- queue and cache ---

        [HarmonyPatch(typeof(TriggerQueue), nameof(TriggerQueue.AddTriggerToQueue))]
        [HarmonyPostfix]
        internal static void TriggerQueued() => Profiler.Count("TriggerQueue.AddTriggerToQueue");

        [HarmonyPatch(typeof(BuildingExtensions), nameof(BuildingExtensions.ResetCaches))]
        [HarmonyPrefix]
        internal static void ResetCachesPre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(BuildingExtensions), nameof(BuildingExtensions.ResetCaches))]
        [HarmonyPostfix]
        internal static void ResetCachesPost(long __state) => Profiler.Record("BuildingExtensions.ResetCaches", __state);

        // --- the decisive probe ---
        //
        // TriggerQueue.Update drives the whole cascade. If the stall lands inside it, the cost is
        // game logic and worth chasing further. If frames spike while this stays cheap, the stall
        // is outside managed game code entirely -- rendering, asset loading or engine-side
        // instantiation -- and no amount of patching game methods will touch it.

        [HarmonyPatch(typeof(TriggerQueue), "Update")]
        [HarmonyPrefix]
        internal static void QueueUpdatePre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(TriggerQueue), "Update")]
        [HarmonyPostfix]
        internal static void QueueUpdatePost(long __state) => Profiler.Record("TriggerQueue.Update", __state);

        /// <summary>Spawns a SpecialResourceScorer prefab on every call.</summary>
        [HarmonyPatch(typeof(_GamePieceBehaviour), nameof(_GamePieceBehaviour.ScoreSpecialResource))]
        [HarmonyPrefix]
        internal static void SpecialResourcePre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(_GamePieceBehaviour), nameof(_GamePieceBehaviour.ScoreSpecialResource))]
        [HarmonyPostfix]
        internal static void SpecialResourcePost(long __state) => Profiler.Record("ScoreSpecialResource (spawns)", __state);

        // Building creation mid-cascade, which instantiates a full building prefab.
        //
        // The argument types are mandatory here: this method has two overloads (Tile, and x/y),
        // and patching by name alone throws AmbiguousMatchException -- which took the whole
        // plugin's Awake down with it when this probe was first added.
        [HarmonyPatch(typeof(BuildingController), nameof(BuildingController.InstantiateAndBuildBuildingAt),
            new[] { typeof(GameTag), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(GamePiece) })]
        [HarmonyPrefix]
        internal static void BuildPre(out long __state) => __state = Profiler.Now();

        [HarmonyPatch(typeof(BuildingController), nameof(BuildingController.InstantiateAndBuildBuildingAt),
            new[] { typeof(GameTag), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(GamePiece) })]
        [HarmonyPostfix]
        internal static void BuildPost(long __state) => Profiler.Record("InstantiateAndBuildBuildingAt", __state);
    }
}
