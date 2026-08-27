using System;
using System.Collections.Generic;
using System.Reflection;
using Framework;
using GameState;
using GameState.Data;

namespace ComboMod
{
    /// <summary>
    /// The difficulty curve: what score each milestone demands.
    /// <para>
    /// Every milestone carries three thresholds — a base one, and higher ones used once you
    /// reach the Yeoman and Governor ranks — and picks between them by your current rank. All
    /// three are set together unless a pack asks for one specifically, because setting only the
    /// base leaves a ranked player on untouched numbers and looking at a mod that appears to do
    /// nothing.
    /// </para>
    /// <para>
    /// Tier 1. These live on a ScriptableObject that is never written to disk; the save stores
    /// only <c>MilestoneIndex</c>. Removing ComboMod restores the shipped curve.
    /// </para>
    /// <para>
    /// One caveat worth knowing: the ScriptableObject is shared for the whole session, so a
    /// change applies to later runs too until the game restarts.
    /// </para>
    /// </summary>
    public static class MilestoneTuning
    {
        /// <summary>Which of a milestone's three thresholds a pack is addressing.</summary>
        public enum Threshold
        {
            /// <summary>All three at once. What a pack means unless it says otherwise.</summary>
            All,

            /// <summary>Base, used below Yeoman rank.</summary>
            Base,

            /// <summary>Used at Yeoman rank and above.</summary>
            RankA,

            /// <summary>Used at Governor rank and above.</summary>
            RankB,
        }

        private const string BaseField  = "<_scoreRequiredToComplete>k__BackingField";
        private const string RankAField = "<ScoreRequiredToCompleteRankA>k__BackingField";
        private const string RankBField = "<ScoreRequiredToCompleteRankB>k__BackingField";

        private struct Target
        {
            public int Value;
            public Threshold Which;
        }

        private static readonly Dictionary<CitySize, Target> Requested = new Dictionary<CitySize, Target>();

        /// <summary>Multiplies every milestone. Applied after any explicit value for that milestone.</summary>
        private static float _scale = 1f;

        // Shipped values, captured the first time anything is changed so the curve can be put
        // back without a restart.
        private static readonly Dictionary<CitySize, int[]> Original = new Dictionary<CitySize, int[]>();

        /// <summary>True when a pack has asked for any change to the curve.</summary>
        public static bool AnyOverrides => Requested.Count > 0 || Math.Abs(_scale - 1f) > 0.0001f;

        /// <summary>The scale in force. 1.0 is the shipped curve.</summary>
        public static float Scale => _scale;

        // Who last set each milestone, and the scale, for conflict reporting.
        private static readonly Dictionary<CitySize, string> SetBy = new Dictionary<CitySize, string>();
        private static string _scaleSetBy;

        /// <summary>Set one milestone's requirement.</summary>
        public static void Set(CitySize size, int value, Threshold which = Threshold.All, string source = null)
        {
            string previous;
            if (source != null && SetBy.TryGetValue(size, out previous) && previous != source)
                ComboModApi.Log?.LogWarning(
                    "'" + source + "' overrides '" + previous + "' for milestone " + size + ".");

            if (source != null)
                SetBy[size] = source;

            Requested[size] = new Target { Value = value, Which = which };
        }

        /// <summary>
        /// Multiply every milestone. 0.5 halves the curve, 2 doubles it.
        /// <para>
        /// Two difficulty packs both setting a scale is the most likely conflict of all, and the
        /// least visible -- the numbers still change, just not by the amount one of them asked
        /// for.
        /// </para>
        /// </summary>
        public static void SetScale(float scale, string source = null)
        {
            if (source != null && _scaleSetBy != null && _scaleSetBy != source)
                ComboModApi.Log?.LogWarning(
                    "'" + source + "' overrides '" + _scaleSetBy + "' for the milestone scale "
                    + "(" + _scale.ToString("0.##") + " -> " + scale.ToString("0.##") + "). "
                    + "Later packs win; disable one in the Packs tab.");

            if (source != null)
                _scaleSetBy = source;

            _scale = scale;
        }

        /// <summary>Drop every change and put the shipped curve back.</summary>
        public static void ClearAll()
        {
            bool had = AnyOverrides;

            Requested.Clear();
            SetBy.Clear();
            _scale = 1f;
            _scaleSetBy = null;

            if (had)
                Restore();
        }

