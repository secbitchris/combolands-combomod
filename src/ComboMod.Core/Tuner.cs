using System;
using System.Collections.Generic;
using System.Reflection;
using Entities;
using Environment;
using GameState.Data;

namespace ComboMod
{
    /// <summary>
    /// Typed access to a behaviour's base stats.
    /// <para>
    /// The game keeps every base stat in a protected/private field on
    /// <c>_GamePieceBehaviour</c> and exposes only <c>protected</c> setters, so a rebalance
    /// from outside the assembly has to go through reflection. Each write snapshots the
    /// previous value first, which is what makes <see cref="ComboModApi.RestoreAll"/> work.
    /// </para>
    /// <para>
    /// None of these fields are serialized. The run save stores only deltas from the base
    /// (LocalRangeChanges and friends), which is precisely why this tier is save-safe.
    /// </para>
    /// </summary>
    public sealed class Tuner
    {
        private static readonly Dictionary<string, FieldInfo> FieldCache =
            new Dictionary<string, FieldInfo>();

        private readonly object _behaviour;
        private readonly Dictionary<string, object> _originals;

        internal Tuner(object behaviour, Dictionary<string, object> originals)
        {
            _behaviour = behaviour;
            _originals = originals;
        }

        /// <summary>The tag this behaviour drives. Read-only: retagging is not save-safe.</summary>
        public GameTag Tag => (GameTag)Get("_behaviourType");

        // --- Numeric knobs. Vanilla ranges are in docs/modding-surface.html. ---

        /// <summary>Turns between activations. Vanilla 2..15. Effective value floors at 1 downstream.</summary>
        public int Cooldown { get => (int)Get("_cooldownParam"); set => Set("_cooldownParam", value); }

        /// <summary>Tile radius. Vanilla 2..8, unclamped. Large values cost range-index rebuild time.</summary>
        public int Range { get => (int)Get("_range"); set => Set("_range", value); }

        /// <summary>Shop cost. Vanilla 4..200; the constructor default is 77.</summary>
        public int BuyPrice { get => (int)Get("_buyPrice"); set => Set("_buyPrice", value); }

        /// <summary>Activations per trigger. Vanilla 1..5, unclamped.</summary>
        public int ActivationCount { get => (int)Get("_activationCount"); set => Set("_activationCount", value); }

        /// <summary>Tested as Random.value &lt;= chance. Vanilla 0.1..2.0; anything at or above 1 always fires.</summary>
        public float ActivationChance { get => (float)Get("_activationChance"); set => Set("_activationChance", value); }

        /// <summary>Behaviour-specific multiplier payload. Vanilla -3..7; negatives are legitimate.</summary>
        public float MultParam { get => (float)Get("_multParam"); set => Set("_multParam", value); }

        /// <summary>The piece's own score multiplier. Default 1.0, unclamped.</summary>
        public float Multiplier { get => (float)Get("_multiplier"); set => Set("_multiplier", value); }

        /// <summary>Coins granted. Vanilla 1..100.</summary>
        public int Money { get => (int)Get("_moneyCountParam"); set => Set("_moneyCountParam", value); }

        /// <summary>Points granted. Vanilla -150..250.</summary>
        public int Score { get => (int)Get("_baseScoreParam"); set => Set("_baseScoreParam", value); }

        /// <summary>Rerolls granted. Vanilla 1..5.</summary>
        public int Rerolls { get => (int)Get("_rerollsCountParam"); set => Set("_rerollsCountParam", value); }

        /// <summary>Removes granted. Vanilla always 1.</summary>
        public int Removes { get => (int)Get("_removesCountParam"); set => Set("_removesCountParam", value); }

        /// <summary>Dismisses granted.</summary>
        public int Dismisses { get => (int)Get("_dismissesCountParam"); set => Set("_dismissesCountParam", value); }

        /// <summary>Enchant charges. Vanilla 1..2.</summary>
        public int Enchant { get => (int)Get("_enchantParam"); set => Set("_enchantParam", value); }

