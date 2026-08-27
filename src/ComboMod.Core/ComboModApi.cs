using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using Entities;
using BuildingBehavioursMap = Entities.BuildingBehaviours.BuildingBehaviours;
using ItemBehavioursMap = Entities.ItemBehaviours.ItemBehaviours;

namespace ComboMod
{
    /// <summary>One field a tune actually changed, for display and diagnostics.</summary>
    public struct FieldChange
    {
        public string Field;
        public object From;
        public object To;

        /// <summary>Field name without its leading underscore, e.g. "cooldownParam".</summary>
        public string DisplayName => Field.TrimStart('_');

        public override string ToString() =>
            DisplayName + ": " + Describe(From) + " -> " + Describe(To);

        /// <summary>
        /// Render a value for a human. Collections print their contents rather than their type
        /// name -- "Grass, Sand, Shore" instead of
        /// "System.Collections.Generic.HashSet`1[Environment.TileType]", which is what the
        /// default ToString gives and which tells a pack author nothing.
        /// </summary>
        private static string Describe(object value)
        {
            if (value == null)
                return "null";

            if (value is string s)
                return s;

            if (value is System.Collections.IEnumerable items)
            {
                var parts = new List<string>();
                foreach (object item in items)
                    parts.Add(item?.ToString() ?? "null");

                parts.Sort(StringComparer.OrdinalIgnoreCase);
                return parts.Count == 0 ? "(none)" : string.Join(", ", parts.ToArray());
            }

            return value.ToString();
        }
    }

    /// <summary>
    /// Where a tune came from, which decides who wins when several touch the same stat.
    /// <para>
    /// Later beats earlier. A hand-authored pack is more specific intent than a shipped DLL's
    /// defaults, and something typed into the panel just now is more specific still.
    /// </para>
    /// </summary>
    public enum TuneSourceKind
    {
        /// <summary>Registered by a mod assembly through Tune/TuneItem.</summary>
        Code = 0,

        /// <summary>Read from a .pack file.</summary>
        Pack = 1,

        /// <summary>Typed into the in-game editor.</summary>
        LiveEdit = 2,
    }

    /// <summary>
    /// One registered rebalance. Toggling <see cref="Enabled"/> and calling
    /// <see cref="ComboModApi.Reapply"/> turns it on or off live.
    /// </summary>
    public sealed class TuneRegistration
    {
        /// <summary>Which layer this belongs to. Higher values are applied later and win.</summary>
        public TuneSourceKind Kind { get; }

        /// <summary>The building or item this tune targets.</summary>
        public GameTag Tag { get; }

        /// <summary>Assembly that registered it, so the panel can group by mod.</summary>
        public string Source { get; }

        /// <summary>False means the tune is registered but not applied.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>True for item/heirloom tunes, false for buildings.</summary>
        public bool IsItem { get; }

        /// <summary>What the last apply actually changed. Empty when disabled or a no-op.</summary>
        public IList<FieldChange> LastChanges { get; internal set; } = new List<FieldChange>();

        internal Action<Tuner> Configure { get; }

        internal TuneRegistration(
            GameTag tag, string source, bool isItem, TuneSourceKind kind, Action<Tuner> configure)
        {
            Tag = tag;
            Source = source;
            IsItem = isItem;
            Kind = kind;
            Configure = configure;
        }
    }

    /// <summary>
    /// The Tier 1 modding surface: rebalancing existing buildings and items.
    /// <para>
    /// Everything reachable through this class is save-safe by construction. Base stats are
    /// never serialized, so removing the mod restores vanilla behaviour and any save written
    /// while it was active still loads in an unmodded client.
    /// </para>
    /// <para>
    /// Tunes are registered, not applied immediately. The game rebuilds its behaviour
    /// dictionaries from scratch on every scene load (BehavioursController.Awake), so
    /// registrations are re-applied by a Harmony postfix each time that happens.
    /// </para>
    /// </summary>
    public static class ComboModApi
    {
        /// <summary>
        /// Safe stand-in for a zero draft weight.
        /// <para>
        /// ChooseBag.RemoveElement only removes entries whose weight is greater than 0. An
        /// entry at exactly 0 can be chosen but never removed, so GetRoll can pick it
        /// repeatedly; because results are collected into a HashSet the caller silently
        /// receives fewer draft options than it asked for. A tiny positive weight keeps the
        /// entry removable while making it effectively unreachable.
        /// </para>
        /// </summary>
        public const float SuppressionWeight = 1e-6f;

