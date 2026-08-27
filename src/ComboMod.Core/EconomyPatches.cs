using GameState;
using GameState.Data;
using HarmonyLib;
using UnityEngine;

namespace ComboMod
{
    /// <summary>
    /// Harmony patches that route the game's hardcoded economy tables through
    /// <see cref="Economy"/>.
    /// <para>
    /// These are prefixes returning <c>false</c> rather than postfixes adjusting a result,
    /// because the vanilla methods are switch expressions with no state to nudge — replacing the
    /// answer is both simpler and exact. Every one of them falls through to the original when no
    /// override is set, so an unmodified install behaves identically.
    /// </para>
    /// </summary>
    internal static class EconomyPatches
    {
        /// <summary>
        /// Building draft weight by rarity. Vanilla: 0.70 / 0.24 / 0.05 / 0.01, and 0 for
        /// Legendary, which is why Legendary buildings never appear in a draft.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(RarityLookup), nameof(RarityLookup.GetDefaultBuildingRollChanceForRarity))]
        internal static bool BuildingRollChance(Rarity rarity, ref float __result)
        {
            if (!Economy.AnyOverrides)
                return true;

            float weight;
            if (!Economy.TryBuildingWeight(rarity, out weight))
                return true;

            __result = weight;
            return false;
        }

        /// <summary>Item draft weight by rarity. Vanilla: 0.60 / 0.30 / 0.10.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(RarityLookup), nameof(RarityLookup.GetDefaultItemRollChanceForRarity))]
        internal static bool ItemRollChance(Rarity rarity, ref float __result)
        {
            if (!Economy.AnyOverrides)
                return true;

            float weight;
            if (!Economy.TryItemWeight(rarity, out weight))
                return true;

            __result = weight;
            return false;
        }

        /// <summary>
        /// How rarity weights drift as the city grows. Vanilla shifts Common down 3% per size
        /// step and Rare/Masterwork up 15%, which is what makes late milestones feel richer.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(RarityLookup), nameof(RarityLookup.GetRarityMilestoneMultiplier))]
        internal static bool MilestoneMultiplier(Rarity rarity, Milestone milestone, ref float __result)
        {
            if (!Economy.AnyOverrides)
                return true;

            if (Economy.RaritySuffix(rarity) == null)
                return true;

            // Same shape as vanilla: one step per city size above the first, clamped at 10.
            int steps = Mathf.Clamp(milestone.CitySizeAsInt() - 1, 0, 10);
            __result = 1f + Economy.DriftFor(rarity) * steps;
            return false;
        }

        /// <summary>Flat blueprint price. Vanilla returns 4 regardless of building or milestone.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DropChancesAndPrices), nameof(DropChancesAndPrices.GetPriceForBlueprint))]
        internal static bool BlueprintPrice(ref int __result)
        {
            if (!Economy.AnyOverrides)
                return true;

            __result = Mathf.Max(0, Mathf.RoundToInt(Economy.Get("blueprintprice")));
            return false;
        }

        /// <summary>Sale value. Vanilla is floor(buyPrice / 2).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DropChancesAndPrices), nameof(DropChancesAndPrices.SellPrice))]
        internal static bool SellPrice(int buyPrice, ref int __result)
        {
            if (!Economy.AnyOverrides)
                return true;

            __result = Mathf.Max(0, Mathf.FloorToInt(buyPrice * Economy.Get("sellratio")));
            return false;
        }

        // --- shop card weights ---
        //
        // These are private static get-only properties on DropChancesAndPrices, fed into a
        // ChooseBag inside GetRandomShopCard. Patching the property getters is what lets the
        // weights change without reimplementing the card-selection logic, which reads unlock
        // state and would be easy to get subtly wrong.

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DropChancesAndPrices), "get_HeirloomCardChance")]
        internal static bool HeirloomCardChance(ref float __result) => ShopWeight("shop.heirloom", ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DropChancesAndPrices), "get_FavourCardChance")]
        internal static bool FavourCardChance(ref float __result) => ShopWeight("shop.favour", ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DropChancesAndPrices), "get_BlueprintCardChance")]
        internal static bool BlueprintCardChance(ref float __result) => ShopWeight("shop.blueprint", ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DropChancesAndPrices), "get_BuildingSupplyCardChance")]
        internal static bool SupplyCardChance(ref float __result) => ShopWeight("shop.supply", ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DropChancesAndPrices), "get_SpellCardChance")]
        internal static bool SpellCardChance(ref float __result) => ShopWeight("shop.spell", ref __result);

        private static bool ShopWeight(string key, ref float result)
        {
            if (!Economy.AnyOverrides)
                return true;

            // ChooseBag normalises, so these are relative. A zero here is safe in a way a zero
            // building roll weight is not: the bag is rebuilt per shop visit rather than having
            // entries removed from it.
            result = Mathf.Max(0f, Economy.Get(key));
            return false;
        }
    }
}