        /// <summary>
        /// Write the requested curve onto the milestone assets.
        /// <para>
        /// Always restores to shipped values first, so this is idempotent and a reload that
        /// drops a pack actually undoes it rather than compounding on what was already applied.
        /// </para>
        /// </summary>
        public static void Apply()
        {
            List<Milestone> milestones = GetMilestones();
            if (milestones == null)
                return;

            CaptureOriginals(milestones);
            Restore();

            if (!AnyOverrides)
                return;

            int changed = 0;

            foreach (Milestone milestone in milestones)
            {
                if (milestone == null)
                    continue;

                int[] shipped;
                if (!Original.TryGetValue(milestone.CitySize, out shipped))
                    continue;

                int baseValue  = shipped[0];
                int rankAValue = shipped[1];
                int rankBValue = shipped[2];

                Target target;
                if (Requested.TryGetValue(milestone.CitySize, out target))
                {
                    switch (target.Which)
                    {
                        case Threshold.Base:  baseValue  = target.Value; break;
                        case Threshold.RankA: rankAValue = target.Value; break;
                        case Threshold.RankB: rankBValue = target.Value; break;
                        default:
                            baseValue = rankAValue = rankBValue = target.Value;
                            break;
                    }
                }

                if (Math.Abs(_scale - 1f) > 0.0001f)
                {
                    baseValue  = Scaled(baseValue);
                    rankAValue = Scaled(rankAValue);
                    rankBValue = Scaled(rankBValue);
                }

                Write(milestone, BaseField,  baseValue);
                Write(milestone, RankAField, rankAValue);
                Write(milestone, RankBField, rankBValue);
                changed++;
            }

            ComboModApi.Log?.LogInfo(
                "Milestone curve: " + changed + " milestone(s) set"
                + (Math.Abs(_scale - 1f) > 0.0001f ? ", scale " + _scale.ToString("0.##") : string.Empty));
        }

        /// <summary>Scores are ints and a milestone of 0 would complete instantly, so floor at 1.</summary>
        private static int Scaled(int value) => Math.Max(1, (int)Math.Round(value * _scale));

        private static void CaptureOriginals(List<Milestone> milestones)
        {
            if (Original.Count > 0)
                return;

            foreach (Milestone milestone in milestones)
            {
                if (milestone == null || Original.ContainsKey(milestone.CitySize))
                    continue;

                Original[milestone.CitySize] = new[]
                {
                    Read(milestone, BaseField),
                    Read(milestone, RankAField),
                    Read(milestone, RankBField),
                };
            }
        }

        private static void Restore()
        {
            List<Milestone> milestones = GetMilestones();
            if (milestones == null || Original.Count == 0)
                return;

            foreach (Milestone milestone in milestones)
            {
                int[] shipped;
                if (milestone == null || !Original.TryGetValue(milestone.CitySize, out shipped))
                    continue;

                Write(milestone, BaseField,  shipped[0]);
                Write(milestone, RankAField, shipped[1]);
                Write(milestone, RankBField, shipped[2]);
            }
        }

        private static List<Milestone> GetMilestones()
        {
            try
            {
                MilestoneConfigLookup lookup = ScriptableObjectSingleton<MilestoneConfigLookup>.Instance;
                return lookup == null ? null : lookup.Milestones;
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogWarning("Milestone list unavailable: " + ex.Message);
                return null;
            }
        }

        private static FieldInfo Field(string name) =>
            typeof(Milestone).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static int Read(Milestone milestone, string name)
        {
            FieldInfo field = Field(name);
            return field == null ? 0 : (int)field.GetValue(milestone);
        }

        private static void Write(Milestone milestone, string name, int value)
        {
            FieldInfo field = Field(name);
            if (field != null)
                field.SetValue(milestone, value);
        }

        /// <summary>Parse a pack key like "Hamlet" or "Hamlet.rankA" into a size and threshold.</summary>
        public static bool TryParseKey(string key, out CitySize size, out Threshold which)
        {
            size = CitySize.None;
            which = Threshold.All;

            string sizePart = key;
            int dot = key.IndexOf('.');

            if (dot >= 0)
            {
                sizePart = key.Substring(0, dot);
                string suffix = key.Substring(dot + 1).Trim().ToLowerInvariant();

                switch (suffix)
                {
                    case "base":  which = Threshold.Base;  break;
                    case "ranka": which = Threshold.RankA; break;
                    case "rankb": which = Threshold.RankB; break;
                    default: return false;
                }
            }

            try
            {
                size = (CitySize)Enum.Parse(typeof(CitySize), sizePart.Trim(), ignoreCase: true);
            }
            catch
            {
                return false;
            }

            return Enum.IsDefined(typeof(CitySize), size) && size != CitySize.None;
        }
    }
}