        private sealed class Snapshot
        {
            /// <summary>The instance these values came from. Scene loads hand us a new one.</summary>
            public object Behaviour;

            /// <summary>Field name to its true vanilla value, captured before any tune ran.</summary>
            public readonly Dictionary<string, object> Vanilla = new Dictionary<string, object>();
        }

        private static readonly List<TuneRegistration> Registry = new List<TuneRegistration>();
        private static readonly Dictionary<GameTag, Snapshot> BuildingSnapshots = new Dictionary<GameTag, Snapshot>();
        private static readonly Dictionary<GameTag, Snapshot> ItemSnapshots = new Dictionary<GameTag, Snapshot>();

        /// <summary>Shared log sink. Public so companion assemblies can report through it.</summary>
        public static ManualLogSource Log;

        /// <summary>Every registered tune, in registration order. Safe to enumerate from UI.</summary>
        public static IReadOnlyList<TuneRegistration> Registrations => Registry;

        /// <summary>True once any tune has been registered. Drives the achievement guard.</summary>
        public static bool AnyTunesRegistered => Registry.Count > 0;

        /// <summary>Source label given to registrations created by the in-game editor.</summary>
        public const string LiveEditSource = "Live edits";

        // Field-name to value, per tag. Applied after every mod's registrations, so a live edit
        // always wins over the mod that shipped the value.
        private static readonly Dictionary<GameTag, Dictionary<string, object>> LiveEdits =
            new Dictionary<GameTag, Dictionary<string, object>>();

        /// <summary>The live edits currently in force, keyed by tag then backing field name.</summary>
        public static IReadOnlyDictionary<GameTag, Dictionary<string, object>> Overrides => LiveEdits;

        /// <summary>
        /// Set one base stat directly, overriding whatever any mod registered for it.
        /// <para>
        /// Backed by an ordinary registration tagged <see cref="LiveEditSource"/> that sorts
        /// last, so live edits participate in enable/disable and restore like any other tune
        /// rather than being a parallel mechanism with its own bugs.
        /// </para>
        /// </summary>
        public static void SetOverride(GameTag tag, bool isItem, string field, object value)
        {
            if (!Enum.IsDefined(typeof(GameTag), tag))
                throw new ArgumentException("Not a vanilla GameTag: " + (int)tag, nameof(tag));

            if (!LiveEdits.TryGetValue(tag, out Dictionary<string, object> fields))
            {
                fields = new Dictionary<string, object>();
                LiveEdits[tag] = fields;

                GameTag captured = tag;
                Registry.Add(new TuneRegistration(captured, LiveEditSource, isItem, TuneSourceKind.LiveEdit, tuner =>
                {
                    if (!LiveEdits.TryGetValue(captured, out Dictionary<string, object> live))
                        return;

                    // Per-field, same reasoning as packs: one renamed field should not cost
                    // every other edit on this piece.
                    foreach (KeyValuePair<string, object> entry in live)
                    {
                        try
                        {
                            tuner.SetByField(entry.Key, entry.Value);
                        }
                        catch (MissingFieldException)
                        {
                            Log?.LogWarning(
                                "Live edit " + captured + "." + entry.Key.TrimStart('_') +
                                " no longer exists on this build; skipped.");
                        }
                    }
                }));
            }

            fields[field] = value;
            LiveEditStore.MarkDirty();
            Reapply();
        }

