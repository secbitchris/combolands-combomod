using System;
using System.Collections.Generic;
using ComboMod;
using Entities;
using Library;
using UI;
using UnityEngine;
using UnityEngine.UI;

namespace ComboMod.Cheats
{
    /// <summary>
    /// Removing things from the inventory, and removing the slots themselves.
    /// <para>
    /// The game has no <c>RemoveSlot</c> — slots only ever grow — and its panels lay out for a
    /// handful of slots, so adding many makes the normal inventory UI unusable. Since ComboMod
    /// is what let you add them, it has to be able to take them back.
    /// </para>
    /// <para>
    /// Both <c>HeirloomsPanel.Slots</c> and <c>ConsumablesPanel.Slots</c> return the live backing
    /// list rather than a copy, so a slot can be removed by taking it out of that list and
    /// destroying its GameObject. Slot count is serialized, so this changes the save the same way
    /// adding did.
    /// </para>
    /// </summary>
    public static class InventoryManager
    {
        /// <summary>The counts a fresh run starts with. Going below these is allowed but flagged.</summary>
        public const int VanillaHeirloomSlots = 6;
        public const int VanillaConsumableSlots = 3;

        // ---- reading what is currently held ----

        /// <summary>Heirlooms currently equipped, including the Jewellery Box and Bookshelf.</summary>
        public static List<ItemHeirloom> HeldItems()
        {
            if (!MonoSingleton<ItemHeirloomController>.HasInstance)
                return new List<ItemHeirloom>();

            return new List<ItemHeirloom>(
                MonoSingleton<ItemHeirloomController>.Instance.ItemsIncludingExtraHeirloomPanels);
        }

        /// <summary>Consumables currently held.</summary>
        public static List<Consumable> HeldConsumables()
        {
            if (!MonoSingleton<ConsumablesPanel>.HasInstance)
                return new List<Consumable>();

            return MonoSingleton<ConsumablesPanel>.Instance.GetCurrentConsumables()
                   ?? new List<Consumable>();
        }

        // ---- removing contents ----

        /// <summary>Remove an heirloom outright, no refund.</summary>
        public static bool RemoveItem(ItemHeirloom item)
        {
            if (item == null || !MonoSingleton<ItemHeirloomController>.HasInstance)
                return false;

            try
            {
                MonoSingleton<ItemHeirloomController>.Instance.RemoveItem(item);
                ComboModApi.Log?.LogInfo("Removed heirloom " + item.Tag);
                return true;
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogError("Removing heirloom failed: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Sell an heirloom, which pays out like the shop would.
        /// <para>
        /// <c>triggerOnSell: false</c> — a sale triggered from a debug panel should not fire
        /// on-sell effects that the player did not actually cause.
        /// </para>
        /// </summary>
        public static bool SellItem(ItemHeirloom item)
        {
            if (item == null || !MonoSingleton<ItemHeirloomController>.HasInstance)
                return false;

            try
            {
                MonoSingleton<ItemHeirloomController>.Instance.SellItem(item, triggerOnSell: false);
                ComboModApi.Log?.LogInfo("Sold heirloom " + item.Tag);
                return true;
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogError("Selling heirloom failed: " + ex);
                return false;
            }
        }

        /// <summary>Remove a consumable card.</summary>
        public static bool RemoveConsumable(Consumable consumable)
        {
            if (consumable == null || !MonoSingleton<ConsumablesPanel>.HasInstance)
                return false;

            try
            {
                MonoSingleton<ConsumablesPanel>.Instance.DestroyConsumable(consumable);
                ComboModApi.Log?.LogInfo("Removed consumable " + consumable.GameTag);
                return true;
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogError("Removing consumable failed: " + ex);
                return false;
            }
        }

        // ---- removing slots ----

        /// <summary>
        /// Remove one empty heirloom slot, last first. Returns false when every slot is occupied
        /// or only one remains — emptying a slot is the caller's job, so nothing is destroyed by
        /// surprise.
        /// </summary>
        public static bool RemoveHeirloomSlot(out string reason)
        {
            if (!MonoSingleton<HeirloomsPanel>.HasInstance)
            {
                reason = "no run loaded";
                return false;
            }

            HeirloomsPanel panel = MonoSingleton<HeirloomsPanel>.Instance;
            bool removed = RemoveTrailingEmptySlot(panel.Slots, out reason);

            if (removed)
            {
                RebuildLayout(panel.transform);
                ComboModApi.Log?.LogInfo("Heirloom slots now " + panel.SlotCount);
            }

            return removed;
        }

        /// <summary>Remove one empty consumable slot, last first.</summary>
        public static bool RemoveConsumableSlot(out string reason)
        {
            if (!MonoSingleton<ConsumablesPanel>.HasInstance)
            {
                reason = "no run loaded";
                return false;
            }

            ConsumablesPanel panel = MonoSingleton<ConsumablesPanel>.Instance;
            bool removed = RemoveTrailingEmptySlot(panel.Slots, out reason);

            if (removed)
            {
                RebuildLayout(panel.transform);
                ComboModApi.Log?.LogInfo("Consumable slots now " + panel.Slots.Count);
            }

            return removed;
        }

        private static bool RemoveTrailingEmptySlot(List<UiObjectSlot> slots, out string reason)
        {
            if (slots == null || slots.Count == 0)
            {
                reason = "no slots";
                return false;
            }

            if (slots.Count <= 1)
            {
                reason = "cannot remove the last slot";
                return false;
            }

            for (int i = slots.Count - 1; i >= 0; i--)
            {
                if (slots[i] == null)
                {
                    slots.RemoveAt(i);
                    reason = string.Empty;
                    return true;
                }

                if (slots[i].CurrentUiObject != null)
                    continue;

                UiObjectSlot slot = slots[i];
                slots.RemoveAt(i);
                UnityEngine.Object.Destroy(slot.gameObject);
                reason = string.Empty;
                return true;
            }

            reason = "every slot is occupied";
            return false;
        }

        /// <summary>
        /// Force the panel to re-flow. The game does this on a delay after adding a slot; doing
        /// it immediately on removal avoids a frame where the layout still reserves the space.
        /// </summary>
        private static void RebuildLayout(Transform panel)
        {
            try
            {
                var rect = panel as RectTransform;
                if (rect != null)
                    LayoutRebuilder.MarkLayoutForRebuild(rect);
            }
            catch (Exception ex)
            {
                ComboModApi.Log?.LogWarning("Layout rebuild failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Take heirloom slots down to a target, emptying nothing. Returns how many were actually
        /// removed, which can be fewer than asked when slots are occupied.
        /// </summary>
        public static int TrimHeirloomSlots(int target)
        {
            int removed = 0;
            string reason;

            while (MonoSingleton<HeirloomsPanel>.HasInstance
                   && MonoSingleton<HeirloomsPanel>.Instance.SlotCount > target
                   && RemoveHeirloomSlot(out reason))
            {
                removed++;
            }

            return removed;
        }

        /// <summary>Take consumable slots down to a target. Same caveat as heirlooms.</summary>
        public static int TrimConsumableSlots(int target)
        {
            int removed = 0;
            string reason;

            while (MonoSingleton<ConsumablesPanel>.HasInstance
                   && MonoSingleton<ConsumablesPanel>.Instance.Slots.Count > target
                   && RemoveConsumableSlot(out reason))
            {
                removed++;
            }

            return removed;
        }
    }
}
