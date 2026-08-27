using System;
using System.Collections.Generic;
using System.Globalization;
using GameState.Data;

namespace ComboMod
{
    /// <summary>
    /// Global economy settings: draft weights, shop composition and prices.
    /// <para>
    /// Unlike base stats, these live in <c>static</c> classes with hardcoded switch expressions
    /// (<c>RarityLookup</c>, <c>DropChancesAndPrices</c>), so there is no field to reflect on —
    /// they are changed with Harmony patches that consult this table instead.
    /// </para>
    /// <para>
    /// Still Tier 1. Nothing here is serialized: draft weights and prices are recomputed from
    /// code every time they are asked for, so removing ComboMod restores vanilla exactly and a
    /// save written with an economy pack loaded opens fine without it.
    /// </para>
    /// </summary>
    public static class Economy
    {
        /// <summary>One overridable economy value: its key in a pack, and vanilla's number.</summary>
        public sealed class Setting
        {
            public readonly string Key;
            public readonly float Vanilla;
            public readonly string Description;

            internal Setting(string key, float vanilla, string description)
            {
                Key = key;
                Vanilla = vanilla;
                Description = description;
            }
        }

        /// <summary>
        /// Every economy value a pack can set, with the shipped value for reference.
        /// <para>
        /// Note <c>draft.legendary</c> and <c>item.legendary</c> are 0 in vanilla — Legendary
        /// falls through to the default case in both roll tables, so the buildings tagged
        /// Legendary can never be drafted. Giving it a nonzero weight is the single highest
        /// impact change available here.
        /// </para>
        /// </summary>
        public static readonly Setting[] Settings =
        {
            new Setting("draft.common",      0.70f, "Building draft weight, Common"),
            new Setting("draft.uncommon",    0.24f, "Building draft weight, Uncommon"),
            new Setting("draft.rare",        0.05f, "Building draft weight, Rare"),
            new Setting("draft.masterwork",  0.01f, "Building draft weight, Masterwork"),
            new Setting("draft.legendary",   0.00f, "Building draft weight, Legendary (never rolls in vanilla)"),

            new Setting("item.common",       0.60f, "Item draft weight, Common"),
            new Setting("item.uncommon",     0.30f, "Item draft weight, Uncommon"),
            new Setting("item.rare",         0.10f, "Item draft weight, Rare"),
            new Setting("item.masterwork",   0.00f, "Item draft weight, Masterwork (never rolls in vanilla)"),
            new Setting("item.legendary",    0.00f, "Item draft weight, Legendary (never rolls in vanilla)"),

            new Setting("drift.common",     -0.03f, "Per-city-size change to Common weight"),
            new Setting("drift.uncommon",    0.05f, "Per-city-size change to Uncommon weight"),
            new Setting("drift.rare",        0.15f, "Per-city-size change to Rare weight"),
            new Setting("drift.masterwork",  0.15f, "Per-city-size change to Masterwork weight"),
            new Setting("drift.legendary",   0.00f, "Per-city-size change to Legendary weight"),

            new Setting("shop.heirloom",    20f,    "Shop card weight, heirlooms"),
            new Setting("shop.favour",       6f,    "Shop card weight, council favours"),
            new Setting("shop.blueprint",    4f,    "Shop card weight, blueprints"),
            new Setting("shop.supply",       4f,    "Shop card weight, building supplies"),
            new Setting("shop.spell",        2f,    "Shop card weight, potions"),

            new Setting("blueprintprice",    4f,    "Flat blueprint price, ignores building and milestone"),
            new Setting("sellratio",         0.5f,  "Fraction of buy price returned on sale"),
        };

        private static readonly Dictionary<string, float> Values = new Dictionary<string, float>();

        /// <summary>True when any economy value differs from vanilla.</summary>
        public static bool AnyOverrides => Values.Count > 0;

        /// <summary>Overrides currently in force, by key.</summary>
        public static IReadOnlyDictionary<string, float> Overrides => Values;

        /// <summary>Look a setting up by its pack key, or null.</summary>
        public static Setting Find(string key)
        {
            foreach (Setting setting in Settings)
                if (string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase))
                    return setting;
            return null;
        }

        /// <summary>The value in force: an override if set, otherwise vanilla's.</summary>
        public static float Get(string key)
        {
            float value;
            if (Values.TryGetValue(key, out value))
                return value;

            Setting setting = Find(key);
            return setting?.Vanilla ?? 0f;
        }

        // Who last set each key, so a second pack touching it can say whose value it replaced.
        private static readonly Dictionary<string, string> SetBy = new Dictionary<string, string>();

        /// <summary>Who most recently set a key, or null.</summary>
        public static string LastSetBy(string key)
        {
            string who;
            return SetBy.TryGetValue(key ?? string.Empty, out who) ? who : null;
        }

        /// <summary>Set one economy value. Unknown keys are rejected so typos are not silent.</summary>
        public static bool Set(string key, float value, string source = null)
        {
            Setting setting = Find(key);
            if (setting == null)
                return false;

            // Two packs setting the same global is legal -- later wins -- but silent. Someone
            // enabling two difficulty packs and seeing only one take effect deserves to be told
            // which, rather than concluding the mod is broken.
            string previous;
            if (source != null && SetBy.TryGetValue(setting.Key, out previous) && previous != source)
            {
                ComboModApi.Log?.LogWarning(
                    "'" + source + "' overrides '" + previous + "' for " + setting.Key
                    + ". Later packs win; disable one in the Packs tab if that is not what you want.");
            }

            if (source != null)
                SetBy[setting.Key] = source;

            Values[setting.Key] = value;
            ComboModApi.Log?.LogInfo(
                "Economy " + setting.Key + ": " + setting.Vanilla.ToString("0.####", CultureInfo.InvariantCulture)
                + " -> " + value.ToString("0.####", CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>Drop one override, restoring vanilla for that value.</summary>
        public static void Clear(string key)
        {
            Setting setting = Find(key);
            if (setting != null)
                Values.Remove(setting.Key);
        }

        /// <summary>Drop every override.</summary>
        public static void ClearAll()
        {
            if (Values.Count == 0)
            {
                SetBy.Clear();
                return;
            }

            Values.Clear();
            SetBy.Clear();
            ComboModApi.Log?.LogInfo("Economy restored to vanilla.");
        }

        // --- lookups the Harmony patches call ---

        /// <summary>Map a Rarity onto its key prefix. Returns null for Rarity.None.</summary>
        internal static string RaritySuffix(Rarity rarity)
        {
            switch (rarity)
            {
                case Rarity.Common: return "common";
                case Rarity.Uncommon: return "uncommon";
                case Rarity.Rare: return "rare";
                case Rarity.Masterwork: return "masterwork";
                case Rarity.Legendary: return "legendary";
                default: return null;
            }
        }

        internal static bool TryBuildingWeight(Rarity rarity, out float weight)
        {
            weight = 0f;
            string suffix = RaritySuffix(rarity);
            if (suffix == null)
                return false;

            weight = Get("draft." + suffix);
            return true;
        }

        internal static bool TryItemWeight(Rarity rarity, out float weight)
        {
            weight = 0f;
            string suffix = RaritySuffix(rarity);
            if (suffix == null)
                return false;

            weight = Get("item." + suffix);
            return true;
        }

        internal static float DriftFor(Rarity rarity)
        {
            string suffix = RaritySuffix(rarity);
            return suffix == null ? 0f : Get("drift." + suffix);
        }
    }
}
