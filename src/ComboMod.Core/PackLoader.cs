using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Entities;

namespace ComboMod
{
    /// <summary>
    /// Discovers balance packs on disk and registers them as tunes.
    /// <para>
    /// Packs live under <c>BepInEx/config/ComboMod/packs/</c> rather than beside the plugin, so
    /// that updating or reinstalling ComboMod never deletes a user's own tuning.
    /// </para>
    /// </summary>
    public static class PackLoader
    {
        public const string Extension = ".pack";

        private static readonly List<BalancePack> LoadedPacks = new List<BalancePack>();

        /// <summary>Every pack found on the last scan, in load order.</summary>
        public static IReadOnlyList<BalancePack> Packs => LoadedPacks;

        /// <summary>Directory packs are read from. Created on first run.</summary>
        public static string PacksDirectory =>
            Path.Combine(Path.Combine(Paths.ConfigPath, "ComboMod"), "packs");

        /// <summary>
        /// Scan the packs directory and register everything found, replacing any packs loaded
        /// previously. Safe to call at runtime — the panel's Reload button uses it.
        /// </summary>
        public static void LoadAll()
        {
            UnregisterAll();

            string directory = PacksDirectory;

            try
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    WriteStarterFiles(directory);
                    ComboModApi.Log?.LogInfo("Created packs directory at " + directory);
                }
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogError("Could not create packs directory: " + ex.Message);
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*" + Extension);
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogError("Could not read packs directory: " + ex.Message);
                return;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
                LoadOne(file);

            if (LoadedPacks.Count == 0)
                ComboModApi.Log?.LogInfo("No balance packs found in " + directory);
            else
                ComboModApi.Log?.LogInfo("Loaded " + LoadedPacks.Count + " balance pack(s).");

            ComboModApi.Reapply();
        }

        private static void LoadOne(string file)
        {
            BalancePack pack;
            try
            {
                pack = BalancePack.Parse(file, File.ReadAllLines(file));
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogError("Could not read " + Path.GetFileName(file) + ": " + ex.Message);
                return;
            }

            // Group a pack's entries by tag so each building becomes one registration. That keeps
            // the panel readable and lets a whole building's changes be toggled together.
            var byTag = new Dictionary<GameTag, List<PackEntry>>();
            foreach (PackEntry entry in pack.Entries)
            {
                if (!byTag.TryGetValue(entry.Tag, out List<PackEntry> list))
                {
                    list = new List<PackEntry>();
                    byTag[entry.Tag] = list;
                }
                list.Add(entry);
            }

            foreach (KeyValuePair<GameTag, List<PackEntry>> group in byTag)
            {
                List<PackEntry> entries = group.Value;
                bool isItem = entries[0].IsItem;

                TuneRegistration registration = ComboModApi.RegisterFromPack(
                    group.Key, isItem, pack.Name, tuner =>
                    {
                        foreach (PackEntry entry in entries)
                            tuner.SetByField(entry.Knob.Field, entry.Value);
                    });

                pack.Registrations.Add(registration);
            }

            LoadedPacks.Add(pack);

            string label = pack.Name + (pack.Version.Length > 0 ? " v" + pack.Version : string.Empty);
            ComboModApi.Log?.LogInfo(
                "Pack '" + label + "': " + pack.Entries.Count + " change(s) across " +
                byTag.Count + " piece(s)" +
                (pack.Warnings.Count > 0 ? ", " + pack.Warnings.Count + " warning(s)" : string.Empty));

            foreach (string warning in pack.Warnings)
                ComboModApi.Log?.LogWarning("  " + Path.GetFileName(file) + " " + warning);
        }

        /// <summary>Drop every pack registration, leaving code-registered tunes alone.</summary>
        public static void UnregisterAll()
        {
            foreach (BalancePack pack in LoadedPacks)
                foreach (TuneRegistration registration in pack.Registrations)
                    ComboModApi.Unregister(registration);

            LoadedPacks.Clear();
        }

        /// <summary>Enable or disable every registration belonging to one pack, then re-apply.</summary>
        public static void SetEnabled(BalancePack pack, bool enabled)
        {
            foreach (TuneRegistration registration in pack.Registrations)
                registration.Enabled = enabled;

            ComboModApi.Reapply();
            ComboModApi.Log?.LogInfo("Pack '" + pack.Name + "' " + (enabled ? "enabled" : "disabled") + ".");
        }

        /// <summary>Save live edits as a pack file and reload, so it behaves like any other pack.</summary>
        public static string SaveLiveEditsAsPack(string name, string author)
        {
            try
            {
                if (!Directory.Exists(PacksDirectory))
                    Directory.CreateDirectory(PacksDirectory);

                string safe = MakeSafeFileName(name);
                string path = Path.Combine(PacksDirectory, safe + Extension);
                File.WriteAllText(path, BalancePack.Write(name, author, "1.0", ComboModApi.Overrides));

                ComboModApi.Log?.LogInfo("Wrote pack to " + path);
                return path;
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogError("Could not write pack: " + ex);
                return null;
            }
        }

        private static string MakeSafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder();

            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            string result = sb.ToString().Trim();
            return result.Length == 0 ? "pack" : result;
        }

        private static void WriteStarterFiles(string directory)
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, "README.txt"),
                    "ComboMod balance packs\n" +
                    "======================\n\n" +
                    "Drop .pack files here. They are plain text - open one in any editor.\n" +
                    "Nothing here changes your save: base stats are never serialized, so removing\n" +
                    "a pack restores vanilla values and saves stay readable without ComboMod.\n\n" +
                    "Format:\n\n" +
                    "  [pack]\n" +
                    "  name = My Rebalance\n" +
                    "  author = you\n" +
                    "  version = 1.0\n\n" +
                    "  [building.Bakery]\n" +
                    "  Cooldown = 3\n" +
                    "  Money = 5\n\n" +
                    "  [item.Clover]\n" +
                    "  Multiplier = 2\n\n" +
                    "Section names are [building.Tag] or [item.Tag] using the game's own names -\n" +
                    "browse them in the ComboMod panel (F6). Stat names are the ones the panel\n" +
                    "shows: Cooldown, Range, BuyPrice, ActivationCount, ActivationChance,\n" +
                    "MultParam, Multiplier, Money, Score, Rerolls, Removes, Dismisses, Enchant,\n" +
                    "StoredValue, RangeModification, CooldownModification, RollChanceMultiplier,\n" +
                    "Rarity.\n\n" +
                    "Lines starting with # are comments. A bad line is skipped with a warning in\n" +
                    "the panel rather than failing the whole pack.\n\n" +
                    "Tune in game with F6, then use Save as pack to write your changes here.\n");

                File.WriteAllText(Path.Combine(directory, "example.pack.disabled"),
                    "# Rename to example.pack to try it.\n" +
                    "# Only files ending in .pack are loaded.\n\n" +
                    "[pack]\n" +
                    "name = Example\n" +
                    "author = ComboMod\n" +
                    "version = 1.0\n" +
                    "description = A couple of harmless changes to show the format.\n\n" +
                    "[building.Bakery]\n" +
                    "Cooldown = 3\n\n" +
                    "[building.Windmill]\n" +
                    "RollChanceMultiplier = 2\n");
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogWarning("Could not write starter files: " + ex.Message);
            }
        }
    }
}