        /// <summary>Drop one live edit, restoring whatever the mods asked for.</summary>
        public static void ClearOverride(GameTag tag, string field)
        {
            if (!LiveEdits.TryGetValue(tag, out Dictionary<string, object> fields))
                return;

            fields.Remove(field);
            LiveEditStore.MarkDirty();

            if (fields.Count == 0)
            {
                LiveEdits.Remove(tag);
                for (int i = Registry.Count - 1; i >= 0; i--)
                    if (Registry[i].Source == LiveEditSource && Registry[i].Tag == tag)
                        Registry.RemoveAt(i);
            }

            Reapply();
        }

        /// <summary>Drop every live edit.</summary>
        public static void ClearAllOverrides()
        {
            LiveEdits.Clear();
            LiveEditStore.MarkDirty();

            for (int i = Registry.Count - 1; i >= 0; i--)
                if (Registry[i].Source == LiveEditSource)
                    Registry.RemoveAt(i);

            Reapply();
            Log?.LogInfo("Cleared all live edits.");
        }

        /// <summary>
        /// The live behaviour object for a tag, or null. Exposed so the editor can read current
        /// values for knobs no mod has touched.
        /// </summary>
        public static object GetBehaviour(GameTag tag, bool isItem)
        {
            if (isItem)
                return ItemBehavioursMap.Instance != null
                    && ItemBehavioursMap.Instance.ItemBehavioursDict.TryGetValue(tag, out var i) ? i : null;

            return BuildingBehavioursMap.Instance != null
                && BuildingBehavioursMap.Instance.BuildingBehaviourDict.TryGetValue(tag, out var b) ? b : null;
        }

        /// <summary>
        /// Every tag that actually has a behaviour object, and is therefore editable. This is
        /// the real moddable set: a tag with no behaviour class has no base stats to change.
        /// </summary>
        // Cached because the panel asks for these every OnGUI pass, which runs twice a frame.
        // Building the list means allocating ~167 entries and sorting them by ToString(); doing
        // that at 120 calls a second is real churn on a board that is already struggling.
        private static readonly List<GameTag> BuildingTagCache = new List<GameTag>();
        private static readonly List<GameTag> ItemTagCache = new List<GameTag>();
        private static object _buildingTagSource;
        private static object _itemTagSource;

        /// <summary>
        /// Every tag that actually has a behaviour object, and is therefore editable. This is
        /// the real moddable set: a tag with no behaviour class has no base stats to change.
        /// <para>
        /// The returned list is a shared cache — read it, do not mutate it. It is rebuilt only
        /// when the game swaps in a new behaviour dictionary, which happens on scene load.
        /// </para>
        /// </summary>
        public static List<GameTag> GetTunableTags(bool isItem)
        {
            if (isItem)
            {
                object source = ItemBehavioursMap.Instance != null
                    ? ItemBehavioursMap.Instance.ItemBehavioursDict
                    : null;

                if (!ReferenceEquals(source, _itemTagSource))
                {
                    _itemTagSource = source;
                    Rebuild(ItemTagCache, ItemBehavioursMap.Instance?.ItemBehavioursDict.Keys);
                }

                return ItemTagCache;
            }

            object buildings = BuildingBehavioursMap.Instance != null
                ? BuildingBehavioursMap.Instance.BuildingBehaviourDict
                : null;

            if (!ReferenceEquals(buildings, _buildingTagSource))
            {
                _buildingTagSource = buildings;
                Rebuild(BuildingTagCache, BuildingBehavioursMap.Instance?.BuildingBehaviourDict.Keys);
            }

            return BuildingTagCache;
        }

