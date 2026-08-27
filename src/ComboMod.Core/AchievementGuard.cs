using System;
using System.Reflection;
using BepInEx.Logging;
using External;
using Library;

namespace ComboMod
{
    /// <summary>
    /// An IAchievementPlatform that reports nothing and unlocks nothing.
    /// <para>
    /// Combolands routes all achievement traffic through this interface, with
    /// SteamAchievements as the only shipped implementation, so swapping the instance is
    /// enough to keep a modded run from touching the Steam achievement set.
    /// </para>
    /// </summary>
    public sealed class NullAchievementPlatform : IAchievementPlatform
    {
        public void Init() { }

        public void Update() { }

        public bool CompleteAchievement(Achievement achievement) => false;

        public bool GetAchievementState(Achievement achievement) => false;
    }

    /// <summary>
    /// Keeps modded runs from crediting Steam achievements.
    /// <para>
    /// Note the limits of this guard. It stops achievements reaching Steam; it does not stop
    /// the run being written to Unlocks.save, which still records victories and lifetime
    /// counters. Nothing here is corrupting, because a rebalance never introduces an
    /// unresolvable tag, but a modded win does still count as a win locally.
    /// </para>
    /// </summary>
    public static class AchievementGuard
    {
        private static IAchievementPlatform _originalPlatform;
        private static bool _engaged;

        /// <summary>
        /// The handler we swapped. AchievementsHandler is a scene singleton, so leaving a menu
        /// and starting a run destroys it and builds a fresh one carrying the real Steam
        /// platform. Tracking the instance is what lets us notice and re-engage; without it the
        /// guard reports itself as on while achievements quietly go live again.
        /// </summary>
        private static AchievementsHandler _guarded;

        /// <summary>
        /// True only while the handler that exists right now is the one we neutered.
        /// </summary>
        public static bool IsEngaged
        {
            get
            {
                if (!_engaged)
                    return false;

                if (!SerializedMonoSingleton<AchievementsHandler>.HasInstance)
                    return false;

                return ReferenceEquals(SerializedMonoSingleton<AchievementsHandler>.Instance, _guarded);
            }
        }

        /// <summary>
        /// Replace the live achievement platform with a no-op.
        /// <para>
        /// AchievementsHandler sets its internal enable flag in Start(), so this must run
        /// after the handler exists. Returns false if the handler has not spawned yet, and
        /// the caller should retry.
        /// </para>
        /// </summary>
        public static bool Engage(ManualLogSource log)
        {
            if (IsEngaged)
                return true;

            try
            {
                if (!SerializedMonoSingleton<AchievementsHandler>.HasInstance)
                    return false;

                AchievementsHandler handler = SerializedMonoSingleton<AchievementsHandler>.Instance;
                if (handler == null)
                    return false;

                // Never capture our own no-op as the "original", which would happen if this ran
                // twice against the same handler and would make Release a no-op.
                if (!(handler.achievementPlatform is NullAchievementPlatform))
                    _originalPlatform = handler.achievementPlatform;

                handler.achievementPlatform = new NullAchievementPlatform();
                _guarded = handler;
                _engaged = true;

                log.LogInfo("Achievement guard engaged; Steam achievements are suppressed for this session.");
                return true;
            }
            catch (Exception ex)
            {
                log.LogError("Could not engage the achievement guard: " + ex);
                return false;
            }
        }

        /// <summary>Put the original platform back. Mainly useful for testing.</summary>
        public static void Release(ManualLogSource log)
        {
            if (!_engaged)
                return;

            try
            {
                if (SerializedMonoSingleton<AchievementsHandler>.HasInstance && _originalPlatform != null)
                    SerializedMonoSingleton<AchievementsHandler>.Instance.achievementPlatform = _originalPlatform;

                _guarded = null;
                _engaged = false;
                log.LogInfo("Achievement guard released.");
            }
            catch (Exception ex)
            {
                log.LogError("Could not release the achievement guard: " + ex);
            }
        }
    }
}
