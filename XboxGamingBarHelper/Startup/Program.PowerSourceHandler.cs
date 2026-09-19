using NLog;
using Shared.Enums;
using System;
using System.Collections.Generic;
using XboxGamingBarHelper.Power;

namespace XboxGamingBarHelper
{
    /// <summary>
    /// Helper-side AC/DC transition handling. The widget is UWP and stops receiving
    /// PowerManager.PowerSourceChanged callbacks while Game Bar is dismissed, so any
    /// AC↔DC transition that happens between Game Bar sessions used to be dropped
    /// (issue #72). The helper mirrors the three widget settings that drive its power-plan
    /// auto-switch decision, subscribes to SystemManager.PowerSourceChanged, and does the
    /// same work the widget would have done — independent of widget lifecycle.
    /// </summary>
    internal partial class Program
    {
        private static class PowerSourceProfileState
        {
            // Per-state TDP values. Populated via PowerSourceProfileValues pipe
            // message from the widget. null = no override for that field (helper falls back
            // to whatever the active profile / current property already holds).
            public static int? AcTdp = null;
            public static int? DcTdp = null;

            // Extended per-state values (build 2080+). Same null-means-no-override
            // convention. Covers the full set the widget previously sent piecemeal via
            // per-property pipes — now the helper can reapply all of them on AC/DC
            // transitions independent of widget lifecycle (FSE-only-helper scenarios).
            public static bool? AcCpuBoost = null;
            public static bool? DcCpuBoost = null;
            public static int? AcCpuEpp = null;
            public static int? DcCpuEpp = null;
            public static int? AcMaxCpuState = null;
            public static int? DcMaxCpuState = null;
            public static int? AcMinCpuState = null;
            public static int? DcMinCpuState = null;
            public static int? AcOsPowerMode = null;
            public static int? DcOsPowerMode = null;
            // FPSLimit: 0 means "disabled", non-zero is the cap. Widget's profile splits
            // these into FPSLimitEnabled+FPSLimitValue; we collapse on the wire.
            public static int? AcFpsLimit = null;
            public static int? DcFpsLimit = null;
        }

        // Tracks last observed isOnAC so we can skip the (relatively expensive) plan-switch
        // and TDP-reapply work when SystemManager fires PowerSourceChanged for a status
        // transition that doesn't actually cross the AC/DC boundary. Real-world example
        // (Diego's box, build 2068): a flaky or underspec USB-C charger produces a torrent
        // of Inadequate ↔ Adequate transitions — both map to isOnAC=true, so re-pushing
        // TDP on each is wasted work and could fight the hardware. null on first call so
        // the first real transition always fires (initial seeding is done in SystemManager).
        private static bool? _lastIsOnAC;

