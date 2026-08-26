using System;
using System.Collections.Generic;
using Entities;
using ComboMod;
using UnityEngine;

namespace ComboMod.Editor
{
    /// <summary>
    /// In-game editor. Two views: the tunes mods registered, and a browser over every building
    /// and item where any base stat can be edited live.
    /// <para>
    /// Drawn with IMGUI, which works here because the game keeps legacy input enabled — its own
    /// CheatsHandler uses UnityEngine.Input, and UnityEngine.InputLegacyModule ships in the
    /// build. A new-Input-System-only project would need a different approach.
    /// </para>
    /// </summary>
    public sealed class ModPanel : MonoBehaviour
    {
        private enum Tab { Tunes, Browse, Run, Packs }

        private const int WindowId = 0x0C0B0;
        private const float WindowWidth = 660f;
        private const float WindowHeight = 620f;

        internal KeyCode ToggleKey = KeyCode.F6;

        /// <summary>
        /// Flips every tune between all-on and all-off without opening the panel, so a balance
        /// change can be compared against vanilla in place.
        /// </summary>
        internal KeyCode AbToggleKey = KeyCode.F7;

        /// <summary>
        /// UI scale. IMGUI renders at a fixed pixel size regardless of resolution, so on a 1440p
        /// or 4K display the default is unreadably small. Raising this bumps every font size and
        /// every hardcoded dimension together, which keeps text crisp — scaling GUI.matrix
        /// instead would stretch the glyph bitmaps and just make them blurry.
        /// </summary>
        internal float UiScale = 1f;

        internal const float MinScale = 0.8f;
        internal const float MaxScale = 3f;

        /// <summary>
        /// Set UiScale to this to derive it from screen height on first paint.
        /// <para>
        /// It has to be deferred: plugins load during BepInEx's chainloader, before the game
        /// window is sized, and Screen.height reports a placeholder there (184 on this machine).
        /// Resolving at Awake produced a scale of 1.0 on a 1440p display.
        /// </para>
        /// </summary>
        internal const float AutoScale = 0f;

        /// <summary>Raised when the user changes scale, so the plugin can persist it.</summary>
        internal Action<float> OnScaleChanged;

        private float _builtForScale = -1f;
        private GUISkin _scaledSkin;

        /// <summary>Scale a hardcoded dimension.</summary>
        private float S(float v) => v * UiScale;

        private bool _open;
        private Tab _tab = Tab.Tunes;
        private Rect _window = new Rect(60f, 60f, WindowWidth, WindowHeight);
        private Vector2 _tuneScroll;
        private Vector2 _listScroll;
        private Vector2 _knobScroll;

        private bool _browsingItems;

        // 0 buildings, 1 items, 2 consumables. Consumables have no behaviour object to edit, so
        // that mode is give-only.
        private int _browseMode;
        private static readonly string[] BrowseModeNames = { "Buildings", "Items", "Consumables" };
        private string _search = string.Empty;
        private GameTagSelection _selection;
        private string _exportedTo;
        private Vector2 _packScroll;
        private string _packName = "My Rebalance";
        private string _packAuthor = string.Empty;
        private string _giveResult = string.Empty;

        // Run-tab text state, same reason as _editBuffer: keep partial typing alive.
        private readonly Dictionary<string, string> _runBuffer = new Dictionary<string, string>();

        // Text state per knob, so a half-typed "-" or "1." survives the next repaint.
        private readonly Dictionary<string, string> _editBuffer = new Dictionary<string, string>();

        private GUIStyle _headerStyle;
        private GUIStyle _sourceStyle;
        private GUIStyle _changeStyle;
        private GUIStyle _mutedStyle;
        private GUIStyle _windowStyle;
        private GUIStyle _editedStyle;
        private Texture2D _backdrop;
        private bool _stylesReady;

        // Restored when the panel closes, so we never leave the game's cursor state altered.
        private bool _priorCursorVisible;
        private CursorLockMode _priorCursorLock;

        private struct GameTagSelection
        {
            public bool HasValue;
            public GameTag Tag;
            public bool IsItem;
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey))
                Toggle();