        /// <summary>Initial stored value.</summary>
        public int StoredValue { get => (int)Get("_storedValueParam"); set => Set("_storedValueParam", value); }

        /// <summary>Range granted to other buildings. Vanilla always 1.</summary>
        public int RangeModification { get => (int)Get("_rangeModificationParam"); set => Set("_rangeModificationParam", value); }

        /// <summary>Cooldown reduction granted to other buildings. Vanilla 1..3.</summary>
        public int CooldownModification { get => (int)Get("_cooldownModificationParam"); set => Set("_cooldownModificationParam", value); }

        /// <summary>
        /// Draft weight multiplier, default 1.0.
        /// <para>
        /// Do not set this to exactly 0 to suppress a building. ChooseBag.RemoveElement only
        /// removes entries whose weight is greater than 0, so a 0-weight entry becomes
        /// unremovable and silently shrinks the number of draft choices offered. Use
        /// <see cref="ComboModApi.SuppressionWeight"/> instead.
        /// </para>
        /// </summary>
        public float RollChanceMultiplier { get => (float)Get("_rollChanceMultiplier"); set => Set("_rollChanceMultiplier", value); }

        // --- Enum and collection knobs. Vanilla members only; ComboModApi.Tune enforces that. ---

        /// <summary>Draft rarity. Legendary has a 0.0 weight in vanilla's roll tables, so it never appears naturally.</summary>
        public Rarity Rarity { get => (Rarity)Get("_rarity"); set => Set("_rarity", value); }

        /// <summary>Primary category, used by adjacency and scoring rules.</summary>
        public GamePieceCategory MajorCategory { get => (GamePieceCategory)Get("_majorCategory"); set => Set("_majorCategory", value); }

        /// <summary>Secondary categories. Mutating the returned set in place is not tracked; assign a new set.</summary>
        public HashSet<GamePieceCategory> MinorCategories
        {
            get => (HashSet<GamePieceCategory>)Get("_minorCategories");
            set => Set("_minorCategories", value);
        }

        /// <summary>
        /// Tile types this building may be placed on. Buildings only: items have no such field,
        /// so touching this on an item throws and is reported per-field.
        /// <para>
        /// Widening the set permits a placement, it does not force one.
        /// GetTileTypesCanBePlacedOn is virtual and some behaviours override it outright
        /// (Enclave does), and CanBeBuiltOn can still veto for its own reasons.
        /// </para>
        /// </summary>
        public HashSet<TileType> CanBePlacedOn
        {
            get => (HashSet<TileType>)Get("_canBePlacedOn");
            set => Set("_canBePlacedOn", value);
        }

        /// <summary>Replace the placement set in one call.</summary>
        public Tuner SetCanBePlacedOn(params TileType[] types)
        {
            CanBePlacedOn = new HashSet<TileType>(types);
            return this;
        }

        /// <summary>Which triggers this behaviour responds to. Assign a new set rather than mutating.</summary>
        public HashSet<TriggerType> ValidTriggers
        {
            get => (HashSet<TriggerType>)Get("_validTriggers");
            set => Set("_validTriggers", value);
        }

        /// <summary>Replace the minor-category set in one call.</summary>
        public Tuner SetMinorCategories(params GamePieceCategory[] categories)
        {
            MinorCategories = new HashSet<GamePieceCategory>(categories);
            return this;
        }

        /// <summary>Replace the valid-trigger set in one call.</summary>
        public Tuner SetValidTriggers(params TriggerType[] triggers)
        {
            ValidTriggers = new HashSet<TriggerType>(triggers);
            return this;
        }

        // --- knob metadata, for tools that edit fields generically ---

        /// <summary>One editable base stat: display name, backing field, and value type.</summary>
        public sealed class Knob
        {
            public readonly string Name;
            public readonly string Field;
            public readonly Type Type;

            internal Knob(string name, string field, Type type)
            {
                Name = name;
                Field = field;
                Type = type;
            }
        }

