using System;
using System.Reflection;
using GameState;
using Library;
using UI;

namespace ComboMod
{
    /// <summary>
    /// Live run values: money, weeks, score, and the consumable counters.
    /// <para>
    /// <b>Not all of these behave the same way, so check before you assume.</b>
    /// </para>
    /// <para>
    /// Serialized into <c>GameState.save</c>, and therefore persistent: <b>money</b>
    /// (MoneyCount) and the four consumable counters (RerollCount, RemoveCount, DismissCount,
    /// RewindsCount). Editing these changes your save; removing ComboMod will not undo it. They
    /// are plain integers with no identity meaning, so an unmodded client still reads the save
    /// and nothing is corrupted.
    /// </para>
    /// <para>
    /// Runtime only, gone on reload: <b>weeks remaining</b>, <b>current score</b>, and the
    /// <b>milestone target</b>. SerializedGameState stores MilestoneIndex and per-milestone
    /// completed scores, but never the week counters, the live score, or the target.
    /// </para>
    /// <para>
    /// Money is written straight to the backing field rather than through
    /// <c>ScoreController.ChangeMoney</c> on purpose. That method also feeds
    /// <c>RunStatsController.EarnedGold</c> and <c>UnlockState.OverallStats.EarnGold</c>, which
    /// are lifetime counters in the permanent <c>Unlocks.save</c>. Editing money should not
    /// inflate your career gold total.
    /// </para>
    /// </summary>
    public static class RunState
    {
        /// <summary>True when a run is loaded and these values mean something.</summary>
        public static bool Available =>
            MonoSingleton<ScoreController>.HasInstance && MonoSingleton<GameController>.HasInstance;

        private static ScoreController Score => MonoSingleton<ScoreController>.Instance;
        private static GameController Game => MonoSingleton<GameController>.Instance;

        // ---- money ----

        public static int Money => Available ? Score.Money : 0;

        /// <summary>
        /// Set the money total directly, bypassing triggers, the gold sound, and the lifetime
        /// gold counters. Clamped at 0 the same way the game clamps it.
        /// </summary>
        public static void SetMoney(int value)
        {
            if (!Available)
                return;

            SetAutoProperty(Score, "Money", Math.Max(0, value));

            // The HUD caches its own label; without this the number does not visibly change.
            if (SerializedMonoSingleton<MoneyPanel>.HasInstance)
                SerializedMonoSingleton<MoneyPanel>.Instance.UpdateMoneyCount();

            ComboModApi.Log?.LogInfo("Run: money set to " + Math.Max(0, value));
        }

        // ---- weeks ----

        /// <summary>Weeks left in the current milestone, as the HUD shows it.</summary>
        public static int WeeksRemaining => Available ? Game.WeeksRemaining : 0;

        /// <summary>
        /// Set weeks remaining.
        /// <para>
        /// <c>WeeksRemaining</c> is derived: <c>WeeksAllowed - WeeksSoFar</c>, where
        /// <c>WeeksAllowed</c> is the private <c>_weeksAllowed</c> plus equipped Hourglass minus
        /// equipped CrystalBall. So the target has to be back-solved through the heirloom
        /// adjustment, otherwise wearing either item silently skews the result.
        /// </para>
        /// </summary>
        public static void SetWeeksRemaining(int value)
        {
            if (!Available)
                return;

            FieldInfo field = typeof(GameController)
                .GetField("_weeksAllowed", BindingFlags.Instance | BindingFlags.NonPublic);

            if (field == null)
            {
                ComboModApi.Log?.LogError("GameController._weeksAllowed is gone; re-run the audit.");
                return;
            }

            int rawAllowed = (int)field.GetValue(Game);
            int heirloomAdjust = Game.WeeksAllowed - rawAllowed;

            // WeeksRemaining = (raw + heirloomAdjust) - WeeksSoFar  =>  solve for raw.
            Game.DebugChangeWeeks(value + Game.WeeksSoFar - heirloomAdjust);

            // The goal text is only rebuilt at milestone start and week end, so without this the
            // number is correct but the HUD keeps showing the old one until the next week ticks.
            RefreshGoalText();

            ComboModApi.Log?.LogInfo("Run: weeks remaining set to " + Game.WeeksRemaining);
        }

