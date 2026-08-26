using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using Entities;
using GameState.Data;

namespace ComboMod
{
    /// <summary>
    /// Value parsing and C# export for the in-game editor.
    /// <para>
    /// Kept out of <see cref="ModPanel"/> so the IMGUI code stays about layout, and so the
    /// export can be driven from the API without a panel open.
    /// </para>
    /// </summary>
    public static class LiveEditor
    {
        /// <summary>
        /// Parse editor text into a knob's value type. Returns false on anything unparseable so
        /// the panel can keep showing the user's in-progress typing rather than snapping back.
        /// </summary>
        public static bool TryParse(Tuner.Knob knob, string text, out object value)
        {
            value = null;
            if (knob == null || string.IsNullOrEmpty(text))
                return false;

            // Mid-typing states that are not yet numbers. Treat as "not ready", not an error.
            if (text == "-" || text == "." || text == "-.")
                return false;

            if (knob.Type == typeof(int))
            {
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                {
                    value = i;
                    return true;
                }
                return false;
            }

            if (knob.Type == typeof(float))
            {
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                {
                    value = f;
                    return true;
                }
                return false;
            }

            if (knob.Type == typeof(Rarity))
            {
                try
                {
                    value = (Rarity)Enum.Parse(typeof(Rarity), text, ignoreCase: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>Format a value the way the editor and the exporter both want to see it.</summary>
        public static string Format(object value)
        {
            if (value == null)
                return string.Empty;

            if (value is float f)
                return f.ToString("0.####", CultureInfo.InvariantCulture);

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

    }
}
