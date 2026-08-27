using System;
using System.Collections.Generic;
using UnityEngine;

namespace ComboMod.Editor
{
    /// <summary>
    /// Styling and scaling handed to an extension tab, so a companion assembly can draw rows
    /// that match the rest of the panel without duplicating the style setup.
    /// </summary>
    public sealed class PanelContext
    {
        /// <summary>Current UI scale.</summary>
        public float Scale { get; internal set; }

        public GUIStyle Header { get; internal set; }
        public GUIStyle Section { get; internal set; }
        public GUIStyle Body { get; internal set; }
        public GUIStyle Muted { get; internal set; }
        public GUIStyle Highlight { get; internal set; }

        /// <summary>Scale a hardcoded dimension. Always use this for widths and heights.</summary>
        public float S(float v) => v * Scale;
    }

    /// <summary>
    /// A tab contributed by another assembly.
    /// <para>
    /// This exists so ComboMod.Cheats can add its Run and Give tabs without ComboMod.Editor
    /// referencing it. The dependency runs one way — Cheats knows about Editor, never the
    /// reverse — which is what lets someone install the tuning UI without the cheat menu.
    /// </para>
    /// </summary>
    public static class PanelTabs
    {
        public sealed class Entry
        {
            public readonly string Title;
            public readonly Action<PanelContext> Draw;

            internal Entry(string title, Action<PanelContext> draw)
            {
                Title = title;
                Draw = draw;
            }
        }

        private static readonly List<Entry> Extra = new List<Entry>();

        /// <summary>Tabs contributed by companion assemblies, in registration order.</summary>
        public static IReadOnlyList<Entry> Registered => Extra;

        /// <summary>
        /// Add a tab to the panel. Call from a plugin's Awake; the panel picks it up on the next
        /// repaint, so ordering against the panel's own construction does not matter.
        /// </summary>
        public static void Register(string title, Action<PanelContext> draw)
        {
            if (string.IsNullOrEmpty(title) || draw == null)
                return;

            foreach (Entry existing in Extra)
                if (existing.Title == title)
                    return;

            Extra.Add(new Entry(title, draw));
            ComboModApi.Log?.LogInfo("Panel tab registered: " + title);
        }
    }
}
