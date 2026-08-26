using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx.Logging;
using UnityEngine;

namespace ComboMod
{
    /// <summary>
    /// Version checking for the game assembly this framework was audited against.
    /// <para>
    /// Everything ComboMod does depends on private field names and method signatures inside
    /// Assembly-CSharp. Those are not API and the developers change them freely. When the
    /// hash moves, the correct response is to re-run the audit, not to hope.
    /// </para>
    /// </summary>
    public static class SafetyGate
    {
        /// <summary>
        /// SHA-256 of the Assembly-CSharp.dll this framework was built and verified against
        /// (Combolands build 24951781, patched 2026-08-26). Re-verified after that patch: every
        /// field ComboMod reflects on, both patched methods, and all balance data were unchanged
        /// from build 24930533 — the update only added a Compendium screen.
        /// </summary>
        public const string AuditedAssemblyHash =
            "eff64c97f400b7410a0b9485f20d99c0da0521149a0afc650a1449b48c2188a8";

        /// <summary>The build id shown in Steam for the audited version, for human diagnosis.</summary>
        public const string AuditedBuildId = "24951781";

        /// <summary>Hash of the Assembly-CSharp.dll actually loaded, or null if it could not be read.</summary>
        public static string CurrentAssemblyHash { get; private set; }

        /// <summary>True when the loaded game matches the audited build.</summary>
        public static bool Matches { get; private set; }

        /// <summary>
        /// Compare the running game against the audited build and report.
        /// </summary>
        /// <param name="log">Where to report.</param>
        /// <param name="refuseOnMismatch">
        /// When true, a mismatch returns false and the caller should decline to patch. When
        /// false, a mismatch only logs a warning; reflection failures then surface as
        /// MissingFieldException at tune time, which names the exact field that moved.
        /// </param>
        /// <returns>True if it is safe to proceed.</returns>
        public static bool Verify(ManualLogSource log, bool refuseOnMismatch)
        {
            CurrentAssemblyHash = TryHashGameAssembly(log);

            if (CurrentAssemblyHash == null)
            {
                log.LogWarning("Could not hash Assembly-CSharp.dll; proceeding without a version check.");
                Matches = false;
                return true;
            }

            // The placeholder means nobody has recorded a baseline yet. Report the hash so it
            // can be pasted in, but do not treat an unrecorded baseline as a mismatch.
            if (AuditedAssemblyHash.Replace("0", string.Empty).Length == 0)
            {
                log.LogInfo("No audited hash recorded. Current Assembly-CSharp.dll is " +
                            CurrentAssemblyHash + " (build " + AuditedBuildId + " expected).");
                Matches = false;
                return true;
            }

            Matches = string.Equals(CurrentAssemblyHash, AuditedAssemblyHash, StringComparison.OrdinalIgnoreCase);

            if (Matches)
            {
                log.LogInfo("Game assembly matches the audited build (" + AuditedBuildId + ").");
                return true;
            }

            log.LogWarning("Assembly-CSharp.dll does NOT match the audited build.");
            log.LogWarning("  audited: " + AuditedAssemblyHash);
            log.LogWarning("  running: " + CurrentAssemblyHash);
            log.LogWarning("Field names this framework reflects on may have moved.");

            if (refuseOnMismatch)
            {
                log.LogError("Refusing to patch because RefuseOnVersionMismatch is enabled.");
                return false;
            }

            log.LogWarning("Continuing anyway; any missing field will be reported by name.");
            return true;
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
