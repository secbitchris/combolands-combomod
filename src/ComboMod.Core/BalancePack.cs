using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Entities;

namespace ComboMod
{
    /// <summary>
    /// One tuned value read from a pack file.
    /// </summary>
    public struct PackEntry
    {
        public GameTag Tag;
        public bool IsItem;
        public Tuner.Knob Knob;
        public object Value;
        public int Line;
    }

    /// <summary>
    /// A shareable balance pack: a plain text file describing base-stat changes.
    /// <para>
    /// Deliberately not JSON. The point of packs is that someone who does not write code can
    /// author and share one, and an INI-style file is hand-writable, diff-friendly, and needs no
    /// serializer — <c>JsonUtility</c> cannot round-trip dictionaries anyway, and pulling in a
    /// real JSON library to read twenty key/value pairs is not a trade worth making.
    /// </para>
    /// <example>
    /// <code>
    /// # Faster bakeries
    /// [pack]
    /// name = Faster Bakeries
    /// author = someone
    /// version = 1.0
    ///
    /// [building.Bakery]
    /// Cooldown = 3
    /// Money = 5
    ///
    /// [item.Clover]
    /// Multiplier = 2
    /// </code>
    /// </example>
    /// </summary>
    public sealed class BalancePack
    {
        public string Name = "unnamed";
        public string Author = string.Empty;
        public string Version = string.Empty;
        public string Description = string.Empty;

        /// <summary>Absolute path this pack was read from.</summary>
        public string FilePath = string.Empty;

        /// <summary>File name without extension, used when the pack declares no name.</summary>
        public string FileName = string.Empty;

        public readonly List<PackEntry> Entries = new List<PackEntry>();

        /// <summary>
        /// Anything the parser could not use, with line numbers. Surfaced in the panel rather
        /// than only logged: a pack that silently drops half its entries is worse than one that
        /// refuses to load, and pack authors need to see why.
        /// </summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Registrations this pack created, so it can be enabled and disabled as a unit.</summary>
        public readonly List<TuneRegistration> Registrations = new List<TuneRegistration>();

        public bool Enabled
        {
            get
            {
                foreach (TuneRegistration r in Registrations)
                    if (r.Enabled)
                        return true;
                return false;
            }
        }

        /// <summary>
        /// Parse a pack file. Never throws on bad content: unusable lines are collected into
        /// <see cref="Warnings"/> and skipped, so one typo cannot cost the whole pack.
        /// </summary>
        public static BalancePack Parse(string path, string[] lines)
        {
            var pack = new BalancePack
            {
                FilePath = path,
                FileName = Path.GetFileNameWithoutExtension(path),
            };
            pack.Name = pack.FileName;

            string section = string.Empty;
            GameTag sectionTag = GameTag.None;
            bool sectionIsItem = false;
            bool sectionValid = false;

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNumber = i + 1;
                string line = lines[i].Trim();

                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    continue;

                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    if (close < 0)
                    {
                        pack.Warnings.Add("line " + lineNumber + ": unterminated section header");
                        sectionValid = false;
                        continue;
                    }

                    section = line.Substring(1, close - 1).Trim();
                    sectionValid = pack.BeginSection(section, lineNumber, out sectionTag, out sectionIsItem);
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals < 0)
                {
                    pack.Warnings.Add("line " + lineNumber + ": expected 'key = value'");
                    continue;
                }

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();

                if (section.Equals("pack", StringComparison.OrdinalIgnoreCase))
                {
                    pack.ApplyMetadata(key, value);
                    continue;
                }

                if (!sectionValid)
                    continue;

                Tuner.Knob knob = FindKnobByName(key);
                if (knob == null)
                {
                    pack.Warnings.Add("line " + lineNumber + ": unknown stat '" + key + "'");
                    continue;
                }

                object parsed;
                if (!LiveEditor.TryParse(knob, value, out parsed))
                {
                    pack.Warnings.Add(
                        "line " + lineNumber + ": '" + value + "' is not a valid " + knob.Type.Name +
                        " for " + knob.Name);
                    continue;
                }

                pack.Entries.Add(new PackEntry
                {
                    Tag = sectionTag,
                    IsItem = sectionIsItem,
                    Knob = knob,
                    Value = parsed,
                    Line = lineNumber,
                });
            }

            return pack;
        }

        private bool BeginSection(string section, int lineNumber, out GameTag tag, out bool isItem)
        {
            tag = GameTag.None;
            isItem = false;

            if (section.Equals("pack", StringComparison.OrdinalIgnoreCase))
                return false;

            int dot = section.IndexOf('.');
            if (dot < 0)
            {
                Warnings.Add("line " + lineNumber + ": section must be [pack], [building.Name] or [item.Name]");
                return false;
            }

            string kind = section.Substring(0, dot).Trim();
            string tagName = section.Substring(dot + 1).Trim();

            if (kind.Equals("item", StringComparison.OrdinalIgnoreCase))
                isItem = true;
            else if (!kind.Equals("building", StringComparison.OrdinalIgnoreCase))
            {
                Warnings.Add("line " + lineNumber + ": unknown section kind '" + kind + "'");
                return false;
            }

            try
            {
                tag = (GameTag)Enum.Parse(typeof(GameTag), tagName, ignoreCase: true);
            }
            catch
            {
                Warnings.Add("line " + lineNumber + ": '" + tagName + "' is not a known building or item");
                return false;
            }

            // Belt and braces: Enum.Parse happily accepts a bare number, which would smuggle in a
            // non-vanilla tag and break the save-safety guarantee packs are supposed to keep.
            if (!Enum.IsDefined(typeof(GameTag), tag))
            {
                Warnings.Add("line " + lineNumber + ": '" + tagName + "' is not a vanilla tag");
                return false;
            }

            return true;
        }

        private void ApplyMetadata(string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "name": Name = value; break;
                case "author": Author = value; break;
                case "version": Version = value; break;
                case "description": Description = value; break;
                default: Warnings.Add("unknown [pack] key '" + key + "'"); break;
            }
        }

        private static Tuner.Knob FindKnobByName(string name)
        {
            foreach (Tuner.Knob knob in Tuner.Knobs)
                if (string.Equals(knob.Name, name, StringComparison.OrdinalIgnoreCase))
                    return knob;
            return null;
        }

        /// <summary>Render a set of live edits as pack-file text, ready to save and share.</summary>
        public static string Write(
            string name, string author, string version,
            IReadOnlyDictionary<GameTag, Dictionary<string, object>> edits)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Combolands balance pack, exported by ComboMod.");
            sb.AppendLine("# Drop this in BepInEx/config/ComboMod/packs/ to use or share it.");
            sb.AppendLine();
            sb.AppendLine("[pack]");
            sb.AppendLine("name = " + name);
            sb.AppendLine("author = " + author);
            sb.AppendLine("version = " + version);
            sb.AppendLine();

            foreach (KeyValuePair<GameTag, Dictionary<string, object>> entry in edits)
            {
                if (entry.Value.Count == 0)
                    continue;

                sb.AppendLine("[" + (entry.Key.IsItemTag() ? "item." : "building.") + entry.Key + "]");

                foreach (KeyValuePair<string, object> field in entry.Value)
                {
                    Tuner.Knob knob = Tuner.FindKnob(field.Key);
                    if (knob == null)
                        continue;

                    sb.AppendLine(knob.Name + " = " + FormatValue(field.Value));
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string FormatValue(object value)
        {
            if (value is float f)
                return f.ToString("0.####", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
