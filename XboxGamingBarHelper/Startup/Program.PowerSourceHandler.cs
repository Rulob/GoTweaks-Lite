using System;

namespace XboxGamingBarHelper
{
    /// <summary>
    /// Helper-side AC/DC transition handling. The widget is UWP and stops receiving power-source
    /// callbacks while Game Bar is dismissed (issue #72), so the helper does this work itself.
    /// Separate AC / DC profiles were removed in 1.9.7a; what's left is re-applying the current
    /// Custom TDP, which the firmware may reset when the power source changes.
    /// </summary>
    internal partial class Program
    {
        // Last observed isOnAC, so a charger flapping between Adequate and Inadequate (both AC)
        // doesn't re-push TDP each time. null on first call so the first real transition fires.
        private static bool? _lastIsOnAC;

        private static void SystemManager_PowerSourceChanged(object sender, global::Windows.System.Power.PowerSupplyStatus newStatus)
        {
            // "Inadequate" means a charger is connected but can't keep up (common with the
            // Legion Go's USB-C charger under load): treat it as AC. Only NotPresent is battery.
            bool isOnAC = newStatus != global::Windows.System.Power.PowerSupplyStatus.NotPresent;
            if (_lastIsOnAC == isOnAC)
            {
                Logger.Debug($"Helper-side AC/DC handler: status {newStatus} but isOnAC unchanged ({isOnAC}); skipping");
                return;
            }
            _lastIsOnAC = isOnAC;

            try
            {
                if (performanceManager == null) return;

                // Only in Custom mode (presets are firmware-managed) and not while AutoTDP picks
                // the wattage (it rewrites it on its own cadence).
                bool isLegionCustomMode = legionManager != null && legionManager.CurrentPerformanceMode == 255;
                if (!isLegionCustomMode || performanceManager.IsAutoTDPActive)
                {
                    Logger.Debug("Helper-side AC/DC handler: not in Custom mode or AutoTDP active; nothing to reapply");
                    return;
                }

                int tdp = performanceManager.TDP.Value;
                if (tdp > 0)
                {
                    Logger.Info($"Helper-side AC/DC handler: power source now {(isOnAC ? "AC" : "battery")}, reapplying TDP {tdp}W");
                    performanceManager.SetTDP(tdp);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Helper-side AC/DC handler: reapply TDP threw: {ex.Message}");
            }
        }
    }
}
