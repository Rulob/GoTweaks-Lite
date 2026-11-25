using Shared.Data;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace XboxGamingBarHelper
{
    // Since 1.9.7a the helper is the only owner of profiles. The widget keeps no copy: it shows
    // the live values the helper pushes, sends the user's changes as property sets (which the
    // handlers below record in the active profile), and reads the Profiles tab from ProfileList.
    //
    // Saved in every profile (Global and per game): TDP mode + Custom SPL / SPPT / FPPT, AutoTDP,
    // CPU Boost, EPP, min / max CPU state, FPS limit, Windows power mode, overlay on/off.
    // Controller, lighting and gyro settings stay device-wide (see ProfileSaveFlagsState).
    internal partial class Program
    {
        /// <summary>
        /// Applies a profile's performance settings to the device. Used by every profile switch
        /// (game start / exit, per-game profile on / off, startup). Must run inside
        /// isApplyingProfile = true, so the save handlers don't write the values straight back.
        /// </summary>
        /// <param name="defaultToCustom">For a per-game profile with no saved mode: switch to
        /// Custom (a new game profile starts there). The Global profile keeps the current mode.</param>
        private static void ApplyPerformanceSettings(GameProfile profile, bool defaultToCustom)
        {
            string name = profile.GameId.Name;

            // TDP mode first: the Custom limits below only apply in Custom mode.
            int? mode = profile.LegionPerformanceMode;
            if (legionManager != null)
            {
                int target = mode.HasValue && mode.Value > 0 ? mode.Value : (defaultToCustom ? 255 : legionManager.LegionPerformanceMode.Value);
                if (legionManager.LegionPerformanceMode.Value != target)
                {
                    Logger.Info($"Profile '{name}': TDP mode {target} (was {legionManager.LegionPerformanceMode.Value})");
                    legionManager.LegionPerformanceMode.SetValue(target);
                }
            }

            // AutoTDP before TDP, so IsAutoTDPActive is cleared (or re-armed on AutoTDP's next
            // tick) before the profile's own TDP is written.
            ApplyAutoTDPSettings(
                profile.AutoTDPEnabled,
                profile.AutoTDPTargetFPS,
                profile.AutoTDPMinTDP,
                profile.AutoTDPMaxTDP,
                profile.AutoTDPPauseWhenUnfocused);

            performanceManager.TDP.SetProfileValue(profile.TDP);

            // Custom SPL / SPPT / FPPT, unless AutoTDP picks the wattage itself. SetCustomTDP
            // first (it flushes a pending switch to Custom, which the per-limit setters don't
            // wait for), then the three properties so the widget's sliders follow.
            if (legionManager != null && legionManager.LegionPerformanceMode.Value == 255 && !profile.AutoTDPEnabled)
            {
                int spl = profile.TDP;
                int sppt = Math.Max(spl, profile.TDPFast);
                int fppt = Math.Max(sppt, profile.TDPPeak);
                legionManager.SetCustomTDP(spl, sppt, fppt);
                legionManager.LegionCustomTDPSlow.SetValue(spl);
                legionManager.LegionCustomTDPFast.SetValue(sppt);
                legionManager.LegionCustomTDPPeak.SetValue(fppt);
                Logger.Info($"Profile '{name}': Custom limits {spl}/{sppt}/{fppt} W");
            }

            powerManager.CPUBoost.SetValue(profile.CPUBoost);
            powerManager.CPUEPP.SetValue(profile.CPUEPP);
            powerManager.MaxCPUState.SetValue(profile.MaxCPUState);
            powerManager.MinCPUState.SetValue(profile.MinCPUState);

            if (rtssManager?.FPSLimit != null)
            {
                rtssManager.FPSLimit.SetValue(Math.Max(0, profile.FPSLimit));
            }

            // Not recorded by profiles saved before 1.9.7a (and not migrated): leave as is.
            if (int.TryParse(profile.OSPowerMode, out int osMode) && osMode >= 0 && osMode <= 2)
            {
                powerManager.OSPowerMode.SetValue(osMode);
            }
            if (profile.OverlayLevel.HasValue && onScreenDisplay != null)
            {
                onScreenDisplay.SetValue(profile.OverlayLevel.Value > 0 ? 1 : 0);
            }

            Logger.Info($"Applied profile '{name}': mode={mode?.ToString() ?? "-"}, TDP={profile.TDP}/{profile.TDPFast}/{profile.TDPPeak}, " +
                        $"AutoTDP={profile.AutoTDPEnabled}, boost={profile.CPUBoost}, EPP={profile.CPUEPP}, " +
                        $"CPU={profile.MinCPUState}-{profile.MaxCPUState}%, FPS={profile.FPSLimit}, " +
                        $"power={profile.OSPowerMode ?? "-"}, overlay={profile.OverlayLevel?.ToString() ?? "-"}");
        }

        /// <summary>Subscribes the save handlers for the per-profile settings added in 1.9.7a.</summary>
        private static void SubscribeProfileSettingSaves()
        {
            if (legionManager != null)
            {
                legionManager.LegionCustomTDPSlow.PropertyChanged += ProfileSetting_PropertyChanged;
                legionManager.LegionCustomTDPFast.PropertyChanged += ProfileSetting_PropertyChanged;
                legionManager.LegionCustomTDPPeak.PropertyChanged += ProfileSetting_PropertyChanged;
            }
            if (rtssManager?.FPSLimit != null) rtssManager.FPSLimit.PropertyChanged += ProfileSetting_PropertyChanged;
            if (powerManager?.OSPowerMode != null) powerManager.OSPowerMode.PropertyChanged += ProfileSetting_PropertyChanged;
            if (onScreenDisplay != null) onScreenDisplay.PropertyChanged += ProfileSetting_PropertyChanged;
        }

        /// <summary>
        /// Records a change to one of these settings in the active profile (per-game when one is
        /// in use, else Global), same as TDP_PropertyChanged and friends.
        /// </summary>
        private static void ProfileSetting_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (isApplyingProfile || IsInProfileSwitchCooldown()) return;

            var profile = profileManager?.CurrentProfile;
            if (profile == null) return;

            try
            {
                if (legionManager != null && (sender == legionManager.LegionCustomTDPSlow
                    || sender == legionManager.LegionCustomTDPFast || sender == legionManager.LegionCustomTDPPeak))
                {
                    // AutoTDP owns the wattage while it runs; its limits are not the profile's.
                    if (performanceManager?.IsAutoTDPActive == true) return;
                    if (sender == legionManager.LegionCustomTDPSlow) profile.TDP = legionManager.LegionCustomTDPSlow.Value;
                    else if (sender == legionManager.LegionCustomTDPFast) profile.TDPFast = legionManager.LegionCustomTDPFast.Value;
                    else profile.TDPPeak = legionManager.LegionCustomTDPPeak.Value;
                }
                else if (sender == rtssManager?.FPSLimit)
                {
                    profile.FPSLimit = Math.Max(0, rtssManager.FPSLimit.Value);
                }
                else if (sender == powerManager?.OSPowerMode)
                {
                    profile.OSPowerMode = powerManager.OSPowerMode.Value;
                }
                else if (sender == onScreenDisplay)
                {
                    profile.OverlayLevel = onScreenDisplay.Value > 0 ? 1 : 0;
                }
                else
                {
                    return;
                }
                KeepGlobalProfileInSync();
            }
            catch (Exception ex)
            {
                Logger.Warn($"ProfileSetting_PropertyChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// CurrentProfile holds its own copy of the profile; when it is the Global profile, keep
        /// the GlobalProfile field on the same values so RestoreGlobalProfileSettings reads them.
        /// </summary>
        private static void KeepGlobalProfileInSync()
        {
            if (profileManager.CurrentProfile.IsGlobalProfile)
            {
                profileManager.GlobalProfile = profileManager.CurrentProfile.Value;
            }
        }

        // ---- Profiles tab --------------------------------------------------------------------

        /// <summary>
        /// The Profiles tab's data: the active profile, whether a per-game profile is in use, and
        /// every profile's saved settings. Sent as JSON for the "ProfileList" request.
        /// </summary>
        private static string BuildProfileListJson()
        {
            profileManager.RefreshGlobalProfile();
            var current = profileManager.CurrentProfile.Value;

            // Snapshot under the cache lock: a profile save on another thread updates the cache.
            List<GameProfile> cached;
            lock (GameProfile.ProfileLock)
            {
                cached = profileManager.GameProfiles.Values.ToList();
            }

            var list = new List<Dictionary<string, object>>();
            list.Add(DescribeProfile(current.IsGlobalProfile ? current : profileManager.GlobalProfile, true));
            foreach (var cachedProfile in cached)
            {
                var p = cachedProfile;
                if (p.IsGlobalProfile) continue;
                // The live copy is newer than the cache's when it is the active profile.
                if (!current.IsGlobalProfile && current.GameId.Equals(p.GameId)) p = current;
                list.Add(DescribeProfile(p, false));
            }

            var result = new Dictionary<string, object>
            {
                { "current", current.IsGlobalProfile ? "" : current.GameId.Name ?? "" },
                { "perGame", profileManager.PerGameProfile.Value },
                { "runningGame", systemManager?.RunningGame?.Value.IsValid() == true ? systemManager.RunningGame.Value.GameId.Name ?? "" : "" },
                { "profiles", list },
            };
            return System.Text.Json.JsonSerializer.Serialize(result);
        }

        private static Dictionary<string, object> DescribeProfile(GameProfile p, bool isGlobal)
        {
            return new Dictionary<string, object>
            {
                { "name", isGlobal ? "Global" : p.GameId.Name ?? "" },
                { "path", isGlobal ? "" : p.GameId.Path ?? "" },
                { "global", isGlobal },
                { "use", isGlobal || p.Use },
                { "mode", p.LegionPerformanceMode ?? 0 },
                { "tdp", p.TDP },
                { "tdpFast", p.TDPFast },
                { "tdpPeak", p.TDPPeak },
                { "autoTdp", p.AutoTDPEnabled },
                { "autoTdpTarget", p.AutoTDPTargetFPS },
                { "autoTdpMin", p.AutoTDPMinTDP },
                { "autoTdpMax", p.AutoTDPMaxTDP },
                { "boost", p.CPUBoost },
                { "epp", p.CPUEPP },
                { "minState", p.MinCPUState },
                { "maxState", p.MaxCPUState },
                { "fps", p.FPSLimit },
                { "osPower", int.TryParse(p.OSPowerMode, out int os) ? os : -1 },
                { "overlay", p.OverlayLevel ?? -1 },
            };
        }

        // ---- One-time import of the widget's old profile copies -------------------------------

        private class WidgetProfileValues
        {
            public string name { get; set; }
            public int tdp { get; set; }
            public int tdpFast { get; set; }
            public int tdpPeak { get; set; }
            public int fps { get; set; }
            public int osPower { get; set; }
            public int overlay { get; set; }
        }

        /// <summary>
        /// Before 1.9.7a the widget kept its own copy of every profile, and some settings only
        /// lived there (FPS limit, Windows power mode, overlay, Custom SPPT / FPPT). The widget
        /// sends those once ("MigrateWidgetProfiles", a JSON array; name "Global" or the game
        /// name) and the helper writes them into its profiles. Unknown games are skipped.
        /// </summary>
        private static void MigrateWidgetProfiles(string json)
        {
            List<WidgetProfileValues> entries;
            try
            {
                entries = System.Text.Json.JsonSerializer.Deserialize<List<WidgetProfileValues>>(json);
            }
            catch (Exception ex)
            {
                Logger.Warn($"MigrateWidgetProfiles: bad payload: {ex.Message}");
                return;
            }
            if (entries == null) return;

            lock (profileApplicationLock)
            {
                profileManager.RefreshGlobalProfile();
                int migrated = 0;
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry?.name)) continue;

                    bool isGlobal = string.Equals(entry.name, "Global", StringComparison.OrdinalIgnoreCase);
                    GameProfile target;
                    if (isGlobal)
                    {
                        target = profileManager.GlobalProfile;
                    }
                    else
                    {
                        List<GameProfile> cached;
                        lock (GameProfile.ProfileLock) { cached = profileManager.GameProfiles.Values.ToList(); }
                        var match = cached.FirstOrDefault(p => !p.IsGlobalProfile
                            && string.Equals((p.GameId.Name ?? "").Trim(), entry.name.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (match.GameId.Name == null)
                        {
                            Logger.Info($"MigrateWidgetProfiles: no helper profile for '{entry.name}', skipped");
                            continue;
                        }
                        target = match;
                    }

                    // The live copy is the newest one when this is the active profile.
                    bool isCurrent = profileManager.CurrentProfile.Value.GameId.Equals(target.GameId);
                    if (isCurrent) target = profileManager.CurrentProfile.Value;

                    if (entry.tdp > 0) target.TDP = entry.tdp;
                    if (entry.tdpFast > 0) target.TDPFast = entry.tdpFast;
                    if (entry.tdpPeak > 0) target.TDPPeak = entry.tdpPeak;
                    target.FPSLimit = Math.Max(0, entry.fps);
                    if (entry.osPower >= 0 && entry.osPower <= 2) target.OSPowerMode = entry.osPower.ToString();
                    target.OverlayLevel = entry.overlay > 0 ? 1 : 0;
                    // Each setter above saved the profile (and updated the shared cache).

                    if (isCurrent) profileManager.CurrentProfile.SetValueSilent(target);
                    if (isGlobal || target.IsGlobalProfile) profileManager.GlobalProfile = target;
                    migrated++;

                    // The helper applied this profile at startup, before these values existed
                    // (e.g. no FPS limit): apply it again now that it is complete.
                    if (isCurrent)
                    {
                        isApplyingProfile = true;
                        try { ApplyPerformanceSettings(target, defaultToCustom: !target.IsGlobalProfile); }
                        finally { isApplyingProfile = false; }
                    }
                }
                Logger.Info($"MigrateWidgetProfiles: migrated {migrated} of {entries.Count} profile(s)");
            }
        }
    }
}