            if (Input.GetKeyDown(AbToggleKey))
                ToggleAll();
        }

        /// <summary>All-on if anything is off, otherwise all-off.</summary>
        internal void ToggleAll()
        {
            if (ComboModApi.Registrations.Count == 0)
                return;

            if (ComboModApi.EnabledCount == ComboModApi.Registrations.Count)
                ComboModApi.RevertAll();
            else
                ComboModApi.EnableAll();
        }

        internal void Toggle()
        {
            _open = !_open;

            if (_open)
            {
                _priorCursorVisible = Cursor.visible;
                _priorCursorLock = Cursor.lockState;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            else
            {
                Cursor.visible = _priorCursorVisible;
                Cursor.lockState = _priorCursorLock;
            }
        }

        private void OnDestroy()
        {
            if (_backdrop != null)
                Destroy(_backdrop);
        }

        private void OnGUI()
        {
            if (!_open)
                return;

            ResolveAutoScale();
            EnsureStyles();

            // Assigning the skin here is what makes GUI.skin.button / textField / box pick up
            // the scaled font; the hand-rolled styles above only cover labels.
            GUISkin previous = GUI.skin;
            if (_scaledSkin != null)
                GUI.skin = _scaledSkin;

            // GUILayout.Window auto-sizes to content unless both axes are pinned, which is what
            // pushed the footer out over the scroll area on the first build.
            // Never ask for more room than the window has. At scale 2+ the panel is wider than
            // a modest game window and would simply run off the right edge.
            float width = Mathf.Min(S(WindowWidth), Screen.width - S(20f));
            float height = Mathf.Min(S(WindowHeight), Screen.height - S(20f));

            _window = GUILayout.Window(
                WindowId, _window, DrawWindow, "ComboMod", _windowStyle,
                GUILayout.Width(width), GUILayout.Height(height));

            // Keep it draggable but never draggable off-screen, which would strand the panel.
            _window.x = Mathf.Clamp(_window.x, 0f, Mathf.Max(0f, Screen.width - _window.width));
            _window.y = Mathf.Clamp(_window.y, 0f, Mathf.Max(0f, Screen.height - _window.height));

            GUI.skin = previous;
        }

        /// <summary>Turn AutoScale into a real number once the window has a real size.</summary>
        private void ResolveAutoScale()
        {
            if (UiScale > 0f)
                return;

            // Screen.height is trustworthy by the time anything is being drawn.
            UiScale = Mathf.Clamp(Screen.height / 1080f, 1f, MaxScale);
            _stylesReady = false;

            ComboModApi.Log?.LogInfo(
                "Panel UI scale resolved to " + UiScale.ToString("0.00") +
                " (auto from " + Screen.height + "p)");

            OnScaleChanged?.Invoke(UiScale);
        }

        private void SetScale(float value)
        {
            float clamped = Mathf.Clamp(value, MinScale, MaxScale);
            if (Mathf.Approximately(clamped, UiScale))
                return;

            UiScale = clamped;
            _stylesReady = false;
            OnScaleChanged?.Invoke(UiScale);
        }

        private void EnsureStyles()
        {
            if (_stylesReady && Mathf.Approximately(_builtForScale, UiScale))
                return;

            // IMGUI's default window skin is translucent, which is unreadable over the map.
            _backdrop = new Texture2D(1, 1);
            _backdrop.SetPixel(0, 0, new Color(0.07f, 0.09f, 0.09f, 0.97f));
            _backdrop.Apply();

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _backdrop;
            _windowStyle.onNormal.background = _backdrop;
            _windowStyle.normal.textColor = Color.white;
            _windowStyle.onNormal.textColor = Color.white;
            int pad = Mathf.RoundToInt(12f * UiScale);
            int titleBar = Mathf.RoundToInt(24f * UiScale);
            _windowStyle.border = new RectOffset(6, 6, Mathf.RoundToInt(20f * UiScale), 6);
            _windowStyle.padding = new RectOffset(pad, pad, titleBar, Mathf.RoundToInt(10f * UiScale));

            int baseFont = Mathf.Max(9, Mathf.RoundToInt(12f * UiScale));
            int smallFont = Mathf.Max(8, Mathf.RoundToInt(11f * UiScale));

            _headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = baseFont };
            _sourceStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = smallFont };
            _sourceStyle.normal.textColor = new Color(0.55f, 0.80f, 0.74f);
            _changeStyle = new GUIStyle(GUI.skin.label) { fontSize = smallFont };
            _changeStyle.normal.textColor = new Color(0.78f, 0.82f, 0.80f);
            _mutedStyle = new GUIStyle(GUI.skin.label) { fontSize = smallFont, fontStyle = FontStyle.Italic };
            _mutedStyle.normal.textColor = new Color(0.55f, 0.58f, 0.57f);
            _editedStyle = new GUIStyle(GUI.skin.label) { fontSize = smallFont, fontStyle = FontStyle.Bold };
            _editedStyle.normal.textColor = new Color(0.95f, 0.76f, 0.35f);

            // A copy of the active skin with every built-in widget's font scaled. Assigning
            // GUI.skin to this makes GUI.skin.button and friends scale too, which hand-rolled
            // styles alone would not cover.
            int fontSize = Mathf.Max(9, Mathf.RoundToInt(12f * UiScale));
            _scaledSkin = Instantiate(GUI.skin);
            _scaledSkin.label.fontSize = fontSize;
            _scaledSkin.button.fontSize = fontSize;
            _scaledSkin.textField.fontSize = fontSize;
            _scaledSkin.textArea.fontSize = fontSize;
            _scaledSkin.box.fontSize = fontSize;
            _scaledSkin.toggle.fontSize = fontSize;
            _scaledSkin.window.fontSize = fontSize;
            if (_scaledSkin.customStyles != null)
                foreach (GUIStyle custom in _scaledSkin.customStyles)
                    custom.fontSize = fontSize;

            _builtForScale = UiScale;
            _stylesReady = true;
        }

        private void DrawWindow(int id)
        {
            DrawStatus();
            GUILayout.Space(S(4f));
            DrawTabs();
            GUILayout.Space(S(4f));

            if (_tab == Tab.Tunes)
                DrawTunesTab();
            else if (_tab == Tab.Browse)
                DrawBrowseTab();
            else if (_tab == Tab.Run)
                DrawRunTab();
            else
                DrawPacksTab();

            GUILayout.FlexibleSpace();
            GUILayout.Label(
                ToggleKey + " panel  -  " + AbToggleKey + " toggle all  -  base stats are never saved, so every edit is reversible",
                _mutedStyle);

            // Title bar drag. Full-width so the whole strip is grabbable.
            GUI.DragWindow(new Rect(0f, 0f, 10000f, S(20f)));
        }

        private void DrawStatus()
        {
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.Label(
                SafetyGate.Matches
                    ? "Game build " + SafetyGate.AuditedBuildId + " - matches audit"
                    : "Game build differs from audit " + SafetyGate.AuditedBuildId,
                _headerStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label(
                AchievementGuard.IsEngaged ? "Achievements: suppressed" : "Achievements: LIVE",
                AchievementGuard.IsEngaged ? _changeStyle : _headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                ComboModApi.EnabledCount + "/" + ComboModApi.Registrations.Count + " tunes active",
                _changeStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("UI scale " + UiScale.ToString("0.0"), _changeStyle, GUILayout.Width(S(110f)));
            if (GUILayout.Button("-", GUILayout.Width(S(28f))))
                SetScale(UiScale - 0.1f);
            if (GUILayout.Button("+", GUILayout.Width(S(28f))))
                SetScale(UiScale + 0.1f);
            if (GUILayout.Button("Reset", GUILayout.Width(S(56f))))
                SetScale(1f);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private void DrawTabs()
        {
            GUILayout.BeginHorizontal();

            if (GUILayout.Toggle(_tab == Tab.Tunes, "Registered tunes", GUI.skin.button, GUILayout.Height(S(26f))))
                _tab = Tab.Tunes;

            if (GUILayout.Toggle(_tab == Tab.Browse, "Browse & edit", GUI.skin.button, GUILayout.Height(S(26f))))
                _tab = Tab.Browse;

            if (GUILayout.Toggle(_tab == Tab.Run, "Run", GUI.skin.button, GUILayout.Height(S(26f))))
            {
                if (_tab != Tab.Run)
                    _runBuffer.Clear();
                _tab = Tab.Run;
            }

            if (GUILayout.Toggle(_tab == Tab.Packs, "Packs", GUI.skin.button, GUILayout.Height(S(26f))))
                _tab = Tab.Packs;

            GUILayout.EndHorizontal();
        }

        // ---------- registered tunes ----------

        private void DrawTunesTab()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Revert all to vanilla", GUILayout.Height(S(26f))))
                ComboModApi.RevertAll();
            if (GUILayout.Button("Apply all", GUILayout.Height(S(26f))))
                ComboModApi.EnableAll();
            GUILayout.EndHorizontal();

            IReadOnlyList<TuneRegistration> registrations = ComboModApi.Registrations;
            if (registrations.Count == 0)
            {
                GUILayout.Label("Nothing registered yet. Use Browse & edit, or drop a mod DLL in BepInEx/plugins.", _mutedStyle);
                return;
            }

            _tuneScroll = GUILayout.BeginScrollView(_tuneScroll, GUI.skin.box, GUILayout.Height(S(380f)));

            string currentSource = null;
            foreach (TuneRegistration registration in registrations)
            {
                if (registration.Source != currentSource)
                {
                    currentSource = registration.Source;
                    GUILayout.Space(S(6f));
                    GUILayout.Label(currentSource, _sourceStyle);
                }

                DrawRegistration(registration);
            }

            GUILayout.EndScrollView();
        }

        private void DrawRegistration(TuneRegistration registration)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();

            bool wanted = GUILayout.Toggle(
                registration.Enabled,
                "  " + registration.Tag + (registration.IsItem ? "  (item)" : string.Empty));

            if (wanted != registration.Enabled)
            {
                registration.Enabled = wanted;
                // Immediate feedback is the whole point of the panel; re-apply on the spot.
                ComboModApi.Reapply();
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Edit", GUILayout.Width(S(50f)), GUILayout.Height(S(18f))))
            {
                Select(registration.Tag, registration.IsItem);
                _browsingItems = registration.IsItem;
                _tab = Tab.Browse;
            }

            GUILayout.EndHorizontal();

            if (!registration.Enabled)
            {
                GUILayout.Label("      off - vanilla values in use", _mutedStyle);
            }
            else if (registration.LastChanges.Count == 0)
            {
                GUILayout.Label("      no effect - value already matches vanilla", _mutedStyle);
            }
            else
            {
                foreach (FieldChange change in registration.LastChanges)
                    GUILayout.Label("      " + change, _changeStyle);
            }

            GUILayout.EndVertical();
        }

        // ---------- balance packs ----------

        private void DrawPacksTab()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Shareable text files - no compiling", _headerStyle);
            GUILayout.Label(
                "Packs are plain text under BepInEx/config/ComboMod/packs/. Removing one restores "
                + "vanilla values, and saves written with packs active still load without ComboMod.",
                _mutedStyle);
            GUILayout.EndVertical();

            GUILayout.Space(S(4f));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reload packs", GUILayout.Height(S(26f))))
                PackLoader.LoadAll();
            if (GUILayout.Button("Open folder", GUILayout.Height(S(26f))))
                Application.OpenURL("file://" + PackLoader.PacksDirectory);
            GUILayout.EndHorizontal();

            GUILayout.Space(S(4f));
            GUILayout.Label("Save current live edits as a pack", _sourceStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", _changeStyle, GUILayout.Width(S(56f)));
            _packName = GUILayout.TextField(_packName, GUILayout.Width(S(160f)));
            GUILayout.Label("Author", _changeStyle, GUILayout.Width(S(56f)));
            _packAuthor = GUILayout.TextField(_packAuthor, GUILayout.Width(S(120f)));
            GUILayout.EndHorizontal();

            GUILayout.Space(S(6f));

            IReadOnlyList<BalancePack> packs = PackLoader.Packs;
            if (packs.Count == 0)
            {
                GUILayout.Label(
                    "No packs yet. Tune something in Browse & edit, then use Save as pack above.",
                    _mutedStyle);
                return;
            }

            _packScroll = GUILayout.BeginScrollView(_packScroll, GUI.skin.box, GUILayout.Height(S(280f)));

            foreach (BalancePack pack in packs)
                DrawPack(pack);

            GUILayout.EndScrollView();
        }

        private void DrawPack(BalancePack pack)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();

            bool wanted = GUILayout.Toggle(pack.Enabled, "  " + pack.Name);
            if (wanted != pack.Enabled)
                PackLoader.SetEnabled(pack, wanted);

            GUILayout.FlexibleSpace();
            GUILayout.Label(
                pack.Entries.Count + " change" + (pack.Entries.Count == 1 ? string.Empty : "s"),
                _changeStyle);
            GUILayout.EndHorizontal();

            string byline = pack.Author.Length > 0 ? "by " + pack.Author : "no author";
            if (pack.Version.Length > 0)
                byline += "  v" + pack.Version;
            GUILayout.Label("      " + byline, _mutedStyle);

            if (pack.Description.Length > 0)
                GUILayout.Label("      " + pack.Description, _mutedStyle);

            // Parse problems belong in front of the author, not buried in a log file.
            foreach (string warning in pack.Warnings)
                GUILayout.Label("      ! " + warning, _editedStyle);

            GUILayout.EndVertical();
        }

        // ---------- current run ----------

        private void DrawRunTab()
        {
            if (!RunState.Available)
            {
                GUILayout.Label("No run loaded. Start or continue a game first.", _mutedStyle);
                return;
            }

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Some of these persist, some do not", _headerStyle);
            GUILayout.Label(
                "Saved: money and the four consumable counters. Editing those changes GameState.save "
                + "and removing ComboMod will not undo it (nothing is corrupted - they are plain integers).",
                _mutedStyle);
            GUILayout.Label(
                "Not saved: weeks remaining, score, and milestone target. Those reset on reload.",
                _mutedStyle);
            GUILayout.EndVertical();

            GUILayout.Space(S(6f));

            GUILayout.Label("Saved with the run", _sourceStyle);
            DrawRunField("Money", RunState.Money, v => RunState.SetMoney((int)v));

            GUILayout.Space(S(6f));
            GUILayout.Label("Runtime only", _sourceStyle);
            DrawRunField("Weeks remaining", RunState.WeeksRemaining, v => RunState.SetWeeksRemaining((int)v));
            DrawRunField("Score", RunState.Score_, v => RunState.SetScore(v));
            DrawRunField("Milestone target", RunState.ScoreRequired, v => RunState.SetScoreRequired((int)v));

            GUILayout.Space(S(6f));
            GUILayout.Label("Consumables (saved)", _sourceStyle);
            DrawRunField("Rerolls", RunState.Rerolls, v => RunState.SetRerolls((int)v));
            DrawRunField("Removes", RunState.Removes, v => RunState.SetRemoves((int)v));
            DrawRunField("Dismisses", RunState.Dismisses, v => RunState.SetDismisses((int)v));
            DrawRunField("Rewinds", RunState.Rewinds, v => RunState.SetRewinds((int)v));

            GUILayout.Space(S(6f));
            GUILayout.Label("Inventory slots (saved, add-only)", _sourceStyle);
            DrawSlotField("Heirloom slots", RunState.HeirloomSlots,
                RunState.HeirloomSlotSoftCap, RunState.SetHeirloomSlots, RunState.AddHeirloomSlots);
            DrawSlotField("Consumable slots", RunState.ConsumableSlots,
                int.MaxValue, RunState.SetConsumableSlots, RunState.AddConsumableSlots);

            GUILayout.Space(S(4f));
            GUILayout.Label(
                "Money is written directly, bypassing the lifetime gold counters in Unlocks.save.",
                _mutedStyle);
            GUILayout.Label(
                "Slots cannot be removed - the game has no RemoveSlot, so a count only goes up.",
                _mutedStyle);
            GUILayout.Label(
                "To add actual items: RightShift+C+L arms the game's own cheats, then RightShift+I "
                + "(heirlooms), +C (consumables), +B (buildings).",
                _mutedStyle);
        }

        /// <summary>
        /// One run value: live number, an editable field, and +/- nudges. Applies on Enter or
        /// when focus leaves, not per keystroke, because these drive HUD animations.
        /// </summary>
        private void DrawRunField(string label, long current, Action<long> apply)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _changeStyle, GUILayout.Width(S(140f)));
            GUILayout.Label(current.ToString(), _editedStyle, GUILayout.Width(S(90f)));

            string key = label;
            string text;
            if (!_runBuffer.TryGetValue(key, out text))
            {
                text = current.ToString();
                _runBuffer[key] = text;
            }

            _runBuffer[key] = GUILayout.TextField(_runBuffer[key], GUILayout.Width(S(100f)));

            if (GUILayout.Button("Set", GUILayout.Width(S(46f))))
            {
                long parsed;
                if (long.TryParse(_runBuffer[key], out parsed))
                    apply(parsed);
                else
                    _runBuffer[key] = current.ToString();
            }

            if (GUILayout.Button("-10", GUILayout.Width(S(40f))))
            {
                apply(current - 10);
                _runBuffer.Remove(key);
            }

            if (GUILayout.Button("+10", GUILayout.Width(S(40f))))
            {
                apply(current + 10);
                _runBuffer.Remove(key);
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// A slot count: current value, a target field that only accepts increases, and a +1.
        /// Rendered separately from <see cref="DrawRunField"/> because slots are add-only and
        /// the -10 nudge would be a lie.
        /// </summary>
        private void DrawSlotField(
            string label, int current, int softCap, Func<int, bool> setTo, Action<int> add)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _changeStyle, GUILayout.Width(S(140f)));
            GUILayout.Label(
                current + (current > softCap ? " (past cap)" : string.Empty),
                current > softCap ? _editedStyle : _changeStyle,
                GUILayout.Width(S(90f)));

            string key = "slot:" + label;
            string text;
            if (!_runBuffer.TryGetValue(key, out text))
            {
                text = current.ToString();
                _runBuffer[key] = text;
            }

            _runBuffer[key] = GUILayout.TextField(_runBuffer[key], GUILayout.Width(S(100f)));

            if (GUILayout.Button("Set", GUILayout.Width(S(46f))))
            {
                int parsed;
                if (int.TryParse(_runBuffer[key], out parsed))
                    setTo(parsed);
                _runBuffer.Remove(key);
            }

            if (GUILayout.Button("+1", GUILayout.Width(S(40f))))
            {
                add(1);
                _runBuffer.Remove(key);
            }

            GUILayout.EndHorizontal();
        }

        // ---------- browse and edit any value ----------

        private void DrawBrowseTab()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(S(48f)));
            _search = GUILayout.TextField(_search, GUILayout.Width(S(180f)));

            int wanted = GUILayout.Toolbar(_browseMode, BrowseModeNames, GUILayout.Width(S(230f)));
            if (wanted != _browseMode)
            {
                _browseMode = wanted;
                _browsingItems = _browseMode == 1;
                _selection = default(GameTagSelection);
                _editBuffer.Clear();
                _giveResult = string.Empty;
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Save as pack", GUILayout.Width(S(100f))))
            {
                _exportedTo = PackLoader.SaveLiveEditsAsPack(_packName, _packAuthor);
                if (_exportedTo != null)
                {
                    // Clear the live edits so the pack owns them from here, rather than having
                    // the same values applied twice from two sources.
                    ComboModApi.ClearAllOverrides();
                    _editBuffer.Clear();
                    PackLoader.LoadAll();
                }
            }
            if (GUILayout.Button("Clear edits", GUILayout.Width(S(82f))))
            {
                ComboModApi.ClearAllOverrides();
                _editBuffer.Clear();
            }
            GUILayout.EndHorizontal();

            if (_exportedTo != null)
                GUILayout.Label("Exported to " + _exportedTo, _changeStyle);

            GUILayout.BeginHorizontal();
            DrawTagList();
            DrawKnobEditor();
            GUILayout.EndHorizontal();
        }

        private void DrawTagList()
        {
            GUILayout.BeginVertical(GUILayout.Width(S(200f)));

            List<GameTag> tags = _browseMode == 2
                ? Inventory.GetAllConsumableTags()
                : ComboModApi.GetTunableTags(_browsingItems);

            if (tags.Count == 0)
            {
                GUILayout.Label("Behaviours are not built yet.\nStart or load a run first.", _mutedStyle);
                GUILayout.EndVertical();
                return;
            }

            _listScroll = GUILayout.BeginScrollView(_listScroll, GUI.skin.box, GUILayout.Height(S(350f)));

            int shown = 0;
            foreach (GameTag tag in tags)
            {
                string name = tag.ToString();
                if (_search.Length > 0 && name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                bool edited = ComboModApi.Overrides.ContainsKey(tag);
                bool selected = _selection.HasValue && _selection.Tag == tag && _selection.IsItem == _browsingItems;

                if (GUILayout.Toggle(selected, (edited ? "* " : "  ") + name, GUI.skin.button) && !selected)
                    Select(tag, _browsingItems);

                shown++;
            }

            GUILayout.EndScrollView();
            GUILayout.Label(shown + " of " + tags.Count + " shown", _mutedStyle);
            GUILayout.EndVertical();
        }

        private void Select(GameTag tag, bool isItem)
        {
            _selection = new GameTagSelection { HasValue = true, Tag = tag, IsItem = isItem };
            _editBuffer.Clear();
        }

        private void DrawKnobEditor()
        {
            GUILayout.BeginVertical();

            if (!_selection.HasValue)
            {
                GUILayout.Label("Pick something on the left to edit its stats.", _mutedStyle);
                GUILayout.EndVertical();
                return;
            }

            object behaviour = ComboModApi.GetBehaviour(_selection.Tag, _selection.IsItem);
            if (behaviour == null)
            {
                // Consumables, spells and favours carry their data on ConsumableData rather than
                // a _GamePieceBehaviour, so there are no base stats to edit - only giving.
                GUILayout.BeginHorizontal();
                GUILayout.Label(_selection.Tag.ToString(), _headerStyle);
                GUILayout.FlexibleSpace();
                DrawGiveButton(_selection.Tag);
                GUILayout.EndHorizontal();

                if (_giveResult.Length > 0)
                    GUILayout.Label(_giveResult, _mutedStyle);

                GUILayout.Label("No editable base stats for this one.", _mutedStyle);
                GUILayout.EndVertical();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(_selection.Tag.ToString(), _headerStyle);
            GUILayout.FlexibleSpace();
            DrawGiveButton(_selection.Tag);
            GUILayout.EndHorizontal();

            if (_giveResult.Length > 0)
                GUILayout.Label(_giveResult, _mutedStyle);

            _knobScroll = GUILayout.BeginScrollView(_knobScroll, GUI.skin.box, GUILayout.Height(S(328f)));

            foreach (Tuner.Knob knob in Tuner.Knobs)
                DrawKnob(behaviour, knob);

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Give one of the selected tag. The label names what it will actually become, since a
        /// building handed to the consumables panel arrives as a blueprint rather than a building.
        /// </summary>
        private void DrawGiveButton(GameTag tag)
        {
            Inventory.GiveKind kind = Inventory.ClassifyTag(tag);
            if (kind == Inventory.GiveKind.None)
                return;

            string label = kind == Inventory.GiveKind.Blueprint ? "Give blueprint" : "Give " + kind.ToString().ToLowerInvariant();

            string reason;
            bool canGive = Inventory.CanGive(tag, out reason);

            GUI.enabled = canGive;
            if (GUILayout.Button(label, GUILayout.Width(S(120f))))
            {
                string why;
                _giveResult = Inventory.Give(tag, out why)
                    ? "Gave " + tag + "."
                    : "Could not give " + tag + ": " + why;
            }
            GUI.enabled = true;

            if (!canGive)
                GUILayout.Label(reason, _mutedStyle, GUILayout.Width(S(150f)));
        }

        private void DrawKnob(object behaviour, Tuner.Knob knob)
        {
            object live;
            try
            {
                live = Tuner.ReadRaw(behaviour, knob.Field);
            }
            catch (MissingFieldException)
            {
                // The game moved this field; say so rather than silently dropping the row.
                GUILayout.Label(knob.Name + ": missing on this build", _mutedStyle);
                return;
            }

            Dictionary<string, object> fields;
            bool overridden = ComboModApi.Overrides.TryGetValue(_selection.Tag, out fields)
                              && fields.ContainsKey(knob.Field);

            GUILayout.BeginHorizontal();
            GUILayout.Label(knob.Name, overridden ? _editedStyle : _changeStyle, GUILayout.Width(S(148f)));

            string key = knob.Field;
            string text;
            if (!_editBuffer.TryGetValue(key, out text))
            {
                text = LiveEditor.Format(live);
                _editBuffer[key] = text;
            }

            if (knob.Type == typeof(GameState.Data.Rarity))
            {
                // Six values: cycling beats a text field or a dropdown here.
                if (GUILayout.Button(LiveEditor.Format(live), GUILayout.Width(S(104f))))
                {
                    var values = (GameState.Data.Rarity[])Enum.GetValues(typeof(GameState.Data.Rarity));
                    int next = (Array.IndexOf(values, (GameState.Data.Rarity)live) + 1) % values.Length;
                    ComboModApi.SetOverride(_selection.Tag, _selection.IsItem, knob.Field, values[next]);
                }
            }
            else
            {
                string typed = GUILayout.TextField(text, GUILayout.Width(S(104f)));
                if (typed != text)
                {
                    _editBuffer[key] = typed;
                    object parsed;
                    if (LiveEditor.TryParse(knob, typed, out parsed))
                        ComboModApi.SetOverride(_selection.Tag, _selection.IsItem, knob.Field, parsed);
                }
            }

            if (overridden && GUILayout.Button("x", GUILayout.Width(S(22f))))
            {
                ComboModApi.ClearOverride(_selection.Tag, knob.Field);
                _editBuffer.Remove(key);
            }

            GUILayout.EndHorizontal();
        }
    }
}
