using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Entities;

namespace ComboMod
{
    /// <summary>
    /// Keeps live edits across restarts.
    /// <para>
    /// Without this, tuning in the panel is lost the moment the game closes unless the user
    /// remembered to press Save as pack. That is a bad trade: the work is real, the button is
    /// easy to miss, and nothing warns you. Losing an hour of tuning to a habit you had not
    /// learned yet is the kind of thing people stop using a tool over.
    /// </para>
    /// <para>
    /// Stored in the pack format, in the packs folder, but with a leading underscore so it is
    /// visibly ComboMod's own file and sorts to the top. It is deliberately <b>not</b> loaded as
    /// a pack: it is replayed through <c>SetOverride</c> so it stays in the live-edit layer and
    /// keeps winning over packs, which is where the user's most recent intent belongs.
    /// </para>
    /// </summary>
    internal static class LiveEditStore
    {
        private const string FileName = "_live-edits.pack";

        /// <summary>Seconds of quiet before a write. Typing in a field should not hit the disk per keystroke.</summary>
        private const float WriteDelay = 2f;

        private static bool _dirty;
        private static float _quietFor;
        private static bool _loaded;

        internal static string Path => System.IO.Path.Combine(PackLoader.PacksDirectory, FileName);

        /// <summary>Note that edits changed. The write happens once things go quiet.</summary>
        internal static void MarkDirty()
        {
            _dirty = true;
            _quietFor = 0f;
        }

        /// <summary>Drive the debounce. Called once a frame.</summary>
        internal static void Tick(float deltaSeconds)
        {
            if (!_dirty)
                return;

            _quietFor += deltaSeconds;
            if (_quietFor >= WriteDelay)
                Flush();
        }

        /// <summary>Write now, if there is anything to write. Also called on quit.</summary>
        internal static void Flush()
        {
            if (!_dirty)
                return;

            _dirty = false;
            _quietFor = 0f;

            try
            {
                string directory = PackLoader.PacksDirectory;
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                int count = 0;
                foreach (KeyValuePair<GameTag, Dictionary<string, object>> entry in ComboModApi.Overrides)
                    count += entry.Value.Count;

                if (count == 0)
                {
                    // Clearing every edit should remove the file, not leave an empty one that
                    // loads next time and reports "0 changes" as though it had failed.
                    if (File.Exists(Path))
                    {
                        File.Delete(Path);
                        ComboModApi.Log?.LogInfo("Live edits cleared; removed " + FileName);
                    }
                    return;
                }

                File.WriteAllText(Path, BalancePack.Write(
                    "Live edits", "in-game editor", "auto", ComboModApi.Overrides));

                ComboModApi.Log?.LogInfo("Saved " + count + " live edit(s) to " + FileName);
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogError("Could not save live edits: " + ex.Message);
            }
        }

        /// <summary>
        /// Replay a previous session's edits.
        /// <para>
        /// Runs once, after packs have loaded, so live edits are re-applied on top of them and
        /// the layering matches what the user saw when they made the edits.
        /// </para>
        /// </summary>
        internal static void LoadOnce()
        {
            if (_loaded)
                return;

            _loaded = true;

            try
            {
                if (!File.Exists(Path))
                    return;

                BalancePack saved = BalancePack.Parse(Path, File.ReadAllLines(Path));

                int restored = 0;
                foreach (PackEntry entry in saved.Entries)
                {
                    ComboModApi.SetOverride(entry.Tag, entry.IsItem, entry.Knob.Field, entry.Value);
                    restored++;
                }

                // Restoring is not itself a change worth writing back out.
                _dirty = false;

                if (restored > 0)
                    ComboModApi.Log?.LogInfo("Restored " + restored + " live edit(s) from " + FileName);

                foreach (string warning in saved.Warnings)
                    ComboModApi.Log?.LogWarning("  " + FileName + " " + warning);
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogError("Could not restore live edits: " + ex.Message);
            }
        }

        /// <summary>True when the reserved file is the one being looked at, so packs can skip it.</summary>
        internal static bool IsReserved(string path)
        {
            return string.Equals(
                System.IO.Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