        // ---- score and target ----

        public static long Score_ => Available ? Score.Score : 0L;

        public static void SetScore(long value)
        {
            if (!Available)
                return;

            // ChangeScore takes a delta and drives the on-screen tally, so aim it at the target.
            Score.ChangeScore(value - Score.Score);
            ComboModApi.Log?.LogInfo("Run: score set to " + Score.Score);
        }

        public static long ScoreRequired => Available ? Game.ScoreRequired : 0L;

        /// <summary>
        /// Set the milestone target.
        /// <para>
        /// Two different numbers have to move together. <c>GameController.ScoreRequired</c> is
        /// the value the win check actually compares against, but the number printed on the HUD
        /// comes from <c>MilestoneManager.CurrentRequiredScore</c>, which reads the milestone
        /// ScriptableObject. Setting only the first gives you a real target the UI never shows —
        /// you beat the milestone at a score the goal text says is not enough yet.
        /// </para>
        /// <para>
        /// The milestone object carries three thresholds (base, Yeoman rank, Governor rank) and
        /// picks one by current rank, so all three are set rather than guessing which is live.
        /// This edits a ScriptableObject in memory only — it never reaches disk — but it does
        /// persist for the rest of the session, including later runs, until the game restarts.
        /// </para>
        /// </summary>
        public static void SetScoreRequired(int value)
        {
            if (!Available)
                return;

            Game.DebugSetTarget(value);

            if (MonoSingleton<MilestoneManager>.HasInstance)
            {
                Milestone milestone = MonoSingleton<MilestoneManager>.Instance.CurrentMilestone;
                if (milestone != null)
                {
                    SetAutoProperty(milestone, "_scoreRequiredToComplete", value);
                    SetAutoProperty(milestone, "ScoreRequiredToCompleteRankA", value);
                    SetAutoProperty(milestone, "ScoreRequiredToCompleteRankB", value);
                }

                // Endless mode derives its target from EndlessBase * 2^EndlessIndex instead of
                // the milestone, so the displayed number will not follow there.
                if (milestone != null && milestone.CitySize == CitySize.Metropolis)
                    ComboModApi.Log?.LogWarning(
                        "Endless milestone: the displayed goal is computed, so it will not track this edit.");
            }

            RefreshGoalText();

            ComboModApi.Log?.LogInfo("Run: milestone target set to " + value + " (win check and goal text)");
        }

        /// <summary>
        /// Rebuild the milestone goal block, which prints both the target and weeks remaining.
        /// The game only does this at milestone start and week end, so any mid-week edit needs
        /// to ask for it explicitly or the HUD silently lags behind the real value.
        /// </summary>
        private static void RefreshGoalText()
        {
            if (MonoSingleton<ScorePanel>.HasInstance)
                MonoSingleton<ScorePanel>.Instance.UpdateGoalText(useTypewriter: false);
        }

        // ---- inventory slots ----

        /// <summary>
        /// Heirloom slot count. Starts at 6.
        /// <para>
        /// The familiar 10-slot ceiling is not enforced here — it lives in the callers, which
        /// stop offering the SpellGainHeirloomSlot potion once <c>SlotCount &gt;= 10</c>.
        /// <c>AddSlot</c> itself will keep going, so going past 10 works but the panel layout
        /// beyond that is untested.
        /// </para>
        /// </summary>
        public static int HeirloomSlots =>
            MonoSingleton<HeirloomsPanel>.HasInstance ? MonoSingleton<HeirloomsPanel>.Instance.SlotCount : 0;

        /// <summary>Consumable slot count. Starts at 3.</summary>
        public static int ConsumableSlots =>
            MonoSingleton<ConsumablesPanel>.HasInstance ? MonoSingleton<ConsumablesPanel>.Instance.Slots.Count : 0;

        /// <summary>The game's own soft ceiling on heirloom slots, above which layout is untested.</summary>
        public const int HeirloomSlotSoftCap = 10;

