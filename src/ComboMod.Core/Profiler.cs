using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace ComboMod
{
    /// <summary>
    /// A small sampling harness for working out where frame time actually goes.
    /// <para>
    /// Built because two rounds of reasoning about the scoring slowdown produced two plausible
    /// mechanisms and one real fix that did not resolve it. Guessing a third time is worse than
    /// spending a few hundred lines on numbers.
    /// </para>
    /// <para>
    /// Off unless <c>Performance.Profile</c> is set. The instrumentation itself costs a
    /// timestamp pair per call, which is cheap but not free — it is a diagnostic, not something
    /// to leave running.
    /// </para>
    /// </summary>
    public static class Profiler
    {
        private struct Stat
        {
            public long Calls;
            public long Ticks;

            /// <summary>
            /// Worst single call. Totals hide a stall: one 700ms call among thousands of cheap
            /// ones averages away to nothing, and averaging away the stall is exactly wrong when
            /// the stall is the thing being hunted.
            /// </summary>
            public long MaxTicks;
        }

        private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>();

        /// <summary>Frames seen since the last report, and the worst one.</summary>
        private static int _frames;
        private static float _worstFrameMs;
        private static float _accumulatedFrameMs;
        private static float _sinceReport;

        // Unity's Mono GC is stop-the-world and non-generational, so a collection shows up as a
        // single stalled frame rather than as cost spread across methods. Method timers cannot
        // see it, which is exactly the gap this closes.
        private static int _gcAtLastReport = -1;
        private static int _gcDuringSpikes;
        private static int _spikes;
        private const float SpikeMs = 100f;
        private static int _gcAtLastFrame = -1;

        internal static bool Enabled;

        /// <summary>Seconds between reports. Only reports when something was actually recorded.</summary>
        private const float ReportInterval = 5f;

        /// <summary>Timestamp helper for patches, matching Stopwatch's tick scale.</summary>
        public static long Now() => Stopwatch.GetTimestamp();

        /// <summary>Record one call of <paramref name="key"/> that started at <paramref name="startedAt"/>.</summary>
        public static void Record(string key, long startedAt)
        {
            if (!Enabled)
                return;

            Stat stat;
            Stats.TryGetValue(key, out stat);
            long elapsed = Stopwatch.GetTimestamp() - startedAt;
            stat.Calls++;
            stat.Ticks += elapsed;
            if (elapsed > stat.MaxTicks)
                stat.MaxTicks = elapsed;
            Stats[key] = stat;
        }

        /// <summary>Record a call with no duration, for counting things like instantiations.</summary>
        public static void Count(string key)
        {
            if (!Enabled)
                return;

            Stat stat;
            Stats.TryGetValue(key, out stat);
            stat.Calls++;
            Stats[key] = stat;
        }

        /// <summary>Feed one frame's duration. Called from the plugin's Update.</summary>
        internal static void Frame(float deltaTimeMs)
        {
            if (!Enabled)
                return;

            _frames++;
            _accumulatedFrameMs += deltaTimeMs;
            if (deltaTimeMs > _worstFrameMs)
                _worstFrameMs = deltaTimeMs;

            // Attribute each spike to a collection or not, by checking whether the gen-0 count
            // moved on the same frame. That correlation is the whole test.
            int gcNow = GC.CollectionCount(0);
            if (deltaTimeMs >= SpikeMs)
            {
                _spikes++;
                if (_gcAtLastFrame >= 0 && gcNow > _gcAtLastFrame)
                    _gcDuringSpikes++;
            }

            _gcAtLastFrame = gcNow;

            _sinceReport += deltaTimeMs / 1000f;
            if (_sinceReport >= ReportInterval)
                Report();
        }

        /// <summary>
        /// Log what happened since the last report, heaviest first, then reset.
        /// <para>
        /// Silent when nothing was recorded, so an idle menu does not fill the log.
        /// </para>
        /// </summary>
        public static void Report()
        {
            _sinceReport = 0f;

            // Report whenever anything happened at all, including frame spikes with no recorded
            // calls -- that combination is itself the finding.
            if (Stats.Count == 0 && _spikes == 0)
            {
                _gcAtLastReport = GC.CollectionCount(0);
                ResetCounters();
                return;
            }

            // Sort by total time, since a cheap call made a hundred thousand times and an
            // expensive one made twice are both worth seeing, and only total time ranks them.
            var ordered = new List<KeyValuePair<string, Stat>>(Stats);
            ordered.Sort((a, b) => b.Value.Ticks.CompareTo(a.Value.Ticks));

            double toMs = 1000.0 / Stopwatch.Frequency;
            float avgFrame = _frames > 0 ? _accumulatedFrameMs / _frames : 0f;

            var sb = new StringBuilder();
            sb.AppendLine("--- profile, last " + ReportInterval + "s ---");
            int gcNow = GC.CollectionCount(0);
            int collections = _gcAtLastReport >= 0 ? gcNow - _gcAtLastReport : 0;
            _gcAtLastReport = gcNow;

            long managedMb = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);

            sb.AppendLine("  frames " + _frames
                          + "  avg " + avgFrame.ToString("0.0") + "ms"
                          + "  worst " + _worstFrameMs.ToString("0.0") + "ms"
                          + "  (" + (avgFrame > 0f ? (1000f / avgFrame).ToString("0") : "-") + " fps avg)");
            sb.AppendLine("  spikes >" + SpikeMs + "ms: " + _spikes
                          + "   of those during a GC: " + _gcDuringSpikes
                          + "   gen0 collections: " + collections
                          + "   managed heap: " + managedMb + " MB");

            foreach (KeyValuePair<string, Stat> entry in ordered)
            {
                double ms = entry.Value.Ticks * toMs;
                string perCall = entry.Value.Calls > 0
                    ? (ms / entry.Value.Calls).ToString("0.000")
                    : "-";

                double worst = entry.Value.MaxTicks * toMs;
                sb.AppendLine("  " + entry.Key.PadRight(34)
                              + entry.Value.Calls.ToString().PadLeft(8) + " calls"
                              + ms.ToString("0.0").PadLeft(10) + " ms"
                              + perCall.PadLeft(9) + " avg"
                              + worst.ToString("0.0").PadLeft(10) + " worst");
            }

            ComboModApi.Log?.LogWarning(sb.ToString().TrimEnd());
            ResetCounters();
        }

        private static void ResetCounters()
        {
            Stats.Clear();
            _frames = 0;
            _worstFrameMs = 0f;
            _accumulatedFrameMs = 0f;
            _spikes = 0;
            _gcDuringSpikes = 0;
        }
    }
}
