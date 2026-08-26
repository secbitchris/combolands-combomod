using BepInEx;
using Entities;
using GameState.Data;

namespace ComboMod.SampleTweaks
{
    /// <summary>
    /// A worked example of the Tier 1 surface: three rebalances that between them exercise
    /// every kind of knob (numeric, enum, draft weight) without touching anything serialized.
    /// <para>
    /// Delete this plugin and the game is bit-for-bit vanilla again, including any save
    /// written while it was loaded.
    /// </para>
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(Plugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    public sealed class SampleTweaks : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.combolands.combomod.sampletweaks";
        public const string PluginName = "ComboMod Sample Tweaks";
        public const string PluginVersion = "0.1.0";

        private void Awake()
        {
            // 1. Pure numbers. The Bakery ships at cooldown 5; make it fire noticeably faster
            //    and pay out more. Both are base stats, so neither reaches a save file.
            ComboModApi.Tune(GameTag.Bakery, t =>
            {
                t.Cooldown = 3;
                t.Money = 5;
            });

            // 2. Draft economics. The Windmill is a Bakery prerequisite, so make it show up
            //    more often. RollChanceMultiplier is a plain weight into ChooseBag; 3x means
            //    roughly three times its rarity-derived odds.
            ComboModApi.Tune(GameTag.Windmill, t =>
            {
                t.RollChanceMultiplier = 3f;
            });

            // 3. An enum knob. The Blacksmith ships Rare, so dropping it to Common moves its
            //    base roll weight from 0.05 to 0.70 -- a 14x swing from one assignment.
            //    Note we never write 0 to RollChanceMultiplier to hide something;
            //    ComboModApi.SuppressionWeight exists because a literal 0 makes the entry
            //    unremovable from the draft bag and silently shrinks the draft.
            ComboModApi.Tune(GameTag.Blacksmith, t =>
            {
                t.Rarity = Rarity.Common;
            });

            Logger.LogInfo(PluginName + " registered 3 tunes.");
        }
    }
}
