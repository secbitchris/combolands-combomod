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

        /// <summary>True while achievements are being suppressed.</summary>
        public static bool IsEngaged => _engaged;

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
            if (_engaged)
                return true;

            try
            {
                if (!SerializedMonoSingleton<AchievementsHandler>.HasInstance)
                    return false;

                AchievementsHandler handler = SerializedMonoSingleton<AchievementsHandler>.Instance;
                if (handler == null)
                    return false;

                _originalPlatform = handler.achievementPlatform;
                handler.achievementPlatform = new NullAchievementPlatform();
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
                if (SerializedMonoSingleton<AchievementsHandler>.HasInstance)
                    SerializedMonoSingleton<AchievementsHandler>.Instance.achievementPlatform = _originalPlatform;

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
