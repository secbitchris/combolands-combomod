using System.Collections.Generic;
using Entities;
using GameState;
using HarmonyLib;
using UI;
using Entities.BuildingBehaviours;
using Entities.ItemBehaviours;
using BuildingBehavioursMap = Entities.BuildingBehaviours.BuildingBehaviours;
using ItemBehavioursMap = Entities.ItemBehaviours.ItemBehaviours;

namespace ComboMod
{
    /// <summary>
    /// Optimisations for large boards, each provably equivalent to the code it replaces.
    /// <para>
    /// These are held to a much stricter bar than a rebalance. A tune that is wrong makes the
    /// game play differently and you notice; a "faster" patch that is wrong corrupts scoring
    /// silently. So the only things patched here are ones where the replacement can be shown to
    /// produce identical observable results — not merely to look equivalent.
    /// </para>
    /// <para>
    /// Both patches matter only at scale. On a board of thirty buildings they save nothing
    /// measurable; on a full one they remove a quadratic.
    /// </para>
    /// </summary>
    internal static class PerformancePatches
    {
        /// <summary>Set from config. When false, every patch here falls through to the original.</summary>
        internal static bool Enabled = true;

        /// <summary>
        /// Skip <c>CalculateSumOnScreen</c>, which is dead computation.
        /// <para>
        /// <c>UpdateSumOnScreen</c> exists only to call <c>CalculateSumOnScreen</c>, which sums
        /// <c>RollingSum</c> across every live <c>PointsScorer</c> into the private field
        /// <c>_sumOnScreen</c>. That field has <b>no readers anywhere</b>: not in the class, not
        /// through a property, and the string appears exactly once in Assembly-CSharp, which is
        /// its own metadata entry, so nothing reaches it by reflection either.
        /// </para>
        /// <para>
        /// It is called from <c>PointsScorer</c> every time a scorer ticks, and <c>_scorers</c>
        /// is never pruned during a cascade — so a cascade over N pieces does N passes over N
        /// scorers. That is the quadratic behind the slowdown at high scores, and it computes a
        /// number nobody ever looks at.
        /// </para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ScoreController), nameof(ScoreController.UpdateSumOnScreen))]
        internal static bool SkipDeadSumCalculation()
        {
            // Returning false skips the original entirely.
            return !Enabled;
        }

        /// <summary>
        /// Replace a linear scan with the dictionary lookup it is emulating.
        /// <para>
        /// The original walks <c>_buildingBehaviours</c> comparing each key to the building's
        /// tag and returns on the first match. Dictionary keys are unique, so at most one entry
        /// can match and <c>TryGetValue</c> returns exactly that entry — identical result,
        /// identical side effects, without touching every other entry first.
        /// </para>
        /// <para>
        /// The game already does it this way in <c>GetBehaviourFor</c> on the same dictionary,
        /// so this is the developers' own idiom applied to the path they missed.
        /// </para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildingBehavioursMap), nameof(BuildingBehavioursMap.ProcessTrigger))]
        internal static bool BuildingTriggerLookup(
            BuildingBehavioursMap __instance,
            Building thisBuilding,
            GamePiece triggerSource,
            GamePiece triggerTarget,
            TriggerType trigger,
            NumberObject num,
            GameTag targetTag,
            bool playerInitiated,
            ref float __result)
        {
            if (!Enabled || thisBuilding == null)
                return true;

            Dictionary<GameTag, _BuildingBehaviour> map = __instance.BuildingBehaviourDict;
            if (map == null)
                return true;

            _BuildingBehaviour behaviour;
            __result = map.TryGetValue(thisBuilding.Tag, out behaviour) && behaviour != null
                ? behaviour.Trigger(thisBuilding, triggerSource, triggerTarget, trigger, num, targetTag, playerInitiated)
                : -1f;

            return false;
        }

        /// <summary>Same substitution for item triggers, which has the identical shape.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemBehavioursMap), nameof(ItemBehavioursMap.ProcessTrigger))]
        internal static bool ItemTriggerLookup(
            ItemBehavioursMap __instance,
            ItemHeirloom thisItemHeirloom,
            GamePiece triggerSource,
            GamePiece triggerTarget,
            TriggerType trigger,
            NumberObject num,
            GameTag targetTag,
            bool playerInitiated,
            ref float __result)
        {
            if (!Enabled || thisItemHeirloom == null)
                return true;

            Dictionary<GameTag, _ItemBehaviour> map = __instance.ItemBehavioursDict;
            if (map == null)
                return true;

            _ItemBehaviour behaviour;
            __result = map.TryGetValue(thisItemHeirloom.Tag, out behaviour) && behaviour != null
                ? behaviour.Trigger(thisItemHeirloom, triggerSource, triggerTarget, trigger, num, targetTag, playerInitiated)
                : -1f;

            return false;
        }
    }
}
