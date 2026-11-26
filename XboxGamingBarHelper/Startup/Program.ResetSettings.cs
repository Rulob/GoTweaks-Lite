using NLog;
using Shared.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Settings;

namespace XboxGamingBarHelper
{
    /// <summary>
    /// "Reset all settings" (1.9.8, Settings > Debug). Puts GoTweaks' own settings back to a fresh
    /// install; Windows settings (power button, timeouts, touchscreen, Game Bar slot...) are left
    /// as they are.
    ///  1. Backs everything up to a Desktop folder (the same format as Export All Data, so Import
    ///     All Data brings it back). If the backup fails, nothing is reset.
    ///  2. Writes the default controller settings to the hardware (remaps, deadzones, triggers,
    ///     vibration, lighting, touchpad), because the controller keeps them across restarts.
    ///  3. Restarts the helper. The restart script replaces the settings files only after this
    ///     process has exited, so nothing still in memory can write them back; the new helper
    ///     then starts with a default Global profile and applies it.
    /// The widget clears its own storage and closes itself once this returns.
    /// </summary>
    internal partial class Program
    {
        private static global::Windows.Foundation.Collections.ValueSet HandleResetAllSettings(Shared.IPC.PipeMessage request)
        {
            int functionValue = (int)request.Function;
            bool keepGameProfiles = request.Extra != null
                && request.Extra.TryGetValue("KeepGameProfiles", out var keepObj)
                && keepObj is bool keep && keep;
            Logger.Info($"Pipe: ResetAllSettings request received (keep game profiles: {keepGameProfiles})");

            var response = new global::Windows.Foundation.Collections.ValueSet();
            response.Add(nameof(Function), functionValue);

            if (_restartInProgress || _isShuttingDown)
            {
                response.Add("Content", "Error: the helper is already restarting");
                return response;
            }

            // 1. Backup first. No backup, no reset.
            string backupPath;
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                backupPath = Path.Combine(desktopPath, $"GoTweaks_Backup_BeforeReset_{DateTime.Now:yyyy-MM-dd_HHmmss}");
                ExportDataToFolder(backupPath, request.Content);
            }
            catch (Exception ex)
            {
                Logger.Error($"ResetAllSettings: backup failed, nothing was reset: {ex.Message}");
                response.Add("Content", $"Error: the backup failed, so nothing was reset ({ex.Message})");
                return response;
            }

