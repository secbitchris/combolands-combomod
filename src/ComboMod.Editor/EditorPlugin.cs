using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace ComboMod.Editor
{
    /// <summary>
    /// ComboMod.Editor: the in-game panel.
    /// <para>
    /// Split out from ComboMod.Core deliberately. Core is headless — it loads packs and applies
    /// tunes with no UI — so someone installing a balance pack is not also handed a money editor
    /// they never asked for. Remove this assembly and packs keep working.
    /// </para>
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(ComboMod.Plugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    public sealed class EditorPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.combolands.combomod.editor";
        public const string PluginName = "ComboMod Editor";
        public const string PluginVersion = "0.2.0";

        private void Awake()
        {
            ConfigEntry<KeyCode> panelKey = Config.Bind(
                "UI", "PanelKey", KeyCode.F6,
                "Key that opens the ComboMod panel.");

            ConfigEntry<KeyCode> abKey = Config.Bind(
                "UI", "AbToggleKey", KeyCode.F7,
                "Key that flips every tune on or off at once, for comparing against vanilla.");

            ConfigEntry<float> uiScale = Config.Bind(
                "UI", "Scale", 0f,
                "Panel size multiplier. 0 means derive it from screen height, which is usually "
                + "what you want: IMGUI draws at a fixed pixel size, so a panel sized for 1080p is "
                + "unreadably small on 1440p or 4K. Any other value pins it.");

            var panel = gameObject.AddComponent<ModPanel>();
            panel.ToggleKey = panelKey.Value;
            panel.AbToggleKey = abKey.Value;

            // 0 stays 0: the panel resolves it on first paint, because Screen.height is not
            // meaningful yet during BepInEx chainloader startup.
            panel.UiScale = uiScale.Value > 0f
                ? Mathf.Clamp(uiScale.Value, ModPanel.MinScale, ModPanel.MaxScale)
                : ModPanel.AutoScale;

            panel.OnScaleChanged = scale =>
            {
                uiScale.Value = scale;
                Config.Save();
            };

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. "
                           + panelKey.Value + " = panel, " + abKey.Value + " = toggle all tunes.");
        }
    }
}