        /// <summary>
        /// Every knob the live editor can drive. Collection-valued knobs (MinorCategories,
        /// ValidTriggers) are deliberately absent: they are not meaningfully editable as text
        /// and stay API-only.
        /// </summary>
        public static readonly Knob[] Knobs =
        {
            new Knob("Cooldown",             "_cooldownParam",             typeof(int)),
            new Knob("Range",                "_range",                     typeof(int)),
            new Knob("BuyPrice",             "_buyPrice",                  typeof(int)),
            new Knob("ActivationCount",      "_activationCount",           typeof(int)),
            new Knob("ActivationChance",     "_activationChance",          typeof(float)),
            new Knob("MultParam",            "_multParam",                 typeof(float)),
            new Knob("Multiplier",           "_multiplier",                typeof(float)),
            new Knob("Money",                "_moneyCountParam",           typeof(int)),
            new Knob("Score",                "_baseScoreParam",            typeof(int)),
            new Knob("Rerolls",              "_rerollsCountParam",         typeof(int)),
            new Knob("Removes",              "_removesCountParam",         typeof(int)),
            new Knob("Dismisses",            "_dismissesCountParam",       typeof(int)),
            new Knob("Enchant",              "_enchantParam",              typeof(int)),
            new Knob("StoredValue",          "_storedValueParam",          typeof(int)),
            new Knob("RangeModification",    "_rangeModificationParam",    typeof(int)),
            new Knob("CooldownModification", "_cooldownModificationParam", typeof(int)),
            new Knob("RollChanceMultiplier", "_rollChanceMultiplier",      typeof(float)),
            new Knob("Rarity",               "_rarity",                    typeof(Rarity)),
        };

        /// <summary>Look a knob up by its backing field name.</summary>
        public static Knob FindKnob(string field)
        {
            foreach (Knob knob in Knobs)
                if (knob.Field == field)
                    return knob;
            return null;
        }

        /// <summary>
        /// Set a field by name, tracked for restore exactly like the typed properties.
        /// Used by the live editor, which works from <see cref="Knobs"/> rather than
        /// compile-time property access.
        /// </summary>
        public void SetByField(string field, object value) => Set(field, value);

        /// <summary>Read a field by name.</summary>
        public object GetByField(string field) => Get(field);

        // --- reflection plumbing ---

        /// <summary>
        /// Write a field directly without snapshotting. Used by the restore path, which is
        /// replaying values it already captured.
        /// </summary>
        internal static void WriteRaw(object behaviour, string field, object value)
        {
            ResolveOn(behaviour, field).SetValue(behaviour, value);
        }

        /// <summary>Read a field without going through a typed property. Used for change reporting.</summary>
        public static object ReadRaw(object behaviour, string field)
        {
            return ResolveOn(behaviour, field).GetValue(behaviour);
        }

        private object Get(string field) => Resolve(field).GetValue(_behaviour);

        private void Set(string field, object value)
        {
            FieldInfo fi = Resolve(field);
            if (!_originals.ContainsKey(field))
                _originals[field] = fi.GetValue(_behaviour);
            fi.SetValue(_behaviour, value);
        }

        private FieldInfo Resolve(string field) => ResolveOn(_behaviour, field);

        private static FieldInfo ResolveOn(object behaviour, string field)
        {
            // Every knob lives on _GamePieceBehaviour itself, so one cache entry per field
            // name is correct regardless of which concrete behaviour asked for it.
            if (FieldCache.TryGetValue(field, out FieldInfo cached))
                return cached;

            // _rarity is private on _GamePieceBehaviour, so GetField on the derived behaviour
            // type will not find it. Walk the hierarchy explicitly.
            for (Type t = behaviour.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo fi = t.GetField(field,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fi != null)
                {
                    FieldCache[field] = fi;
                    return fi;
                }
            }

            throw new MissingFieldException(
                "ComboMod: field " + field + " is gone from " + behaviour.GetType().Name +
                ". The game was almost certainly patched; re-run the surface audit.");
        }
    }
}