            // 2-3 run after the reply (the widget waits 10 s at most and the controller writes can
            // take a few seconds). The widget closes itself meanwhile.
            List<string> filesToDelete = GetSettingsFilesForReset(keepGameProfiles, out string profilesFolder);
            string keptSettingsJson = BuildKeptSettingsJson();
            string cleanupScript = BuildResetCleanupScript(filesToDelete, profilesFolder,
                LocalSettingsHelper.SettingsFilePath, keptSettingsJson);
            Logger.Info($"ResetAllSettings: backup at {backupPath}; will delete after exit: {string.Join(", ", filesToDelete)}"
                + (profilesFolder != null ? $", {profilesFolder}\\*.xml" : "")
                + $"; settings kept: {keptSettingsJson}");

            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // let the reply go out first
                try
                {
                    ResetLegionControllerToDefaults();
                }
                catch (Exception ex)
                {
                    // Keep going: the files still get reset and the new helper applies defaults.
                    Logger.Error(ex, "ResetAllSettings: writing the default controller settings failed");
                }
                await RestartHelperForResetAsync(cleanupScript);
            });

            response.Add("Content", backupPath);
            response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            return response;
        }

        // Helper settings that only record the state of Windows, not a GoTweaks preference. The
        // reset doesn't touch Windows, so these must survive or GoTweaks would lose track of it:
        // the Game Bar slot's original owner (put back by Prepare for Uninstall) and whether the
        // touchscreen is turned off.
        private static readonly string[] SettingsKeptByReset = { "GameBarFirstSlotBackup", "TouchscreenEnabled" };

        private static string BuildKeptSettingsJson()
        {
            var kept = new Dictionary<string, object>();
            foreach (var key in SettingsKeptByReset)
            {
                if (LocalSettingsHelper.TryGetValue<object>(key, out var value) && value != null)
                {
                    kept[key] = value is System.Text.Json.JsonElement element ? element.Clone() : value;
                }
            }
            return System.Text.Json.JsonSerializer.Serialize(kept);
        }

        /// <summary>
        /// The files that hold GoTweaks settings: the Global profile, the helper settings (and their
        /// old LocalCache copy, which would otherwise be copied back), the AutoTDP learning data,
        /// and the game profiles unless they are kept. system_restore_data.json is kept: it holds
        /// the original Windows values that Prepare for Uninstall still needs.
        /// </summary>
        private static List<string> GetSettingsFilesForReset(bool keepGameProfiles, out string profilesFolder)
        {
            // The live settings file isn't listed: the script rewrites it with the kept settings.
            var files = new List<string>
            {
                Profile.ProfileManager.GetGlobalProfilePath(),
                LocalSettingsHelper.LegacySettingsFilePath,
            };

            var localStatePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", Shared.Constants.PackageConstants.PackageFamilyName, "LocalState");
            files.Add(Path.Combine(localStatePath, "autotdp_qtable.json"));

            profilesFolder = keepGameProfiles ? null : Profile.ProfileManager.GetGameProfilesFolder();
            return files.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string BuildResetCleanupScript(List<string> files, string profilesFolder, string settingsPath, string keptSettingsJson)
        {
            string Quote(string s) => "'" + s.Replace("'", "''") + "'";
            var sb = new StringBuilder();
            foreach (var file in files)
            {
                sb.AppendLine($"Remove-Item -LiteralPath {Quote(file)} -Force -ErrorAction SilentlyContinue");
            }
            if (!string.IsNullOrEmpty(settingsPath))
            {
                // Rewrite the helper settings with only the kept keys (UTF-8 without BOM).
                sb.AppendLine($"try {{ [System.IO.File]::WriteAllText({Quote(settingsPath)}, {Quote(keptSettingsJson ?? "{}")}) }} catch {{ Remove-Item -LiteralPath {Quote(settingsPath)} -Force -ErrorAction SilentlyContinue }}");
            }
            if (!string.IsNullOrEmpty(profilesFolder))
            {
                sb.AppendLine($"if (Test-Path -LiteralPath {Quote(profilesFolder)}) {{ Get-ChildItem -LiteralPath {Quote(profilesFolder)} -Filter '*.xml' | Remove-Item -Force -ErrorAction SilentlyContinue }}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Same as the tray's Restart, plus the cleanup script, which runs between this process
        /// exiting and the new helper starting.
        /// </summary>
        private static async Task RestartHelperForResetAsync(string cleanupScript)
        {
            Logger.Info("ResetAllSettings: restarting the helper");
            NotifyWidgetHelperExiting("ResetAllSettings");
            _restartInProgress = true;

            if (!TryRestartHelperProcess(cleanupScript))
            {
                // Without the script the files can't be deleted after exit; the scheduled task
                // brings the helper back with the settings unchanged rather than not at all.
                Logger.Error("ResetAllSettings: the restart script did not start; settings were not deleted");
                try { Services.ScheduledTaskService.RunTaskNow(); }
                catch (Exception ex) { Logger.Error(ex, "ResetAllSettings: scheduled task fallback failed"); }
            }

            _isShuttingDown = true;
            LogManager.Flush();
            await Task.Delay(3000);
            Logger.Info("ResetAllSettings: forcing helper process exit");
            LogManager.Flush();
            Environment.Exit(0);
        }

        /// <summary>
        /// Writes GoTweaks' default controller settings to the hardware. These are the values the
        /// helper itself starts with, so after the restart the widget shows what the controller
        /// actually has. Gyro needs nothing here: the startup apply always writes its defaults.
        /// </summary>
        private static void ResetLegionControllerToDefaults()
        {
            if (legionManager == null || legionManager.LegionGoDetected?.Value != true) return;
            Logger.Info("ResetAllSettings: writing default controller settings");

            const string DisabledMapping = "{\"Type\":0,\"GamepadAction\":0}";
            void Set<T>(HelperProperty<T, LegionManager> property, T value)
            {
                if (property == null) return;
                try { property.SetValue(value, DateTime.Now.Ticks); }
                catch (Exception ex) { Logger.Warn($"ResetAllSettings: {property.GetType().Name} -> {value} failed: {ex.Message}"); }
            }

            // Extra buttons (back paddles, M buttons, Desktop, Page): clear only the remapped ones.
            var buttons = new HelperProperty<string, LegionManager>[]
            {
                legionManager.LegionButtonY1, legionManager.LegionButtonY2, legionManager.LegionButtonY3,
                legionManager.LegionButtonM1, legionManager.LegionButtonM2, legionManager.LegionButtonM3,
                legionManager.LegionButtonDesktop, legionManager.LegionButtonPage,
            };
            foreach (var button in buttons)
            {
                if (button != null && !ButtonMappingParser.IsDefaultMapping(button.Value))
                {
                    Set(button, DisabledMapping);
                }
            }

            // Regular buttons (A, B, sticks...): put each remapped one back on itself.
            string gamepadReset = BuildGamepadMappingReset(legionManager.LegionGamepadMapping?.Value);
            if (gamepadReset != null)
            {
                Set(legionManager.LegionGamepadMapping, gamepadReset);
            }

            Set(legionManager.LegionHairTriggers, false);
            Set(legionManager.LegionLeftStickDeadzone, 4);
            Set(legionManager.LegionRightStickDeadzone, 4);
            Set(legionManager.LegionLeftTriggerStart, 0);
            Set(legionManager.LegionLeftTriggerEnd, 0);
            Set(legionManager.LegionRightTriggerStart, 0);
            Set(legionManager.LegionRightTriggerEnd, 0);
            Set(legionManager.LegionVibration, 2);      // Medium
            Set(legionManager.LegionVibrationMode, 1);  // FPS
            Set(legionManager.LegionTouchpadEnabled, true);

            // Lighting: colour, brightness and speed before the mode, as the profile apply does.
            Set(legionManager.LegionLightColor, "#FFFFFF");
            Set(legionManager.LegionLightBrightness, 100);
            Set(legionManager.LegionLightSpeed, 50);
            Set(legionManager.LegionLightMode, 1);      // Solid
            Set(legionManager.LegionPowerLight, true);

            // Touchpad vibration is re-applied from its default (Medium) when the helper starts.
        }

        /// <summary>
        /// Turns a gamepad mapping JSON ({"A":{...},"B":{...}}) into one that puts every remapped
        /// button back to default, or null when nothing is remapped.
        /// </summary>
        internal static string BuildGamepadMappingReset(string mappingJson)
        {
            if (string.IsNullOrWhiteSpace(mappingJson)) return null;
            var names = new List<string>();
            foreach (Match match in Regex.Matches(mappingJson, "\"(\\w+)\"\\s*:\\s*(\\{[^}]+\\})"))
            {
                if (!ButtonMappingParser.IsDefaultMapping(match.Groups[2].Value))
                {
                    names.Add(match.Groups[1].Value);
                }
            }
            if (names.Count == 0) return null;
            return "{" + string.Join(",", names.Select(n => $"\"{n}\":{{\"Type\":0,\"GamepadAction\":0}}")) + "}";
        }
    }
}
