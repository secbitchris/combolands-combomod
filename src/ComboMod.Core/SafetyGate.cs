using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx.Logging;
using Entities;
using GameState;
using UI;
using UnityEngine;

namespace ComboMod
{
    /// <summary>
    /// Checks that the running game still looks like something ComboMod can drive.
    /// <para>
    /// Everything here depends on private field names inside Assembly-CSharp, which are not API
    /// and can change in any patch. A hash tells you the build moved; it does not tell you
    /// whether anything <i>you rely on</i> moved. So the hash is only the first question — the
    /// second, and the one that actually decides whether the mod works, is whether every member
    /// ComboMod reflects on is still present.
    /// </para>
    /// </summary>
    public static class SafetyGate
    {
        /// <summary>A build that has been checked by hand and works.</summary>
        public struct KnownBuild
        {
            public readonly string BuildId;
            public readonly string Hash;
            public readonly string Note;

            public KnownBuild(string buildId, string hash, string note)
            {
                BuildId = buildId;
                Hash = hash;
                Note = note;
            }
        }

        /// <summary>
        /// Builds verified to work. A list rather than a single hash: the game patches often, and
        /// scaring users on every patch teaches them to ignore the warning that matters.
        /// </summary>
        public static readonly KnownBuild[] KnownBuilds =
        {
            new KnownBuild("24930533", "29930e2cd7d0c21079046a6ac6555ec2fb85472b1798ccd0962102627f85cd0e",
                "2026-08-25. Original audit."),
            new KnownBuild("24951781", "eff64c97f400b7410a0b9485f20d99c0da0521149a0afc650a1449b48c2188a8",
                "2026-08-26. Added a Compendium screen; nothing ComboMod uses changed."),
        };

        /// <summary>Hash of the Assembly-CSharp.dll actually loaded, or null if unreadable.</summary>
        public static string CurrentAssemblyHash { get; private set; }

        /// <summary>The known build that matched, if any.</summary>
        public static KnownBuild? MatchedBuild { get; private set; }

        /// <summary>True when the running assembly is one this mod has been verified against.</summary>
        public static bool Matches => MatchedBuild.HasValue;

        /// <summary>Members ComboMod reflects on that are missing from this build. Empty is good.</summary>
        public static IReadOnlyList<string> MissingMembers => Missing;

        private static readonly List<string> Missing = new List<string>();

        /// <summary>Build id of the newest known-good build, for messages.</summary>
        public static string AuditedBuildId => KnownBuilds[KnownBuilds.Length - 1].BuildId;

        /// <summary>
        /// Hash the game assembly, then check that every reflected member still exists.
        /// </summary>
        /// <param name="refuseOnMismatch">
        /// When true, an unrecognised build stops the mod loading. Off by default, because an
        /// unrecognised build with a clean integrity check is the normal case after a patch.
        /// </param>
        /// <returns>True if it is safe to proceed.</returns>
        public static bool Verify(ManualLogSource log, bool refuseOnMismatch)
        {
            CurrentAssemblyHash = TryHashGameAssembly(log);
            MatchedBuild = FindKnownBuild(CurrentAssemblyHash);
            RunIntegrityCheck();

            if (Matches)
            {
                log.LogInfo("Game matches known build " + MatchedBuild.Value.BuildId + ".");
                return true;
            }

            log.LogWarning("This Combolands build is not one ComboMod has been verified against.");
            if (CurrentAssemblyHash != null)
                log.LogWarning("  Assembly-CSharp.dll sha256: " + CurrentAssemblyHash);
            log.LogWarning("  Newest verified build: " + AuditedBuildId);

            // The integrity check is the answer that matters. An unknown hash with everything
            // present means the devs changed something ComboMod does not touch.
            if (Missing.Count == 0)
            {
                log.LogWarning(
                    "Integrity check passed: all " + CheckedMemberCount +
                    " members ComboMod uses are present, so it should work normally.");
            }
            else
            {
                log.LogError("Integrity check FAILED. Missing " + Missing.Count + " member(s):");
                foreach (string member in Missing)
                    log.LogError("  " + member);
                log.LogError("Tunes touching these will be skipped and reported by name.");
            }

            if (refuseOnMismatch)
            {
                log.LogError("Refusing to patch because RefuseOnVersionMismatch is enabled.");
                return false;
            }

            return true;
        }

        /// <summary>How many members the integrity check looks for.</summary>
        public static int CheckedMemberCount { get; private set; }

        /// <summary>
        /// Confirm every field and method ComboMod reflects on still exists.
        /// <para>
        /// This is what turns a version warning into a useful statement. Without it, "the build
        /// changed" and "the mod is broken" look identical to a user.
        /// </para>
        /// </summary>
        private static void RunIntegrityCheck()
        {
            Missing.Clear();
            int checkedCount = 0;

            // Base-stat fields, the bulk of the surface.
            foreach (Tuner.Knob knob in Tuner.Knobs)
            {
                checkedCount++;
                if (!HasField(typeof(_GamePieceBehaviour), knob.Field))
                    Missing.Add("_GamePieceBehaviour." + knob.Field + " (" + knob.Name + ")");
            }

            checkedCount++;
            if (!HasField(typeof(_GamePieceBehaviour), "_behaviourType"))
                Missing.Add("_GamePieceBehaviour._behaviourType");

            // Run-state members, used by the Run tab only.
            checkedCount++;
            if (!HasField(typeof(GameController), "_weeksAllowed"))
                Missing.Add("GameController._weeksAllowed (weeks editing)");

            checkedCount++;
            if (!HasField(typeof(ScoreController), "<Money>k__BackingField"))
                Missing.Add("ScoreController.Money backing field (money editing)");

            // Methods the panel and RunState call directly. These are public, so a rename would
            // be a compile error rather than a runtime surprise -- but a build swapped in
            // underneath us would still hit it.
            checkedCount++;
            if (!HasMethod(typeof(GameController), "DebugChangeWeeks"))
                Missing.Add("GameController.DebugChangeWeeks");

            checkedCount++;
            if (!HasMethod(typeof(ScorePanel), "UpdateGoalText"))
                Missing.Add("ScorePanel.UpdateGoalText (HUD refresh)");

            CheckedMemberCount = checkedCount;
        }

        private static bool HasField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
                if (t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) != null)
                    return true;
            return false;
        }

        private static bool HasMethod(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
                if (t.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) != null)
                    return true;
            return false;
        }

        private static KnownBuild? FindKnownBuild(string hash)
        {
            if (hash == null)
                return null;

            foreach (KnownBuild build in KnownBuilds)
                if (string.Equals(build.Hash, hash, StringComparison.OrdinalIgnoreCase))
                    return build;

            return null;
        }

        private static string TryHashGameAssembly(ManualLogSource log)
        {
            try
            {
                // Application.dataPath is <game>/Combolands_Data at runtime.
                string path = Path.Combine(
                    Path.Combine(Application.dataPath, "Managed"), "Assembly-CSharp.dll");

                if (!File.Exists(path))
                {
                    log.LogWarning("Assembly-CSharp.dll not found at " + path);
                    return null;
                }

                using (var sha = SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("Hashing Assembly-CSharp.dll failed: " + ex.Message);
                return null;
            }
        }
    }
}
