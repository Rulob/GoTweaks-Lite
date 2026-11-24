using System;

namespace XboxGamingBarHelper
{
    internal partial class Program
    {
        // How long no game must be running before AutoTDP hands the device back to its idle mode,
        // so a launcher handoff or a game restarting doesn't flip the mode back and forth.
        private const double AutoTDPIdleDelaySeconds = 5;
        private static DateTime autoTDPNoGameSinceUtc = DateTime.MinValue;

        /// <summary>
        /// Called by the AutoTDP loop (every tick, while AutoTDP is enabled). On the Global profile,
        /// with an idle mode set (the Quiet / Balanced / Performance mode the widget switched away
        /// from when AutoTDP was turned on):
        /// - no game for <see cref="AutoTDPIdleDelaySeconds"/> and the device in Custom: switch to
        ///   the idle mode, so leaving a game doesn't leave the device in AutoTDP's last wattage;
        /// - a game running and the device in a firmware mode: switch to Custom so AutoTDP can
        ///   manage it.
        /// Per-game profiles are left alone: leaving a game restores the Global profile anyway.
        /// Runs as a profile application so the mode-change handler (and its post-switch
        /// cooldown) stays out of it; the mode is written to the Global profile here instead.
        /// </summary>
        private static void OnAutoTDPGameRunningState(bool gameRunning)
        {
            if (legionManager == null || !(legionManager.LegionGoDetected?.Value ?? false)) return;

            int idleMode = autoTDPManager?.IdleMode?.Value ?? 0;
            if (idleMode < 1 || idleMode > 3)
            {
                autoTDPNoGameSinceUtc = DateTime.MinValue;
                return;
            }

            if (gameRunning)
            {
                autoTDPNoGameSinceUtc = DateTime.MinValue;
            }
            else if (autoTDPNoGameSinceUtc == DateTime.MinValue)
            {
                autoTDPNoGameSinceUtc = DateTime.UtcNow;
                return;
            }

            int mode = legionManager.CurrentPerformanceMode;
            bool wantCustom = gameRunning && mode >= 1 && mode <= 3;
            bool wantIdle = !gameRunning && mode == 255
                && (DateTime.UtcNow - autoTDPNoGameSinceUtc).TotalSeconds >= AutoTDPIdleDelaySeconds;
            if (!wantCustom && !wantIdle) return;

            // Never wait here: this runs on the AutoTDP loop, and a profile switch in progress
            // may itself be waiting on AutoTDP. Try again on the next tick instead.
            if (!System.Threading.Monitor.TryEnter(profileApplicationLock)) return;
            try
            {
                if (isApplyingProfile || profileManager == null || profileManager.PerGameProfile.Value) return;

                isApplyingProfile = true;
                try
                {
                    int newMode = wantCustom ? 255 : idleMode;
                    if (wantCustom)
                    {
                        Logger.Info($"AutoTDP: game started on the Global profile - switching to Custom (was mode {mode})");
                    }
                    else
                    {
                        Logger.Info($"AutoTDP: no game for {AutoTDPIdleDelaySeconds:F0}s on the Global profile - returning to mode {idleMode}");
                    }
                    legionManager.LegionPerformanceMode.SetValue(newMode);

                    // Keep the helper's Global profile on the mode the device is really in, as
                    // LegionControllerSetting_PropertyChanged would (it is skipped here). If
                    // AutoTDP is then turned off while idle, the Global profile comes back in the
                    // idle mode rather than in AutoTDP's Custom mode.
                    profileManager.CurrentProfile.LegionPerformanceMode = newMode;
                    if (profileManager.CurrentProfile.IsGlobalProfile)
                    {
                        profileManager.GlobalProfile.LegionPerformanceMode = newMode;
                    }
                }
                finally
                {
                    isApplyingProfile = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"AutoTDP idle-mode switch failed: {ex.Message}");
            }
            finally
            {
                System.Threading.Monitor.Exit(profileApplicationLock);
            }
        }
    }
}