        /// <summary>
        /// Cache the per-state TDP / TDPBoost values for the active profile so the helper
        /// can apply them on AC/DC transitions. Widget should call this whenever the
        /// active profile or its AC/DC sub-profile changes. JSON value of `null` for any
        /// field means "no override" — helper leaves that property alone on transition.
        /// </summary>
        internal static void ApplyPowerSourceProfileValues(string configJson)
        {
            try
            {
                var cfg = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(configJson);
                if (cfg == null) return;

                int? ParseInt(string key)
                {
                    if (!cfg.TryGetValue(key, out var el)) return null;
                    if (el.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
                    if (el.ValueKind == System.Text.Json.JsonValueKind.Number && el.TryGetInt32(out int v)) return v;
                    return null;
                }
                bool? ParseBool(string key)
                {
                    if (!cfg.TryGetValue(key, out var el)) return null;
                    if (el.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                    if (el.ValueKind == System.Text.Json.JsonValueKind.False) return false;
                    return null;
                }

                PowerSourceProfileState.AcTdp = ParseInt("AcTdp");
                PowerSourceProfileState.DcTdp = ParseInt("DcTdp");

                PowerSourceProfileState.AcCpuBoost = ParseBool("AcCpuBoost");
                PowerSourceProfileState.DcCpuBoost = ParseBool("DcCpuBoost");
                PowerSourceProfileState.AcCpuEpp = ParseInt("AcCpuEpp");
                PowerSourceProfileState.DcCpuEpp = ParseInt("DcCpuEpp");
                PowerSourceProfileState.AcMaxCpuState = ParseInt("AcMaxCpuState");
                PowerSourceProfileState.DcMaxCpuState = ParseInt("DcMaxCpuState");
                PowerSourceProfileState.AcMinCpuState = ParseInt("AcMinCpuState");
                PowerSourceProfileState.DcMinCpuState = ParseInt("DcMinCpuState");
                PowerSourceProfileState.AcOsPowerMode = ParseInt("AcOsPowerMode");
                PowerSourceProfileState.DcOsPowerMode = ParseInt("DcOsPowerMode");
                PowerSourceProfileState.AcFpsLimit = ParseInt("AcFpsLimit");
                PowerSourceProfileState.DcFpsLimit = ParseInt("DcFpsLimit");

                Logger.Info($"Applied PowerSourceProfileValues "
                    + $"(AC: tdp={PowerSourceProfileState.AcTdp?.ToString() ?? "-"}W, "
                    + $"cpuBoost={PowerSourceProfileState.AcCpuBoost?.ToString() ?? "-"}, "
                    + $"epp={PowerSourceProfileState.AcCpuEpp?.ToString() ?? "-"}, "
                    + $"cpuState={PowerSourceProfileState.AcMinCpuState?.ToString() ?? "-"}–{PowerSourceProfileState.AcMaxCpuState?.ToString() ?? "-"}, "
                    + $"osMode={PowerSourceProfileState.AcOsPowerMode?.ToString() ?? "-"}, "
                    + $"fpsLimit={PowerSourceProfileState.AcFpsLimit?.ToString() ?? "-"}; "
                    + $"DC: tdp={PowerSourceProfileState.DcTdp?.ToString() ?? "-"}W, "
                    + $"cpuBoost={PowerSourceProfileState.DcCpuBoost?.ToString() ?? "-"}, "
                    + $"epp={PowerSourceProfileState.DcCpuEpp?.ToString() ?? "-"}, "
                    + $"cpuState={PowerSourceProfileState.DcMinCpuState?.ToString() ?? "-"}–{PowerSourceProfileState.DcMaxCpuState?.ToString() ?? "-"}, "
                    + $"osMode={PowerSourceProfileState.DcOsPowerMode?.ToString() ?? "-"}, "
                    + $"fpsLimit={PowerSourceProfileState.DcFpsLimit?.ToString() ?? "-"})");
            }
            catch (Exception ex)
            {
                Logger.Error($"ApplyPowerSourceProfileValues: {ex.Message}");
            }
        }

        private static void SystemManager_PowerSourceChanged(object sender, global::Windows.System.Power.PowerSupplyStatus newStatus)
        {
            // Match the widget's interpretation (GamingWidget.PowerSourceEvents.cs:70 and
            // GamingWidget.xaml.cs:2742, :2777): "Inadequate" means a charger is connected
            // but can't keep up with current load — common with the Legion Go's stock
            // USB-C charger under heavy draw. Treat that as AC, not battery, so we match
            // what the user expects when they have something physically plugged in.
            // Only NotPresent (truly unplugged) means DC.
            bool isOnAC = newStatus != global::Windows.System.Power.PowerSupplyStatus.NotPresent;

            // Short-circuit when isOnAC didn't actually change. SystemManager fires
            // PowerSourceChanged on every PowerSupplyStatus transition (Adequate ↔
            // Inadequate ↔ NotPresent), but our work below — power plan switch and
            // TDP reapply — only depends on the AC/DC boolean. On a flaky charger
            // (Inadequate ↔ Adequate flapping) we'd otherwise re-push TDP dozens of
            // times in a few minutes for no behavioral change.
            if (_lastIsOnAC == isOnAC)
            {
                Logger.Debug($"Helper-side AC/DC handler: status {newStatus} but isOnAC unchanged ({isOnAC}); skipping plan/TDP work");
                return;
            }
            _lastIsOnAC = isOnAC;

            // Apply per-state values cached by the widget on AC/DC transitions.
            //
            // Layered gating, in order of how much they should restrict the work:
            //   - performanceManager null / AutoTDP active → skip everything (no manager
            //     to call, or AutoTDP overwrites TDP every tick anyway).
            //   - TDP-specific gate (Legion Custom mode): only blocks TDP/TDPBoost reapply.
            //     In Legion preset modes the system manages TDP itself, so pushing our
            //     value would fight the preset (and preset modes have no manual TDP).
            //   - Extended fields (CPUBoost / CPUEPP / CPUState / OSPowerMode / FPSLimit)
            //     are NOT gated by Legion mode — those settings work regardless of preset
            //     mode, and the per-property equality check below ensures we no-op when
            //     the user's AC and DC profiles are identical for a given field.
            //
            // Net effect: helper-side AC/DC apply does useful work whenever the user has
            // configured ANY AC/DC differences for the active profile.
            try
            {
                if (performanceManager == null)
                {
                    return;
                }

                bool isLegionCustomMode = legionManager != null && legionManager.CurrentPerformanceMode == 255;
                bool tdpGateAllowed = isLegionCustomMode && !performanceManager.IsAutoTDPActive;

                // 2a) TDP / TDPBoost — gated by Legion Custom mode. Also skipped while AutoTDP is
                // actively managing TDP: it overwrites TDP on its own cadence, so a stale
                // profile-cached value here would just fight it.
                if (!tdpGateAllowed)
                {
                    Logger.Debug("Helper-side AC/DC handler: skipping TDP/TDPBoost reapply — not in Legion Custom mode or AutoTDP active (extended fields below still apply)");
                }
                else
                {
                    // Pick the per-state TDP value for the new power state, if the widget
                    // has configured one via PowerSourceProfileValues. Falls back to the
                    // helper's current cached TDP so behavior matches the pre-AC/DC-values
                    // build for users who haven't configured per-state values.
                    int? perStateTdp = isOnAC ? PowerSourceProfileState.AcTdp : PowerSourceProfileState.DcTdp;
                    int targetTdp = perStateTdp ?? performanceManager.TDP.Value;
                    if (targetTdp > 0)
                    {
                        string source = perStateTdp.HasValue ? $"per-state {(isOnAC ? "AC" : "DC")} profile" : "current cached value";
                        Logger.Info($"Helper-side AC/DC handler: applying TDP {targetTdp}W from {source} (legionCustom={isLegionCustomMode})");
                        // Update the helper's TDP property so the widget's slider stays
                        // in sync (the property change pipes back to the widget). Use
                        // SetValue rather than just calling SetTDP so the widget UI
                        // reflects the new value.
                        if (perStateTdp.HasValue && perStateTdp.Value != performanceManager.TDP.Value)
                        {
                            performanceManager.TDP.SetValue(targetTdp);
                        }
                        performanceManager.SetTDP(targetTdp);
                    }

                    // TDP Boost removed — boost is always on (SPPT/FPPT applied directly), so there's
                    // no per-state boost flag to reapply on AC/DC transitions anymore.
                }

                // 2b) Extended fields (build 2080+): CPUBoost / CPUEPP / CPUState (Min+Max)
                // / OSPowerMode / FPSLimit. Apply unconditionally — these settings aren't
                // managed by Legion preset modes, so they're safe to reapply anytime the
                // cached value differs from current. Skip silently when the cache has no
                // override (legacy widget that didn't pipe extended fields, or first
                // transition before widget had a chance to reconnect). null-coalescing on
                // each SetValue mirrors the TDP block above.
                string state = isOnAC ? "AC" : "DC";

                bool? perStateCpuBoost = isOnAC ? PowerSourceProfileState.AcCpuBoost : PowerSourceProfileState.DcCpuBoost;
                if (perStateCpuBoost.HasValue && powerManager?.CPUBoost != null
                    && perStateCpuBoost.Value != powerManager.CPUBoost.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying CPUBoost={perStateCpuBoost.Value} from per-state {state} profile");
                    powerManager.CPUBoost.SetValue(perStateCpuBoost.Value);
                }

                int? perStateCpuEpp = isOnAC ? PowerSourceProfileState.AcCpuEpp : PowerSourceProfileState.DcCpuEpp;
                if (perStateCpuEpp.HasValue && powerManager?.CPUEPP != null
                    && perStateCpuEpp.Value != powerManager.CPUEPP.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying CPUEPP={perStateCpuEpp.Value} from per-state {state} profile");
                    powerManager.CPUEPP.SetValue(perStateCpuEpp.Value);
                }

                int? perStateMaxCpuState = isOnAC ? PowerSourceProfileState.AcMaxCpuState : PowerSourceProfileState.DcMaxCpuState;
                if (perStateMaxCpuState.HasValue && powerManager?.MaxCPUState != null
                    && perStateMaxCpuState.Value != powerManager.MaxCPUState.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying MaxCPUState={perStateMaxCpuState.Value}% from per-state {state} profile");
                    powerManager.MaxCPUState.SetValue(perStateMaxCpuState.Value);
                }

                int? perStateMinCpuState = isOnAC ? PowerSourceProfileState.AcMinCpuState : PowerSourceProfileState.DcMinCpuState;
                if (perStateMinCpuState.HasValue && powerManager?.MinCPUState != null
                    && perStateMinCpuState.Value != powerManager.MinCPUState.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying MinCPUState={perStateMinCpuState.Value}% from per-state {state} profile");
                    powerManager.MinCPUState.SetValue(perStateMinCpuState.Value);
                }

                int? perStateOsMode = isOnAC ? PowerSourceProfileState.AcOsPowerMode : PowerSourceProfileState.DcOsPowerMode;
                if (perStateOsMode.HasValue && powerManager?.OSPowerMode != null
                    && perStateOsMode.Value != powerManager.OSPowerMode.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying OSPowerMode={perStateOsMode.Value} from per-state {state} profile");
                    powerManager.OSPowerMode.SetValue(perStateOsMode.Value);
                }

                int? perStateFpsLimit = isOnAC ? PowerSourceProfileState.AcFpsLimit : PowerSourceProfileState.DcFpsLimit;
                if (perStateFpsLimit.HasValue && rtssManager?.FPSLimit != null
                    && perStateFpsLimit.Value != rtssManager.FPSLimit.Value)
                {
                    Logger.Info($"Helper-side AC/DC handler: applying FPSLimit={perStateFpsLimit.Value} from per-state {state} profile");
                    rtssManager.FPSLimit.SetValue(perStateFpsLimit.Value);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Helper-side AC/DC handler: reapply TDP threw: {ex.Message}");
            }
        }
    }
}
