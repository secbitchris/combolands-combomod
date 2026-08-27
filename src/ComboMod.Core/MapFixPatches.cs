using System;
using System.Collections.Generic;
using Entities;
using Entities.Data;
using Environment;
using Framework;
using HarmonyLib;
using Library;
using UnityEngine;

namespace ComboMod
{
    /// <summary>
    /// Fixes a vanilla bug that permanently deletes plateau and canal ground from a run, one
    /// save/load cycle at a time.
    /// <para>
    /// A covered plateau is not a building. When something is built on top of one,
    /// <c>BuildBuildingAt</c> removes the Plateau building and leaves only a sprite, tracked in
    /// <c>MapRenderer._plateauTiles</c> — and that dictionary's keys are what
    /// <c>StateSerializer</c> writes to the save as <c>PlateauCoords</c>. Canals work the same
    /// way through <c>_canalTiles</c> / <c>CanalCoords</c>.
    /// </para>
    /// <para>
    /// The bug is an ordering error in <c>MapController.Start</c>:
    /// <c>InitializeGridFromSave</c> faithfully recreates every sprite from the save, and then
    /// <c>InitializeTilemaps</c> replaces both dictionaries with fresh empty ones. The sprites
    /// are orphaned but keep rendering, so the session <i>looks</i> right — the loss lands at
    /// the next save, which writes only what was placed since the load. Load that save and the
    /// ground is gone: buildings stand on open water, and removing one no longer gives the
    /// plateau back (the <c>RemoveBuilding</c> restore path checks the same dictionary).
    /// </para>
    /// <para>
    /// Two patches. The first preserves the dictionaries across <c>InitializeTilemaps</c> —
    /// safe because <c>MapRenderer</c> is a per-scene singleton (no <c>DontDestroyOnLoad</c>)
    /// and <c>InitializeTilemaps</c> has exactly one call site, directly after the grid is
    /// initialized, so a non-null dictionary can only hold sprites created moments earlier in
    /// the same <c>Start</c>. The second heals saves the bug already damaged: terraforming is
    /// recoverable from the tiles themselves, because <c>SetTileToGrass</c> /
    /// <c>SetTileToOcean</c> are called from exactly one place each (the plateau and canal
    /// branches of <c>BuildBuildingAt</c>) and stamp <c>PrevType</c> — so Grass over a sea
    /// <c>PrevType</c> is a plateau, and Ocean with a non-sea <c>PrevType</c> is a canal.
    /// </para>
    /// </summary>
    internal static class MapFixPatches
    {
        /// <summary>Set from config, so the whole thing can be switched off.</summary>
        internal static bool Enabled = true;

        private static readonly AccessTools.FieldRef<MapRenderer, Dictionary<Vector2Int, SpriteRenderer>>
            PlateauTilesRef = AccessTools.FieldRefAccess<MapRenderer, Dictionary<Vector2Int, SpriteRenderer>>("_plateauTiles");

        private static readonly AccessTools.FieldRef<MapRenderer, Dictionary<Vector2Int, SpriteRenderer>>
            CanalTilesRef = AccessTools.FieldRefAccess<MapRenderer, Dictionary<Vector2Int, SpriteRenderer>>("_canalTiles");

        internal sealed class SavedDicts
        {
            public Dictionary<Vector2Int, SpriteRenderer> Plateaus;
            public Dictionary<Vector2Int, SpriteRenderer> Canals;
        }

        // --- patch 1: stop InitializeTilemaps discarding populated dictionaries ---

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MapRenderer), nameof(MapRenderer.InitializeTilemaps))]
        internal static void CaptureBeforeInit(MapRenderer __instance, out SavedDicts __state)
        {
            __state = Enabled
                ? new SavedDicts { Plateaus = PlateauTilesRef(__instance), Canals = CanalTilesRef(__instance) }
                : null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MapRenderer), nameof(MapRenderer.InitializeTilemaps))]
        internal static void RestoreAfterInit(MapRenderer __instance, SavedDicts __state)
        {
            if (__state == null)
                return;

            // Null means a fresh scene with nothing restored yet: keep the new empty
            // dictionary, which is exactly what vanilla intended.
            if (__state.Plateaus != null)
                PlateauTilesRef(__instance) = __state.Plateaus;
            if (__state.Canals != null)
                CanalTilesRef(__instance) = __state.Canals;
        }

        // --- patch 2: rebuild sprites a damaged save no longer records ---

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MapController), "InitializeGridFromSave")]
        internal static void HealAfterLoad(MapController __instance)
        {
            if (!Enabled)
                return;

            try
            {
                var renderer = MonoSingleton<MapRenderer>.Instance;
                Sprite canalSprite = null;
                int plateaus = 0, canals = 0;

                for (int x = 0; x < __instance.Grid.Width; x++)
                {
                    for (int y = 0; y < __instance.Grid.Height; y++)
                    {
                        Tile tile = __instance.Grid[x, y];

                        // Only SetTileToGrass / SetTileToOcean ever write PrevType, so
                        // PrevType == None means the tile was never terraformed.
                        if (tile.PrevType == TileType.None)
                            continue;

                        bool prevWasSea = tile.PrevType == TileType.Sand
                                          || tile.PrevType == TileType.Shore
                                          || tile.PrevType == TileType.Ocean;

                        if (tile.Type == TileType.Grass && prevWasSea)
                        {
                            // An uncovered plateau is a real building and owns its own visual.
                            if (!tile.IsEmpty && tile.Building != null && tile.Building.Tag == GameTag.Plateau)
                                continue;
                            if (renderer.PlateauTiles == null
                                || !renderer.PlateauTiles.ContainsKey(new Vector2Int(x, y)))
                            {
                                __instance.CreatePlateauSprite(x, y);
                                plateaus++;
                            }
                        }
                        else if (tile.Type == TileType.Ocean && !prevWasSea)
                        {
                            if (!tile.IsEmpty && tile.Building != null && tile.Building.Tag == GameTag.Canal)
                                continue;
                            if (renderer.CanalTiles == null
                                || !renderer.CanalTiles.ContainsKey(new Vector2Int(x, y)))
                            {
                                if (canalSprite == null)
                                    canalSprite = ScriptableObjectSingleton<GamePieceDataHolder>.Instance
                                        .GetBuildingDataFor(GameTag.Canal).Sprite;
                                __instance.CreateCanalSprite(x, y, canalSprite);
                                canals++;
                            }
                        }
                    }
                }

                if (plateaus + canals > 0)
                    ComboModApi.Log?.LogWarning(
                        "Rebuilt " + plateaus + " plateau and " + canals
                        + " canal sprite(s) the save no longer recorded (vanilla save/load bug).");
            }
            catch (Exception ex)
            {
                // Healing is best-effort; a failure here must not take the load path down.
                ComboModApi.Log?.LogError("Plateau/canal heal failed: " + ex);
            }
        }
    }
}