        /// <summary>
        /// Add heirloom slots. Slots are add-only: the game has no RemoveSlot, so a count can
        /// rise but never fall for the rest of the run.
        /// </summary>
        public static void AddHeirloomSlots(int count)
        {
            if (!MonoSingleton<HeirloomsPanel>.HasInstance || count <= 0)
                return;

            HeirloomsPanel panel = MonoSingleton<HeirloomsPanel>.Instance;
            for (int i = 0; i < count; i++)
                panel.AddSlot();

            if (panel.SlotCount > HeirloomSlotSoftCap)
                ComboModApi.Log?.LogWarning(
                    "Heirloom slots now " + panel.SlotCount + ", past the game's own " +
                    HeirloomSlotSoftCap + "-slot ceiling. Layout past that is untested.");

            ComboModApi.Log?.LogInfo("Run: heirloom slots now " + panel.SlotCount);
        }

        /// <summary>Add consumable slots. Add-only, same as heirlooms.</summary>
        public static void AddConsumableSlots(int count)
        {
            if (!MonoSingleton<ConsumablesPanel>.HasInstance || count <= 0)
                return;

            ConsumablesPanel panel = MonoSingleton<ConsumablesPanel>.Instance;
            for (int i = 0; i < count; i++)
                panel.CreateConsumableSlot(forceRebuild: i == count - 1);

            ComboModApi.Log?.LogInfo("Run: consumable slots now " + panel.Slots.Count);
        }

        /// <summary>
        /// Raise a slot count to <paramref name="target"/>. Returns false and changes nothing if
        /// the target is at or below the current count, because slots cannot be removed.
        /// </summary>
        public static bool SetHeirloomSlots(int target)
        {
            int current = HeirloomSlots;
            if (target <= current)
            {
                ComboModApi.Log?.LogWarning(
                    "Heirloom slots are add-only; cannot go from " + current + " down to " + target + ".");
                return false;
            }

            AddHeirloomSlots(target - current);
            return true;
        }

        /// <summary>Raise the consumable slot count. Add-only, same as heirlooms.</summary>
        public static bool SetConsumableSlots(int target)
        {
            int current = ConsumableSlots;
            if (target <= current)
            {
                ComboModApi.Log?.LogWarning(
                    "Consumable slots are add-only; cannot go from " + current + " down to " + target + ".");
                return false;
            }

            AddConsumableSlots(target - current);
            return true;
        }

        // ---- consumable counters ----

        public static int Rerolls => Available ? Score.Rerolls : 0;
        public static int Removes => Available ? Score.Removes : 0;
        public static int Dismisses => Available ? Score.Dismisses : 0;
        public static int Rewinds => Available ? Score.Rewinds : 0;

        // These go through the game's own Change* methods with triggers suppressed, because the
        // counters drive UI the panels listen to. Deltas, so aim them at the target.
        public static void SetRerolls(int value)
        {
            if (Available) Score.ChangeRerolls(value - Score.Rerolls, null, withTrigger: false);
        }

        public static void SetRemoves(int value)
        {
            if (Available) Score.ChangeRemoves(value - Score.Removes, null, withTrigger: false);
        }

        public static void SetDismisses(int value)
        {
            if (Available) Score.ChangeDismisses(value - Score.Dismisses);
        }

        public static void SetRewinds(int value)
        {
            if (Available) Score.ChangeRewinds(value - Score.Rewinds);
        }

        /// <summary>
        /// Write an auto-implemented property's compiler-generated backing field. Used to move a
        /// value without triggering the side effects its public setter method would run.
        /// </summary>
        private static void SetAutoProperty(object target, string propertyName, object value)
        {
            string backing = "<" + propertyName + ">k__BackingField";
            FieldInfo field = target.GetType()
                .GetField(backing, BindingFlags.Instance | BindingFlags.NonPublic);

            if (field == null)
            {
                ComboModApi.Log?.LogError(
                    "Backing field for " + propertyName + " not found on " + target.GetType().Name +
                    "; the game was probably patched.");
                return;
            }

            field.SetValue(target, value);
        }
    }
}
