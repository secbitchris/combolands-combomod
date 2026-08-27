using System;
using System.Collections.Generic;
using Entities;
using Library;
using UI;
using ComboMod;

namespace ComboMod.Cheats
{
    /// <summary>
    /// Putting things into the player's inventory.
    /// <para>
    /// <b>This writes to the save.</b> Heirlooms, gems, tomes and consumables are all stored in
    /// <c>SerializedGameState</c> by <c>GameTag</c>. Because those are vanilla tags an unmodded
    /// client reads the save back fine and nothing is corrupted — but an item given here is a
    /// real item in a real run, and removing ComboMod will not take it away.
    /// </para>
    /// </summary>
    public static class Inventory
    {
        /// <summary>What a tag can be given as, which decides which panel receives it.</summary>
        public enum GiveKind
        {
            /// <summary>Nothing sensible can be done with this tag.</summary>
            None,

            /// <summary>An item, heirloom, gem or tome. Goes to the heirlooms panel.</summary>
            Heirloom,

            /// <summary>A potion, favour or building supply. Goes to the consumables panel.</summary>
            Consumable,

            /// <summary>A building, given as a blueprint card in the consumables panel.</summary>
            Blueprint,
        }

        /// <summary>Work out how a tag would be given, without giving it.</summary>
        public static GiveKind ClassifyTag(GameTag tag)
        {
            if (tag == GameTag.None)
                return GiveKind.None;

            if (tag.IsItemTag())
                return GiveKind.Heirloom;

            if (tag.IsConsumableTag())
                return GiveKind.Consumable;

            // AddConsumable has a building branch: a building handed to the consumables panel
            // becomes a blueprint card, stacking onto an existing card of the same type.
            if (tag.IsBuildingTag())
                return GiveKind.Blueprint;

            return GiveKind.None;
        }

        /// <summary>True when the relevant panel exists and has room.</summary>
        public static bool CanGive(GameTag tag, out string reason)
        {
            reason = string.Empty;

            switch (ClassifyTag(tag))
            {
                case GiveKind.Heirloom:
                    if (!MonoSingleton<HeirloomsPanel>.HasInstance)
                    {
                        reason = "no run loaded";
                        return false;
                    }

                    // HasSpace also accounts for the Jewellery Box and Bookshelf, which take
                    // gems and tomes even when the main slots are full.
                    if (!MonoSingleton<HeirloomsPanel>.Instance.HasSpace(tag))
                    {
                        reason = "no free heirloom slot";
                        return false;
                    }

                    return true;

                case GiveKind.Consumable:
                case GiveKind.Blueprint:
                    if (!MonoSingleton<ConsumablesPanel>.HasInstance)
                    {
                        reason = "no run loaded";
                        return false;
                    }

                    // showText:false so a failed check does not flash the game's own
                    // "no space" banner at the player from a panel interaction.
                    if (!MonoSingleton<ConsumablesPanel>.Instance.HasSpace(showText: false))
                    {
                        reason = "no free consumable slot";
                        return false;
                    }

                    return true;

                default:
                    reason = "not an item, consumable or building";
                    return false;
            }
        }

        /// <summary>
        /// Give one of <paramref name="tag"/> to the player. Returns false with a reason rather
        /// than throwing, so the panel can say why nothing happened.
        /// </summary>
        public static bool Give(GameTag tag, out string reason)
        {
            if (!CanGive(tag, out reason))
                return false;

            try
            {
                switch (ClassifyTag(tag))
                {
                    case GiveKind.Heirloom:
                        // Routes gems to the Jewellery Box and tomes to the Bookshelf on its own.
                        MonoSingleton<HeirloomsPanel>.Instance.AddHeirloom(tag);
                        break;

                    case GiveKind.Consumable:
                    case GiveKind.Blueprint:
                        MonoSingleton<ConsumablesPanel>.Instance.AddConsumable(tag);
                        break;

                    default:
                        reason = "not givable";
                        return false;
                }
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                ComboModApi.Log?.LogError("Giving " + tag + " failed: " + ex);
                return false;
            }

            ComboModApi.Log?.LogInfo("Gave " + tag + " (" + ClassifyTag(tag) + ")");
            return true;
        }

        /// <summary>
        /// Every consumable tag the game defines.
        /// <para>
        /// GameTagExtensions ships GetAllBuildingTags and GetAllItemTags but no consumable
        /// equivalent, so this filters the enum directly.
        /// </para>
        /// </summary>
        public static List<GameTag> GetAllConsumableTags()
        {
            var tags = new List<GameTag>();

            foreach (GameTag tag in Enum.GetValues(typeof(GameTag)))
                if (tag.IsConsumableTag())
                    tags.Add(tag);

            tags.Sort((a, b) => string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase));
            return tags;
        }

        /// <summary>Item and heirloom tags, sorted by name.</summary>
        public static List<GameTag> GetAllItemTags()
        {
            var tags = new List<GameTag>(GameTagExtensions.GetAllItemTags());
            tags.Sort((a, b) => string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase));
            return tags;
        }
    }
}