        private static void Rebuild(List<GameTag> cache, ICollection<GameTag> keys)
        {
            cache.Clear();
            if (keys == null)
                return;

            cache.AddRange(keys);
            cache.Sort((a, b) => string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>How many registrations are currently switched on.</summary>
        public static int EnabledCount
        {
            get
            {
                int n = 0;
                foreach (TuneRegistration r in Registry)
                    if (r.Enabled) n++;
                return n;
            }
        }

        /// <summary>
        /// Register a rebalance for one building. Safe to call more than once for the same
        /// tag; the actions run in registration order.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when the tag is not a member of the vanilla GameTag enum. Custom tags
        /// serialize into saves as bare integers that an unmodded client cannot resolve, so
        /// they are deliberately out of scope for this tier.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static TuneRegistration Tune(GameTag tag, Action<Tuner> configure) =>
            Register(tag, configure, isItem: false, caller: Assembly.GetCallingAssembly());

        /// <summary>Register a rebalance for one item or heirloom. Same rules as <see cref="Tune"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static TuneRegistration TuneItem(GameTag tag, Action<Tuner> configure) =>
            Register(tag, configure, isItem: true, caller: Assembly.GetCallingAssembly());

        /// <summary>
        /// Register on behalf of a balance pack, attributing it to the pack rather than to
        /// whichever assembly happened to call in.
        /// </summary>
        internal static TuneRegistration RegisterFromPack(
            GameTag tag, bool isItem, string packName, Action<Tuner> configure)
        {
            if (!Enum.IsDefined(typeof(GameTag), tag))
                throw new ArgumentException("Not a vanilla GameTag: " + (int)tag, nameof(tag));

            var registration = new TuneRegistration(tag, packName, isItem, TuneSourceKind.Pack, configure);
            Registry.Add(registration);
            return registration;
        }

        /// <summary>
        /// Remove a registration and restore whatever it had changed. Used when packs are
        /// reloaded, so a removed pack's values do not linger until the next scene load.
        /// </summary>
        internal static void Unregister(TuneRegistration registration)
        {
            if (registration == null)
                return;

            registration.Enabled = false;
            Registry.Remove(registration);
        }

        private static TuneRegistration Register(
            GameTag tag, Action<Tuner> configure, bool isItem, Assembly caller)
        {
            if (configure == null)
                throw new ArgumentNullException(nameof(configure));

            // The Tier 1 guarantee in one line: vanilla tags only.
            if (!Enum.IsDefined(typeof(GameTag), tag))
            {
                throw new ArgumentException(
                    "ComboMod: " + (int)tag + " is not a vanilla GameTag. Custom tags are " +
                    "written into saves as plain integers and cannot be resolved by an " +
                    "unmodded client, so they are outside the save-safe tier.",
                    nameof(tag));
            }

            // Captured by the public entry point: GetCallingAssembly() here would just name
            // ComboMod.Core itself, since Tune() is the immediate caller.
            string source = caller?.GetName().Name ?? "unknown";
            var registration = new TuneRegistration(tag, source, isItem, TuneSourceKind.Code, configure);
            Registry.Add(registration);

            Log?.LogInfo("Registered " + (isItem ? "item" : "building") + " tune for " + tag + " from " + source);
            return registration;
        }

        /// <summary>
        /// Re-apply building tunes to the live behaviour instances.
        /// <para>
        /// Buildings and items are applied separately and only from their own init hook. A
        /// single combined apply would run twice per scene load (once per hook) and the second
        /// pass would snapshot the already-tuned value as the restore baseline, quietly
        /// destroying the vanilla values everything else depends on.
        /// </para>
        /// </summary>
        internal static void ApplyBuildings()
        {
            if (BuildingBehavioursMap.Instance == null)
                return;

            ApplyKind(
                isItem: false,
                snapshots: BuildingSnapshots,
                lookup: tag => BuildingBehavioursMap.Instance.BuildingBehaviourDict.TryGetValue(tag, out var b) ? b : null);
        }

        /// <summary>Re-apply item tunes. See <see cref="ApplyBuildings"/> for why these are separate.</summary>
        internal static void ApplyItems()
        {
            if (ItemBehavioursMap.Instance == null)
                return;

            ApplyKind(
                isItem: true,
                snapshots: ItemSnapshots,
                lookup: tag => ItemBehavioursMap.Instance.ItemBehavioursDict.TryGetValue(tag, out var b) ? b : null);
        }

        /// <summary>
        /// Re-apply everything now. Call after changing any <see cref="TuneRegistration.Enabled"/>
        /// flag; the effect is immediate and visible on buildings already placed.
        /// </summary>
        public static void Reapply()
        {
            ApplyBuildings();
            ApplyItems();
        }

        /// <summary>Switch every registration on and re-apply.</summary>
        public static void EnableAll()
        {
            foreach (TuneRegistration r in Registry)
                r.Enabled = true;
            Reapply();
            Log?.LogInfo("All tunes enabled.");
        }

        /// <summary>
        /// Switch every registration off and re-apply, putting the game back to vanilla numbers
        /// without a restart. Registrations survive, so they can be switched back on.
        /// </summary>
        public static void RevertAll()
        {
            foreach (TuneRegistration r in Registry)
                r.Enabled = false;
            Reapply();
            Log?.LogInfo("All tunes reverted to vanilla.");
        }

        private static void ApplyKind(
            bool isItem,
            Dictionary<GameTag, Snapshot> snapshots,
            Func<GameTag, object> lookup)
        {
            // Distinct tags this kind touches, preserving registration order.
            var tags = new List<GameTag>();
            foreach (TuneRegistration r in Registry)
                if (r.IsItem == isItem && !tags.Contains(r.Tag))
                    tags.Add(r.Tag);

            // Also visit tags we have a snapshot for but no longer have any registration for.
            // Deleting a pack and reloading removes its registrations, and without this those
            // tags would never be visited again -- so the deleted pack's values would stay
            // applied until the next scene load, looking like the reload had silently failed.
            foreach (GameTag orphaned in snapshots.Keys)
                if (!tags.Contains(orphaned))
                    tags.Add(orphaned);

            // Only true when a value genuinely moved, which is the bar for paying for a cache
            // walk. An earlier flag tracked "a tune wrote a field at all", which was true almost
            // always and made the check pointless.
            bool valuesActuallyMoved = false;
            // Debug level: the game calls the Init methods from three places
            // (BehavioursController, ItemPool, BuildingCategoryVisualization), so passes are
            // frequent and interleave. These headers are what make the log readable when they
            // do.
            Log?.LogDebug("[apply " + (isItem ? "items" : "buildings") + "] tags=" + tags.Count
                          + " enabled=" + EnabledCount + "/" + Registry.Count);

            foreach (GameTag tag in tags)
            {
                object behaviour = lookup(tag);
                if (behaviour == null)
                {
                    // Not fatal: some tags legitimately have no behaviour class.
                    Log?.LogWarning("No " + (isItem ? "item" : "building") + " behaviour for " + tag + "; skipping.");
                    continue;
                }

                Log?.LogDebug("   " + tag + " instance=" + behaviour.GetHashCode()
                              + " restored=" + (snapshots.TryGetValue(tag, out Snapshot dbg)
                                                && ReferenceEquals(dbg.Behaviour, behaviour)));

                // Put the behaviour back to vanilla before re-applying, so toggling a tune off
                // actually removes it. Only valid when it is the same instance we snapshotted;
                // after a scene load the new instance is already vanilla.
                if (snapshots.TryGetValue(tag, out Snapshot previous)
                    && ReferenceEquals(previous.Behaviour, behaviour))
                {
                    foreach (KeyValuePair<string, object> field in previous.Vanilla)
                        Tuner.WriteRaw(behaviour, field.Key, field.Value);
                }

                // Nothing tunes this tag any more: it has been restored above, so forget it
                // rather than carrying a stale snapshot forward for the rest of the session.
                bool stillTuned = false;
                foreach (TuneRegistration r in Registry)
                    if (r.IsItem == isItem && r.Tag == tag) { stillTuned = true; break; }

                if (!stillTuned)
                {
                    snapshots.Remove(tag);
                    valuesActuallyMoved = true;
                    continue;
                }

                var snapshot = new Snapshot { Behaviour = behaviour };
                snapshots[tag] = snapshot;

                // Layered by Kind so the winner does not depend on plugin load order: code
                // first, then packs, then live edits. Registration order still decides within a
                // layer, which is what a user expects from two packs touching the same stat.
                var ordered = new List<TuneRegistration>();
                for (int layer = 0; layer <= (int)TuneSourceKind.LiveEdit; layer++)
                    foreach (TuneRegistration r in Registry)
                        if (r.IsItem == isItem && r.Tag == tag && (int)r.Kind == layer)
                            ordered.Add(r);

                foreach (TuneRegistration registration in ordered)
                {

                    if (!registration.Enabled)
                    {
                        registration.LastChanges = new List<FieldChange>();
                        continue;
                    }

                    var before = new Dictionary<string, object>();
                    try
                    {
                        registration.Configure(new Tuner(behaviour, before));
                    }
                    catch (Exception ex)
                    {
                        // One bad tune must not take down every other mod's registrations.
                        Log?.LogError("Tune for " + tag + " from " + registration.Source + " threw: " + ex);
                    }

                    var changes = new List<FieldChange>();
                    foreach (KeyValuePair<string, object> field in before)
                    {
                        object after = Tuner.ReadRaw(behaviour, field.Key);
                        if (!Equals(field.Value, after))
                            valuesActuallyMoved = true;

                        changes.Add(new FieldChange
                        {
                            Field = field.Key,
                            From = field.Value,
                            To = after,
                        });

                        // Earliest value wins: we restored to vanilla above, so the first
                        // writer of a field captured the true vanilla value.
                        if (!snapshot.Vanilla.ContainsKey(field.Key))
                            snapshot.Vanilla[field.Key] = field.Value;
                    }

                    registration.LastChanges = changes;
                    if (changes.Count > 0)
                    {
                        foreach (FieldChange change in changes)
                            Log?.LogInfo("  " + tag + "." + change);
                    }
                    else
                    {
                        Log?.LogWarning("Tune for " + tag + " from " + registration.Source + " changed nothing.");
                    }
                }
            }

            // Stats are cached per placed building; without this, changes appear to work on
            // newly placed buildings and silently no-op on everything already on the map.
            //
            // Requested rather than performed: ResetCaches discards four dictionaries and walks
            // every placed building, and Reapply calls this twice (buildings then items). On a
            // full board that was two full walks per keystroke in the editor.
            if (valuesActuallyMoved)
                RequestCacheInvalidation();
        }

        /// <summary>
        /// Clear the cached range/multiplier/cooldown values on every placed building.
        /// Guards internally on whether a BuildingController exists, so it is safe to call
        /// from the main menu.
        /// </summary>
        /// <summary>True when a cache reset is owed. Flushed once per frame by the plugin.</summary>
        public static bool CacheInvalidationPending { get; private set; }

        /// <summary>
        /// Mark the stat caches stale without paying for it yet. Several tunes applied in one
        /// frame then cost one walk instead of one each.
        /// </summary>
        public static void RequestCacheInvalidation() => CacheInvalidationPending = true;

        /// <summary>Perform a pending invalidation, if any. Called once per frame.</summary>
        public static void FlushCacheInvalidation()
        {
            if (!CacheInvalidationPending)
                return;

            CacheInvalidationPending = false;
            InvalidateStatCaches();
        }

        public static void InvalidateStatCaches()
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                BuildingExtensions.ResetCaches();
                clock.Stop();

                // Cost scales with placed buildings, so it is invisible early and material on a
                // full board. Logged rather than assumed.
                if (clock.Elapsed.TotalMilliseconds >= 5.0)
                    Log?.LogWarning("Stat cache reset took " + clock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms.");
                else
                    Log?.LogDebug("Stat cache reset: " + clock.Elapsed.TotalMilliseconds.ToString("0.00") + " ms.");
            }
            catch (Exception ex)
            {
                Log?.LogError("Failed to reset stat caches: " + ex);
            }
        }
    }
}
