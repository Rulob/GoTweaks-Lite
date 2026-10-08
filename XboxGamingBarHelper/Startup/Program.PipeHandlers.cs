using NLog;
using Shared.Constants;
using Shared.Data;
using Shared.IPC;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using XboxGamingBarHelper.AMD;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.ControllerEmulation;
using XboxGamingBarHelper.Devices.Libraries.GPD;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.LosslessScaling;
using XboxGamingBarHelper.OnScreenDisplay;
using XboxGamingBarHelper.Performance;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Profile;
using XboxGamingBarHelper.RTSS;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Systems;
using XboxGamingBarHelper.Labs;
using Shared.Enums;

namespace XboxGamingBarHelper
{
    internal partial class Program
    {

        // NOTE: Connection_RequestReceived (AppService handler) was removed - using Named Pipes only
        // See PipeServer_MessageReceived below for the pipe-based message handler

        // The following code was Connection_RequestReceived content - it has been removed
        // since we now use PipeServer_MessageReceived for all widget communication


        /// <summary>
        /// Handles messages received from the widget via Named Pipe
        /// </summary>
        private static async void PipeServer_MessageReceived(object sender, IPC.PipeMessageEventArgs e)
        {
            try
            {
                var pipeMsg = Shared.IPC.PipeMessage.FromJson(e.Message);
                Logger.Info($"Helper received pipe message: {pipeMsg}");

                // One-off "action" messages keyed by an Extra entry. Dispatched in
                // priority order — the first matching condition wins (the same order
                // the inline if-chain used to run in). Handlers live below this method.

                // Handle keyboard shortcut request
                if (pipeMsg.Extra.TryGetValue("SendKeyboardShortcut", out object shortcutValue) && shortcutValue is string)
                {
                    HandleSendKeyboardShortcut(pipeMsg);
                    return;
                }

                // Handle close game request
                if (pipeMsg.Extra.ContainsKey("CloseGame"))
                {
                    HandleCloseGame(pipeMsg);
                    return;
                }

                // Handle touch keyboard toggle request
                if (pipeMsg.Extra.ContainsKey("ToggleTouchKeyboard"))
                {
                    HandleToggleTouchKeyboard(pipeMsg);
                    return;
                }

                // Handle LaunchUrl request (for Donate button, etc.)
                if (pipeMsg.Extra.TryGetValue("LaunchUrl", out object urlValue) && urlValue is string)
                {
                    HandleLaunchUrl(pipeMsg);
                    return;
                }

                // Handle hibernate request
                if (pipeMsg.Extra.ContainsKey("Hibernate"))
                {
                    HandleHibernate(pipeMsg);
                    return;
                }

                // Handle "Disable Sleep Timer (AC+DC)" request from the Power & Sleep card
                if (pipeMsg.Extra.ContainsKey("DisableWindowsSleepTimers"))
                {
                    HandleDisableWindowsSleepTimers(pipeMsg);
                    return;
                }

                // Handle export logs request
                if (pipeMsg.Extra.ContainsKey("ExportLogs"))
                {
                    HandleExportLogs(pipeMsg);
                    return;
                }

                // Minimize the desktop app window instead of letting it close.
                // Closing the last visible window suspends the UWP process and
                // kills the active Game Bar widget; the UWP view can't minimize
                // itself, so it asks us. Restored from the tray icon's
                // "Open GoTweaks" or by relaunching the app.
                if (pipeMsg.Extra.ContainsKey("HideAppWindow"))
                {
                    bool hidden = false;
                    try
                    {
                        hidden = XboxGamingBarHelper.Windows.User32.HideAppFrameWindow("GoTweaks Lite");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Pipe: HideAppWindow threw: {ex.Message}");
                    }
                    Logger.Info($"Pipe: HideAppWindow request - hidden={hidden}");
                    return;
                }

                // Re-show a previously minimized desktop app window (sent by the
                // widget's OnLaunched so a Start-menu relaunch un-hides it).
                if (pipeMsg.Extra.ContainsKey("ShowAppWindow"))
                {
                    bool shown = false;
                    try
                    {
                        shown = XboxGamingBarHelper.Windows.User32.ShowAppFrameWindow("GoTweaks Lite");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Pipe: ShowAppWindow threw: {ex.Message}");
                    }
                    Logger.Info($"Pipe: ShowAppWindow request - shown={shown}");
                    return;
                }

                // Handle export profiles request
                if (pipeMsg.Extra.ContainsKey("ExportProfiles"))
                {
                    HandleExportProfiles(pipeMsg);
                    return;
                }

                // Handle import profiles request
                if (pipeMsg.Extra.ContainsKey("ImportProfiles"))
                {
                    HandleImportProfiles(pipeMsg);
                    return;
                }

                // Handle Auto SDR curve export/import (Go2HDR-compatible JSON)
                if (pipeMsg.Extra.ContainsKey("ExportAutoSdrCurve"))
                {
                    HandleExportAutoSdrCurve(pipeMsg);
                    return;
                }
                if (pipeMsg.Extra.ContainsKey("ImportAutoSdrCurve"))
                {
                    HandleImportAutoSdrCurve(pipeMsg);
                    return;
                }

                // Handle Lenovo driver-update check. Widget button fires this, helper
                // resolves the machine type + BIOS version via WMI and best-effort-fetches
                // the live driver list from Lenovo's public pcsupport API. Response is
                // serialized JSON so the widget can render it without knowing the helper's
                // internal data model.
                if (pipeMsg.Extra.ContainsKey("CheckDriverUpdates"))
                {
                    await HandleCheckDriverUpdates(pipeMsg);
                    return;
                }

                // Persists the user's "Check for driver updates on start"
                // preference on the helper side. Widget writes here whenever
                // the checkbox flips; helper re-reads at next launch before
                // scheduling the Lenovo probe, so flipping the box off keeps
                // the helper from hitting pcsupport.lenovo.com on boot.
                if (pipeMsg.Extra.ContainsKey("SetDriverCheckOnStart"))
                {
                    HandleSetDriverCheckOnStart(pipeMsg);
                    return;
                }

                // Persists the user's "Check for updates on start" preference
                // for the GoTweaks self-update probe. Mirrors SetDriverCheckOnStart.
                if (pipeMsg.Extra.ContainsKey("SetGoTweaksCheckOnStart"))
                {
                    HandleSetGoTweaksCheckOnStart(pipeMsg);
                    return;
                }

                // GoTweaks self-update check. Widget explicitly asks; helper
                // serves the cached startup-probe result unless ForceRefresh=true.
                if (pipeMsg.Extra.ContainsKey("CheckGoTweaksUpdate"))
                {
                    await HandleCheckGoTweaksUpdate(pipeMsg);
                    return;
                }

                if (pipeMsg.Extra.ContainsKey("InstallGoTweaksUpdate"))
                {
                    await HandleInstallGoTweaksUpdate(pipeMsg);
                    return;
                }

                // Handle "update all" batch install. Widget sends an array of
                // Lenovo download URLs; helper downloads them in parallel (bounded)
                // and launches each installer sequentially so they don't fight
                // over the Windows Installer mutex.
                if (pipeMsg.Extra.ContainsKey("BatchInstallDrivers"))
                {
                    await HandleBatchInstallDrivers(pipeMsg);
                    return;
                }

                // Handle driver-install request. Widget sends a Lenovo download URL;
                // helper downloads the file to a per-session temp folder and launches
                // it. The helper runs elevated so Lenovo EXE installers that require
                // admin work without a second UAC prompt. Response signals start
                // success/failure only — we don't wait for the installer to finish.
                if (pipeMsg.Extra.ContainsKey("InstallDriverUpdate"))
                {
                    await HandleInstallDriverUpdate(pipeMsg);
                    return;
                }

                // Handle "reapply TDP to hardware" request. After a power source change or
                // Custom-mode re-entry, Windows/Legion firmware may silently reset TDP limits
                // even though our cached value hasn't changed. Widget asks the helper to
                // re-push the current TDP to hardware without going through the property
                // system's equality check. Must NOT modify the profile's saved TDP (the old
                // N-1/N trick did and corrupted global.xml by 1 W).
                if (pipeMsg.Extra.ContainsKey("ReapplyTDP"))
                {
                    HandleReapplyTDP(pipeMsg);
                    return;
                }

                // Widget pushes its saved copy of the active per-game profile (CPU boost / EPP /
                // CPU state) so the helper applies it AND records it in its own copy of the profile.
                if (pipeMsg.Extra.ContainsKey("SyncGameProfile"))
                {
                    HandleSyncGameProfile(pipeMsg);
                    return;
                }

                // Widget fires this on each panel open so the brightness slider always shows the
                // real current value (in case brightness was changed from another source) and grays
                // out when the built-in panel is off (external monitor). Helper re-reads WMI live.
                if (pipeMsg.Extra.ContainsKey("RefreshPanelBrightness"))
                {
                    HandleRefreshPanelBrightness(pipeMsg);
                    return;
                }

                // Handle gyro-calibration request. Widget fires this as a one-shot while
                // the user holds the Legion controllers still; helper sends the HID
                // output report to the controllers' firmware to capture a fresh bias.
                // Strictly firmware-only; JSL calibration lives on the separate
                // ControllerEmulation Calibrate Gyro button (different concern, kept
                // separate per user request).
                if (pipeMsg.Extra.ContainsKey("CalibrateLegionGyro"))
                {
                    HandleCalibrateLegionGyro(pipeMsg);
                    return;
                }

                // Handle exit helper request (for version mismatch restart - legacy)
                if (pipeMsg.Extra.ContainsKey("ExitHelper"))
                {
                    HandleExitHelper(pipeMsg);
                    return;
                }

                // Handle upgrade helper request (UAC-free upgrade - preferred)
                // The widget sends the MSIX source path so we can copy files after exit
                if (pipeMsg.Extra.ContainsKey("UpgradeHelper"))
                {
                    HandleUpgradeHelper(pipeMsg);
                    return;
                }

                // Handle batch get request (for fast property sync)
                if (pipeMsg.Command == Shared.Enums.Command.BatchGet)
                {
                    await HandleBatchGetRequestViaPipe(pipeMsg);
                    return;
                }

                // Handle property requests via the properties system
                if (pipeMsg.Function != Shared.Enums.Function.None)
                {
                    HandlePipePropertyRequest(pipeMsg);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling pipe message: {ex.Message}");
                Logger.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        // ====================================================================
        // One-off "action" handlers, keyed by an Extra entry on the pipe message.
        // Dispatched in priority order from PipeServer_MessageReceived above.
        // Each was previously an inline if-block in that method; extracted here
        // so the dispatcher reads as a flat table of named actions. Behaviour is
        // unchanged — the dispatcher applies the exact same match condition that
        // used to guard each block.
        // ====================================================================

        // SendKeyboardShortcut: inject a keyboard shortcut via InputInjector.
        private static void HandleSendKeyboardShortcut(Shared.IPC.PipeMessage pipeMsg)
        {
            if (pipeMsg.Extra.TryGetValue("SendKeyboardShortcut", out object shortcutValue) && shortcutValue is string shortcutStr)
            {
                Logger.Info($"Sending keyboard shortcut via InputInjector: {shortcutStr}");
                SendKeyboardShortcutViaInputInjector(shortcutStr);
                SendPipeAck(pipeMsg.RequestId);
            }
        }

        // CloseGame: close the foreground window.
        private static void HandleCloseGame(Shared.IPC.PipeMessage pipeMsg)
        {
            Logger.Info("CloseGame request received - attempting to close foreground window");
            bool success = Windows.User32.CloseForegroundWindow();
            Logger.Info($"CloseGame result: {success}");
            SendPipeAck(pipeMsg.RequestId, success);
        }

        // ToggleTouchKeyboard: show/hide the on-screen touch keyboard.
        private static void HandleToggleTouchKeyboard(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                Logger.Info("Pipe: ToggleTouchKeyboard request received");
                TouchKeyboardHelper.Toggle();
                Logger.Info("Pipe: Touch keyboard toggled");
                SendPipeAck(pipeMsg.RequestId);
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: Failed to toggle touch keyboard: {ex.Message}");
                SendPipeAck(pipeMsg.RequestId, false);
            }
        }

        // LaunchUrl: open a URL in the default browser (Donate button, etc.).
        private static void HandleLaunchUrl(Shared.IPC.PipeMessage pipeMsg)
        {
            if (pipeMsg.Extra.TryGetValue("LaunchUrl", out object urlValue) && urlValue is string url)
            {
                try
                {
                    Logger.Info($"Pipe: LaunchUrl request received: {url}");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                    Logger.Info("Pipe: URL launched successfully");
                    SendPipeAck(pipeMsg.RequestId);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Pipe: Failed to launch URL: {ex.Message}");
                    SendPipeAck(pipeMsg.RequestId, false);
                }
            }
        }

        // Hibernate: put the system into hibernation.
        private static void HandleHibernate(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                Logger.Info("Pipe: Hibernate request received - initiating system hibernation");
                bool success = Windows.PowrProf.SetSuspendState(
                    bHibernate: true,      // Hibernate, not sleep
                    bForce: false,         // Don't force - let apps save work
                    bWakeupEventsDisabled: false);  // Allow wake events

                if (success)
                {
                    Logger.Info("Pipe: Hibernate initiated successfully");
                }
                else
                {
                    int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    Logger.Error($"Pipe: Failed to initiate hibernate, Win32 error: {error}");
                }
                SendPipeAck(pipeMsg.RequestId, success);
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: Failed to hibernate: {ex.Message}");
                SendPipeAck(pipeMsg.RequestId, false);
            }
        }

        // DisableWindowsSleepTimers: zero Windows' own idle-to-sleep timeout for both AC
        // and DC, so it doesn't put the system to Sleep before the GoTweaks Hibernate
        // Timeout (System tab) ever fires.
        private static void HandleDisableWindowsSleepTimers(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                Logger.Info("Pipe: DisableWindowsSleepTimers request received - setting AC+DC Sleep idle timeout to Never");
                PowerManager.DisableSleepTimers();
                SendPipeAck(pipeMsg.RequestId, true);
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: DisableWindowsSleepTimers failed: {ex.Message}");
                SendPipeAck(pipeMsg.RequestId, false);
            }
        }

        // ExportLogs: copy the last 2 helper + widget logs to the Desktop.
        private static void HandleExportLogs(Shared.IPC.PipeMessage pipeMsg)
        {
            Logger.Info("Pipe: ExportLogs request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var exportFolder = Path.Combine(desktopPath, $"GoTweaks_Logs_{timestamp}");

                // Create export folder
                Directory.CreateDirectory(exportFolder);
                var helperFolder = Path.Combine(exportFolder, "Helper");
                var widgetFolder = Path.Combine(exportFolder, "Widget");
                Directory.CreateDirectory(helperFolder);
                Directory.CreateDirectory(widgetFolder);

                // Get log paths from app package location
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var packageFolder = Path.Combine(localAppData, "Packages", PackageConstants.PackageFamilyName);
                var helperLogPath = localAppData; // Helper logs are in %LocalAppData% when elevated
                var widgetLogPath = Path.Combine(packageFolder, "LocalState");

                // Copy helper logs (last 2) - check both locations
                var helperLogs = new List<string>();
                if (Directory.Exists(helperLogPath))
                {
                    helperLogs.AddRange(Directory.GetFiles(helperLogPath, "helper_*.log"));
                }
                var packageHelperLogPath = Path.Combine(packageFolder, "LocalCache", "Local");
                if (Directory.Exists(packageHelperLogPath))
                {
                    helperLogs.AddRange(Directory.GetFiles(packageHelperLogPath, "helper_*.log"));
                }
                foreach (var log in helperLogs.OrderByDescending(f => File.GetLastWriteTime(f)).Take(2))
                {
                    var destPath = Path.Combine(helperFolder, Path.GetFileName(log));
                    File.Copy(log, destPath, true);
                    Logger.Info($"Copied: {Path.GetFileName(log)}");
                }

                // Copy widget logs (last 2)
                if (Directory.Exists(widgetLogPath))
                {
                    var widgetLogs = Directory.GetFiles(widgetLogPath, "widget_*.log")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .Take(2);

                    foreach (var log in widgetLogs)
                    {
                        var destPath = Path.Combine(widgetFolder, Path.GetFileName(log));
                        File.Copy(log, destPath, true);
                        Logger.Info($"Copied: {Path.GetFileName(log)}");
                    }
                }

                Logger.Info($"Pipe: Logs exported to: {exportFolder}");
                response.Add("Success", true);
                response.Add("Path", exportFolder);
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: Failed to export logs: {ex.Message}");
                response.Add("Success", false);
                response.Add("Error", ex.Message);
            }

            // Send response
            if (pipeServer != null && pipeServer.IsConnected)
            {
                var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                responseMsg.RequestId = pipeMsg.RequestId;
                pipeServer.SendMessage(responseMsg.ToJson());
            }
        }

        // ExportProfiles: serialize global + per-game profiles (+ global widget
        // settings) to an XML file on the Desktop.
        private static void HandleExportProfiles(Shared.IPC.PipeMessage pipeMsg)
        {
            Logger.Info("Pipe: ExportProfiles request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var exportPath = Path.Combine(desktopPath, $"GoTweaks_Profiles_{timestamp}.xml");

                // Collect all profiles for export
                var gameProfilesList = new List<Shared.Data.GameProfile>();
                foreach (var kvp in profileManager.GameProfiles)
                {
                    gameProfilesList.Add(kvp.Value);
                }

                // Get app version from assembly
                var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";

                // Parse global widget settings from message (sent by widget as serialized GlobalWidgetSettings)
                Shared.Data.GlobalWidgetSettings globalSettings = null;
                if (pipeMsg.Extra.TryGetValue("GlobalSettings", out object gsXml) && gsXml is string gsXmlStr)
                {
                    try
                    {
                        // Deserialize the GlobalWidgetSettings directly
                        globalSettings = Shared.Utilities.XmlHelper.FromXMLString<Shared.Data.GlobalWidgetSettings>(gsXmlStr);
                        if (globalSettings != null)
                        {
                            Logger.Info("Pipe: Global widget settings included in export");
                            Logger.Info($"  TDP Limits: Min={globalSettings.DeviceTDPMin}, Max={globalSettings.DeviceTDPMax}");
                        }
                    }
                    catch (Exception gsEx)
                    {
                        Logger.Warn($"Pipe: Failed to parse global settings: {gsEx.Message}");
                    }
                }

                // Create export container
                var export = new Shared.Data.ProfileExport(
                    profileManager.GlobalProfile,
                    gameProfilesList,
                    appVersion,
                    globalSettings
                );

                // Serialize to XML file
                if (Shared.Utilities.XmlHelper.ToXMLFile(export, exportPath))
                {
                    Logger.Info($"Pipe: Profiles exported to: {exportPath}");
                    Logger.Info($"  Global profile + {gameProfilesList.Count} game profile(s) exported");
                    if (globalSettings != null)
                        Logger.Info("  Global widget settings (Legion buttons, scroll wheel, TDP limits, OSD) included");
                    response.Add("Success", true);
                    response.Add("Path", exportPath);
                    response.Add("ProfileCount", gameProfilesList.Count + 1); // +1 for global
                }
                else
                {
                    throw new Exception("Failed to write XML file");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: Failed to export profiles: {ex.Message}");
                response.Add("Success", false);
                response.Add("Error", ex.Message);
            }

            // Send response
            if (pipeServer != null && pipeServer.IsConnected)
            {
                var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                responseMsg.RequestId = pipeMsg.RequestId;
                pipeServer.SendMessage(responseMsg.ToJson());
            }
        }

        // ExportAutoSdrCurve: write the currently active Auto SDR curve (whichever preset) to
        // a Go2HDR-compatible JSON file at the widget-picked path.
        private static void HandleExportAutoSdrCurve(Shared.IPC.PipeMessage pipeMsg)
        {
            Logger.Info("Pipe: ExportAutoSdrCurve request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();
            try
            {
                var exportPath = pipeMsg.Extra["ExportAutoSdrCurve"] as string;
                if (string.IsNullOrEmpty(exportPath))
                    throw new Exception("No export path provided");

                if (systemManager.ExportAutoSdrCurve(exportPath, out var error))
                {
                    response.Add("Success", true);
                    response.Add("Path", exportPath);
                }
                else
                {
                    throw new Exception(error ?? "Unknown error");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: Failed to export Auto SDR curve: {ex.Message}");
                response.Add("Success", false);
                response.Add("Error", ex.Message);
            }

            if (pipeServer != null && pipeServer.IsConnected)
            {
                var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                responseMsg.RequestId = pipeMsg.RequestId;
                pipeServer.SendMessage(responseMsg.ToJson());
            }
        }

        // ImportAutoSdrCurve: read a Go2HDR-compatible curve JSON file from the widget-picked
        // path, apply it as the Custom curve, and switch to the Custom preset.
        private static void HandleImportAutoSdrCurve(Shared.IPC.PipeMessage pipeMsg)
        {
            Logger.Info("Pipe: ImportAutoSdrCurve request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();
            try
            {
                var importPath = pipeMsg.Extra["ImportAutoSdrCurve"] as string;
                if (string.IsNullOrEmpty(importPath) || !File.Exists(importPath))
                    throw new Exception($"Import file not found: {importPath}");

                if (systemManager.ImportAutoSdrCurve(importPath, out var error))
                {
                    response.Add("Success", true);
                }
                else
                {
                    throw new Exception(error ?? "Unknown error");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: Failed to import Auto SDR curve: {ex.Message}");
                response.Add("Success", false);
                response.Add("Error", ex.Message);
            }

            if (pipeServer != null && pipeServer.IsConnected)
            {
                var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                responseMsg.RequestId = pipeMsg.RequestId;
                pipeServer.SendMessage(responseMsg.ToJson());
            }
        }

        // ImportProfiles: restore global + per-game profiles from an export file.
        private static void HandleImportProfiles(Shared.IPC.PipeMessage pipeMsg)
        {
            Logger.Info("Pipe: ImportProfiles request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();
            try
            {
                // Get import path from message
                var importPath = pipeMsg.Extra["ImportProfiles"] as string;
                if (string.IsNullOrEmpty(importPath) || !File.Exists(importPath))
                {
                    throw new Exception($"Import file not found: {importPath}");
                }

                Logger.Info($"Pipe: Importing profiles from: {importPath}");

                // Deserialize the export file
                var import = Shared.Utilities.XmlHelper.FromXMLFile<Shared.Data.ProfileExport>(importPath);
                if (import == null)
                {
                    throw new Exception("Failed to parse import file");
                }

                Logger.Info($"Pipe: Import file version {import.Version}, created {import.ExportDate} by app v{import.AppVersion}");

                int importedCount = 0;
                int skippedCount = 0;

                // Import global profile settings (merge into existing global)
                var globalProfile = profileManager.GlobalProfile;
                globalProfile.TDP = import.GlobalProfile.TDP;
                globalProfile.CPUBoost = import.GlobalProfile.CPUBoost;
                globalProfile.CPUEPP = import.GlobalProfile.CPUEPP;
                globalProfile.MaxCPUState = import.GlobalProfile.MaxCPUState;
                globalProfile.MinCPUState = import.GlobalProfile.MinCPUState;
                globalProfile.TDP_DC = import.GlobalProfile.TDP_DC;
                globalProfile.CPUBoost_DC = import.GlobalProfile.CPUBoost_DC;
                globalProfile.CPUEPP_DC = import.GlobalProfile.CPUEPP_DC;
                globalProfile.MaxCPUState_DC = import.GlobalProfile.MaxCPUState_DC;
                globalProfile.MinCPUState_DC = import.GlobalProfile.MinCPUState_DC;
                globalProfile.FPSLimit = import.GlobalProfile.FPSLimit;
                globalProfile.FPSLimit_DC = import.GlobalProfile.FPSLimit_DC;
                globalProfile.OSPowerMode = import.GlobalProfile.OSPowerMode;
                globalProfile.OSPowerMode_DC = import.GlobalProfile.OSPowerMode_DC;
                Logger.Info("Global profile settings imported");
                importedCount++;

                // Import game profiles
                var profilesFolder = Profile.ProfileManager.GetGameProfilesFolder();
                foreach (var gameProfile in import.GameProfiles)
                {
                    try
                    {
                        // Generate profile path from game executable name
                        var exeName = Path.GetFileNameWithoutExtension(gameProfile.GameId.Path);
                        if (string.IsNullOrEmpty(exeName))
                        {
                            Logger.Warn($"Skipping profile with empty path: {gameProfile.GameId.Name}");
                            skippedCount++;
                            continue;
                        }

                        var profilePath = Path.Combine(profilesFolder, $"{exeName}.xml");

                        // Create a copy with the correct path set
                        var importedProfile = gameProfile;
                        importedProfile.Path = profilePath;

                        // Serialize directly to file
                        if (Shared.Utilities.XmlHelper.ToXMLFile(importedProfile, profilePath))
                        {
                            Logger.Info($"Imported profile for: {gameProfile.GameId.Name}");
                            importedCount++;
                        }
                        else
                        {
                            Logger.Warn($"Failed to save profile for: {gameProfile.GameId.Name}");
                            skippedCount++;
                        }
                    }
                    catch (Exception profileEx)
                    {
                        Logger.Warn($"Failed to import profile {gameProfile.GameId.Name}: {profileEx.Message}");
                        skippedCount++;
                    }
                }

                Logger.Info($"Pipe: Import complete - {importedCount} profile(s) imported, {skippedCount} skipped");
                response.Add("Success", true);
                response.Add("ImportedCount", importedCount);
                response.Add("SkippedCount", skippedCount);
                response.Add("Message", $"Imported {importedCount} profile(s). Restart the helper to load imported game profiles.");

                // Return global widget settings to widget for restoration
                if (import.GlobalSettings != null)
                {
                    try
                    {
                        var globalSettingsXml = Shared.Utilities.XmlHelper.ToXMLString(import.GlobalSettings, true);
                        response.Add("GlobalSettings", globalSettingsXml);
                        Logger.Info("Pipe: Global widget settings returned to widget for restoration");
                    }
                    catch (Exception gsEx)
                    {
                        Logger.Warn($"Pipe: Failed to serialize global settings for response: {gsEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: Failed to import profiles: {ex.Message}");
                response.Add("Success", false);
                response.Add("Error", ex.Message);
            }

            // Send response
            if (pipeServer != null && pipeServer.IsConnected)
            {
                var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                responseMsg.RequestId = pipeMsg.RequestId;
                pipeServer.SendMessage(responseMsg.ToJson());
            }
        }

        // CheckDriverUpdates: resolve machine type + BIOS via WMI and best-effort
        // fetch the Lenovo driver list; serve cached probe unless ForceRefresh.
        private static async Task HandleCheckDriverUpdates(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                // If the startup probe has already populated a result,
                // and the widget's request carries no explicit "Force"
                // hint, serve the cached result so the user sees the
                // list instantly instead of waiting for another live
                // Lenovo fetch.
                bool force = false;
                if (pipeMsg.Extra.TryGetValue("ForceRefresh", out var forceObj))
                {
                    if (forceObj is bool fb) force = fb;
                    else if (forceObj is string fs) bool.TryParse(fs, out force);
                }
                var probe = (!force && Services.LenovoDriverCheckService.LastResult != null)
                    ? Services.LenovoDriverCheckService.LastResult
                    : await Services.LenovoDriverCheckService.CheckAsync();
                var json = probe.ToJson();
                var response = new global::Windows.Foundation.Collections.ValueSet
                {
                    { "DriverUpdateResult", json },
                };
                if (pipeServer != null && pipeServer.IsConnected)
                {
                    var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                    responseMsg.RequestId = pipeMsg.RequestId;
                    pipeServer.SendMessage(responseMsg.ToJson());
                }
                Logger.Info($"Pipe: CheckDriverUpdates — MT={probe.MachineTypeCode}, BIOS={probe.BiosVersion}, live={probe.LiveFetchSucceeded}, count={probe.Drivers.Count}");

                // Log each entry flagged UpdateAvailable so we can debug "up to date but
                // flagged outdated" reports without round-tripping with the user for
                // Device Manager screenshots. Catalog version is the raw multi-vendor
                // string; installed is whatever PnP reported on the matched driver.
                // matchedDevice/matchedProvider/matchScore expose the fuzzy-match
                // pick so wrong-driver matches (e.g. "AMD Chipset Driver" matching
                // some other AMD-prefixed PnP entry) surface in the log directly.
                foreach (var d in probe.Drivers)
                {
                    if (d.UpdateStatus == Services.DriverUpdateStatus.UpdateAvailable)
                    {
                        Logger.Info($"  Driver flagged update: name='{d.Name}', category='{d.Category}', installed='{d.InstalledVersion}', catalog='{d.Version}', matchedDevice='{d.MatchedDeviceName}', matchedProvider='{d.MatchedProvider}', matchScore={d.MatchScore}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: CheckDriverUpdates threw: {ex.Message}");
                SendPipeAck(pipeMsg.RequestId, false);
            }
        }

        // SetDriverCheckOnStart: persist the "check for driver updates on start" pref.
        private static void HandleSetDriverCheckOnStart(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                bool val = true;
                if (pipeMsg.Extra.TryGetValue("SetDriverCheckOnStart", out var v))
                {
                    if (v is bool b) val = b;
                    else if (v is string s) bool.TryParse(s, out val);
                }
                Settings.LocalSettingsHelper.SetValue("DriverCheckOnStart", val);
                Logger.Info($"Pipe: SetDriverCheckOnStart = {val}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: SetDriverCheckOnStart threw: {ex.Message}");
            }
            SendPipeAck(pipeMsg.RequestId);
        }

        // SetGoTweaksCheckOnStart: persist the "check for app updates on start" pref.
        private static void HandleSetGoTweaksCheckOnStart(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                bool val = true;
                if (pipeMsg.Extra.TryGetValue("SetGoTweaksCheckOnStart", out var v))
                {
                    if (v is bool b) val = b;
                    else if (v is string s) bool.TryParse(s, out val);
                }
                Settings.LocalSettingsHelper.SetValue("GoTweaksCheckOnStart", val);
                Logger.Info($"Pipe: SetGoTweaksCheckOnStart = {val}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: SetGoTweaksCheckOnStart threw: {ex.Message}");
            }
            SendPipeAck(pipeMsg.RequestId);
        }

        // CheckGoTweaksUpdate: serve the cached self-update probe unless ForceRefresh.
        private static async Task HandleCheckGoTweaksUpdate(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                bool force = false;
                if (pipeMsg.Extra.TryGetValue("ForceRefresh", out var forceObj))
                {
                    if (forceObj is bool fb) force = fb;
                    else if (forceObj is string fs) bool.TryParse(fs, out force);
                }
                var result = (!force && Services.GoTweaksUpdateService.LastResult != null)
                    ? Services.GoTweaksUpdateService.LastResult
                    : await Services.GoTweaksUpdateService.CheckAsync(helperVersion);
                if (pipeServer != null && pipeServer.IsConnected)
                {
                    var response = new global::Windows.Foundation.Collections.ValueSet
                    {
                        { "GoTweaksUpdate", result.ToJson() },
                    };
                    var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                    responseMsg.RequestId = pipeMsg.RequestId;
                    pipeServer.SendMessage(responseMsg.ToJson());
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: CheckGoTweaksUpdate threw: {ex.Message}");
                SendPipeAck(pipeMsg.RequestId, false);
            }
        }

        // InstallGoTweaksUpdate: download + install a GoTweaks self-update.
        private static async Task HandleInstallGoTweaksUpdate(Shared.IPC.PipeMessage pipeMsg)
        {
            string resultJson;
            try
            {
                string gtUrl = null;
                if (pipeMsg.Extra.TryGetValue("InstallGoTweaksUpdate", out var urlObj))
                    gtUrl = urlObj?.ToString();
                resultJson = await Services.GoTweaksUpdateService.InstallAsync(gtUrl);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: InstallGoTweaksUpdate threw: {ex.Message}");
                resultJson = "{\"success\":false,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}";
            }
            if (pipeServer != null && pipeServer.IsConnected)
            {
                var response = new global::Windows.Foundation.Collections.ValueSet
                {
                    { "GoTweaksUpdateInstallResult", resultJson },
                };
                var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                responseMsg.RequestId = pipeMsg.RequestId;
                pipeServer.SendMessage(responseMsg.ToJson());
            }
        }

        // BatchInstallDrivers: download (bounded parallel) + launch a set of Lenovo
        // driver installers sequentially.
        private static async Task HandleBatchInstallDrivers(Shared.IPC.PipeMessage pipeMsg)
        {
            string batchResult;
            try
            {
                var urls = new List<string>();
                if (pipeMsg.Extra.TryGetValue("BatchInstallDrivers", out var urlsObj) && urlsObj is string joined)
                {
                    // Widget serialises as newline-joined string — ValueSet doesn't
                    // support nested arrays cleanly across the pipe contract.
                    foreach (var line in joined.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0) urls.Add(trimmed);
                    }
                }
                batchResult = await Services.LenovoDriverCheckService.BatchInstallAsync(urls);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: BatchInstallDrivers threw: {ex.Message}");
                batchResult = "{\"success\":false,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}";
            }
            if (pipeServer != null && pipeServer.IsConnected)
            {
                var response = new global::Windows.Foundation.Collections.ValueSet
                {
                    { "DriverBatchInstallResult", batchResult },
                };
                var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                responseMsg.RequestId = pipeMsg.RequestId;
                pipeServer.SendMessage(responseMsg.ToJson());
            }
        }

        // InstallDriverUpdate: download a single Lenovo driver and launch its installer.
        private static async Task HandleInstallDriverUpdate(Shared.IPC.PipeMessage pipeMsg)
        {
            string resultJson;
            try
            {
                string installUrl = null;
                if (pipeMsg.Extra.TryGetValue("InstallDriverUpdate", out var urlObj))
                {
                    installUrl = urlObj?.ToString();
                }
                resultJson = await Services.LenovoDriverCheckService.InstallDriverAsync(installUrl);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: InstallDriverUpdate threw: {ex.Message}");
                resultJson = "{\"success\":false,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}";
            }
            if (pipeServer != null && pipeServer.IsConnected)
            {
                var response = new global::Windows.Foundation.Collections.ValueSet
                {
                    { "DriverInstallResult", resultJson },
                };
                var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                responseMsg.RequestId = pipeMsg.RequestId;
                pipeServer.SendMessage(responseMsg.ToJson());
            }
        }

        // ReapplyTDP: re-push the current cached TDP to hardware (after a power-source
        // change / Custom re-entry). Must NOT modify the profile's saved TDP.
        private static void HandleReapplyTDP(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                if (performanceManager != null)
                {
                    int currentTdp = performanceManager.TDP.Value;
                    Logger.Info($"Pipe: ReapplyTDP request — re-pushing current {currentTdp}W to hardware");
                    performanceManager.SetTDP(currentTdp);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: ReapplyTDP threw: {ex.Message}");
            }
            SendPipeAck(pipeMsg.RequestId);
        }

        // SyncGameProfile: payload "boost=1;epp=0;max=100;min=5" (only the settings the widget
        // captures per-game). The widget and helper each keep their own copy of a game profile; the
        // helper's only learns of changes through property-change events, which are skipped during the
        // post-switch cooldown and when a value already matches, so it drifts from what the Profiles
        // tab shows - and the helper re-applies its stale copy whenever it re-detects the game. This
        // applies the widget's values and writes them into the helper's copy explicitly.
        private static void HandleSyncGameProfile(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                string payload = pipeMsg.Extra.TryGetValue("SyncGameProfile", out var v) ? v?.ToString() : null;
                if (!string.IsNullOrEmpty(payload))
                {
                    lock (profileApplicationLock)
                    {
                        var current = profileManager.CurrentProfile;
                        if (current.IsGlobalProfile || !current.Use)
                        {
                            Logger.Info("Pipe: SyncGameProfile ignored - no per-game profile is active");
                        }
                        else
                        {
                            // Keeps the CPUBoost_/CPUEPP_/CPUState_PropertyChanged handlers from also
                            // saving these (they'd route by flag; we write the profile directly below).
                            isApplyingProfile = true;
                            try
                            {
                                foreach (var pair in payload.Split(';'))
                                {
                                    var kv = pair.Split('=');
                                    if (kv.Length != 2) continue;

                                    if (kv[0] == "boost" && ProfileSaveFlagsState.CPUBoost)
                                    {
                                        bool boost = kv[1] == "1";
                                        powerManager.CPUBoost.SetValue(boost);
                                        current.CPUBoost = boost;
                                    }
                                    else if (kv[0] == "epp" && ProfileSaveFlagsState.CPUEPP && int.TryParse(kv[1], out int epp))
                                    {
                                        powerManager.CPUEPP.SetValue(epp);
                                        current.CPUEPP = epp;
                                    }
                                    else if (kv[0] == "max" && ProfileSaveFlagsState.CPUState && int.TryParse(kv[1], out int maxState))
                                    {
                                        powerManager.MaxCPUState.SetValue(maxState);
                                        current.MaxCPUState = maxState;
                                    }
                                    else if (kv[0] == "min" && ProfileSaveFlagsState.CPUState && int.TryParse(kv[1], out int minState))
                                    {
                                        powerManager.MinCPUState.SetValue(minState);
                                        current.MinCPUState = minState;
                                    }
                                }
                                Logger.Info($"Pipe: SyncGameProfile applied to '{current.GameId.Name}': {payload}");
                            }
                            finally
                            {
                                isApplyingProfile = false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: SyncGameProfile threw: {ex.Message}");
            }
            SendPipeAck(pipeMsg.RequestId);
        }

        // RefreshPanelBrightness: re-read the built-in panel brightness + availability from WMI and
        // push both to the widget, so the slider always reflects the true current value (even if it
        // was changed elsewhere) and grays out when the internal panel is off. ForceSetValue pushes
        // even when unchanged so the widget re-asserts its state every panel open.
        private static void HandleRefreshPanelBrightness(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                if (systemManager != null)
                {
                    bool supported = XboxGamingBarHelper.Sidebar.BrightnessManager.IsSupported();
                    systemManager.PanelBrightnessSupported.ForceSetValue(supported);
                    if (supported)
                    {
                        int fresh = XboxGamingBarHelper.Sidebar.BrightnessManager.GetBrightness();
                        systemManager.PanelBrightness.ForceSetValue(fresh);
                    }
                    Logger.Info($"Pipe: RefreshPanelBrightness -> supported={supported}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: RefreshPanelBrightness threw: {ex.Message}");
            }
            SendPipeAck(pipeMsg.RequestId);
        }

        // CalibrateLegionGyro: one-shot firmware gyro-bias capture on the Legion pads.
        private static void HandleCalibrateLegionGyro(Shared.IPC.PipeMessage pipeMsg)
        {
            try
            {
                var monitor = legionButtonMonitor;
                bool ok = monitor != null && monitor.CalibrateGyro(true, true);
                Logger.Info($"Pipe: CalibrateLegionGyro request -> {(ok ? "sent" : "failed")}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Pipe: CalibrateLegionGyro threw: {ex.Message}");
            }
            SendPipeAck(pipeMsg.RequestId);
        }

        // ExitHelper: intentional shutdown for a restart (legacy version-mismatch path).
        private static void HandleExitHelper(Shared.IPC.PipeMessage pipeMsg)
        {
            Logger.Info("Pipe: ExitHelper request received - shutting down helper for restart");
            // Tell the widget this is an INTENTIONAL exit so its pipe-disconnect handler
            // doesn't auto-relaunch us mid-shutdown (which raced the dying helper and
            // spawned two instances contending for the pipe — issue #81). The widget
            // owns relaunch timing after an intentional exit.
            NotifyWidgetHelperExiting("ExitHelper");
            LogManager.Flush(); // Ensure log is written before we start shutdown
            SendPipeAck(pipeMsg.RequestId);
            _isShuttingDown = true;
            // Force a prompt exit so the single-instance mutex is freed quickly and a
            // takeover helper's startup wait (Program.cs) resolves fast, instead of
            // lingering up to a full main-loop poll interval.
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Logger.Info("ExitHelper: forcing process exit");
                LogManager.Flush();
                Environment.Exit(0);
            });
        }

        // UpgradeHelper: UAC-free in-place upgrade. Widget passes the MSIX source path;
        // helper launches the copy script after exit.
        private static void HandleUpgradeHelper(Shared.IPC.PipeMessage pipeMsg)
        {
            string msixSourcePath = pipeMsg.Extra["UpgradeHelper"]?.ToString();
            if (!string.IsNullOrEmpty(msixSourcePath))
            {
                Logger.Info($"Pipe: UpgradeHelper request received - source: {msixSourcePath}");
                NotifyWidgetHelperExiting("UpgradeHelper");
                LogManager.Flush(); // Ensure log is written before we start shutdown
                SendPipeAck(pipeMsg.RequestId);

                // Launch upgrade script that will copy files and restart after we exit
                LaunchUpgradeScript(msixSourcePath);

                _isShuttingDown = true;

                // Schedule a forced exit
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000); // Give main loop time to exit gracefully
                    if (_isShuttingDown)
                    {
                        Logger.Info("Forcing exit for upgrade");
                        LogManager.Flush(); // Ensure log is written before exit

                        // Release mutex before exiting to ensure clean restart
                        try
                        {
                            singleInstanceMutex?.ReleaseMutex();
                            singleInstanceMutex?.Dispose();
                        }
                        catch { /* Ignore mutex errors during shutdown */ }

                        Environment.Exit(0);
                    }
                });
            }
            else
            {
                Logger.Warn("Pipe: UpgradeHelper request missing source path");
                SendPipeAck(pipeMsg.RequestId, false);
            }
        }

        /// <summary>
        /// Handles property Get/Set requests from the pipe
        /// </summary>
        private static void HandlePipePropertyRequest(Shared.IPC.PipeMessage request)
        {
            try
            {
                global::Windows.Foundation.Collections.ValueSet response = null;

                // Handle special functions that are not in FunctionalProperties
                int functionValue = (int)request.Function;

                // Labs: DAService Status request
                if (functionValue == (int)Function.Labs_DAServiceStatus)
                {
                    response = HandleLabsDAServiceStatus(request);
                }
                // Labs: DAService Control request (Start/Stop)
                else if (functionValue == (int)Function.Labs_DAServiceControl)
                {
                    response = HandleLabsDAServiceControl(request);
                }
                // Labs: Task View fix status request
                else if (functionValue == (int)Function.Labs_TaskViewFixStatus)
                {
                    response = HandleLabsTaskViewFixStatus(request);
                }
                // Labs: Task View fix control (enable/disable/run-now)
                else if (functionValue == (int)Function.Labs_TaskViewFixControl)
                {
                    response = HandleLabsTaskViewFixControl(request);
                }
                // Labs: Legion Button Remap
                else if (functionValue == (int)Function.Labs_LegionButtonRemap)
                {
                    response = HandleLabsLegionButtonRemap(request);
                }
                // Labs: Scroll Wheel Remap
                else if (functionValue == (int)Function.Labs_LegionScrollRemap)
                {
                    response = HandleLabsLegionScrollRemap(request);
                }
                // Labs: Legion R Brightness Gesture (hold + tilt stick)
                else if (functionValue == (int)Function.Labs_LegionRBrightnessGesture)
                {
                    response = HandleLabsLegionRBrightnessGesture(request);
                }
                // Controller Hotkey Config: Receive hotkey settings from widget for XInput monitoring
                else if (functionValue == (int)Function.ControllerHotkeyConfig)
                {
                    response = HandleControllerHotkeyConfig(request);
                }
                // Quick-tile controller combos: JSON array of tiles with a combo binding.
                // Content may be "" to clear all bindings.
                else if (functionValue == (int)Function.TileHotkeyConfig)
                {
                    ApplyTileHotkeys(request.Content?.ToString() ?? "");
                }
                // Profile Save Flags: which settings the widget wants captured per-game vs.
                // left as device-wide globals. Routes helper-side writes in the AutoTDP and
                // Legion controller setting handlers.
                else if (functionValue == (int)Function.ProfileSaveFlags)
                {
                    response = HandleProfileSaveFlags(request);
                }
                // Per-state TDP / TDPBoost values from the widget. Cached so the helper
                // can apply them on AC/DC transitions independent of widget lifecycle.
                else if (functionValue == (int)Function.PowerSourceProfileValues)
                {
                    response = HandlePowerSourceProfileValues(request);
                }
                // Quick Metrics: Enable/disable metrics push timer
                else if (functionValue == (int)Function.QuickMetricsEnabled)
                {
                    response = HandleQuickMetricsEnabled(request);
                }
                // Screen Saver: Enable/disable idle-triggered screen saver
                else if (functionValue == (int)Function.ScreenSaverEnabled)
                {
                    response = HandleScreenSaverEnabled(request);
                }
                // Software gyro bias capture or reset. Steam-style one-shot calibration:
                // user puts the device on a flat surface, presses Calibrate, we sample for
                // ~500 ms and store the average as the bias offset. LegionButtonMonitor
                // subtracts this from every TryGetLatestGyroSample read so all gyro
                // consumers (legacy CE stick-gyro, VIIPER stick-gyro, VIIPER native DS4 /
                // DualSense / Xbox forwarding) see the corrected stream.
                else if (functionValue == (int)Function.CalibrateGyroBias)
                {
                    response = HandleCalibrateGyroBias(request);
                }
                // (AutoHibernateMode handler removed — Auto Hibernate feature retired,
                // 2026-07-10 System-tab cleanup. Function ordinal stays reserved.)
                // (ViGEmBusInstalled / InstallViGEmBus handlers removed — ViGEm
                // backend retired. The Function enum entries stay so the wire
                // values of later entries don't shift; unknown functions from
                // any stale sender are simply ignored.)
                // HidHide: Check installed status
                else if (functionValue == (int)Function.HidHideInstalled)
                {
                    response = HandleHidHideInstalled(request);
                }
                // HidHide: Install request - only install when explicitly requested with Set command and "install" content
                else if (functionValue == (int)Function.InstallHidHide)
                {
                    response = HandleInstallHidHide(request);
                }
                // Debug: Export Per-Game Profiles
                else if (functionValue == (int)Function.Debug_ExportProfiles)
                {
                    response = HandleDebugExportProfiles(request);
                }
                // Debug: Check for local update (AppPackages)
                else if (functionValue == (int)Function.CheckLocalUpdate)
                {
                    response = HandleCheckLocalUpdate(request);
                }
                // Debug/Development: Install Update (download and install from URL or local path)
                else if (functionValue == (int)Function.InstallUpdate)
                {
                    response = HandleInstallUpdate(request);
                }
                // System Restore: Prepare for Uninstall
                else if (functionValue == (int)Function.PrepareForUninstall)
                {
                    response = HandlePrepareForUninstall(request);
                }
                // System Restore: Get status of saved original values
                else if (functionValue == (int)Function.SystemRestoreStatus)
                {
                    response = HandleSystemRestoreStatus(request);
                }
                // Export All Data (comprehensive backup)
                else if (functionValue == (int)Function.ExportAllData)
                {
                    response = HandleExportAllData(request);
                }
                // Import All Data (restore from backup)
                else if (functionValue == (int)Function.ImportAllData)
                {
                    response = HandleImportAllData(request);
                }
                // PawnIO Debug: Get CPU Info
                else if (functionValue == (int)Function.PawnIOGetCpuInfo)
                {
                    response = HandlePawnIOGetCpuInfo(request);
                }
                // PawnIO Debug: Apply Settings
                else if (functionValue == (int)Function.PawnIOApplySettings)
                {
                    response = HandlePawnIOApplySettings(request);
                }
                else
                {
                    // Convert to ValueSet and use the existing property handling
                    var valueSet = request.ToValueSet();
                    response = properties.HandlePipeMessage(valueSet);
                }

                if (response != null && pipeServer != null && pipeServer.IsConnected)
                {
                    // Convert response to JSON and send back
                    // Echo the RequestId so client can correlate the response
                    var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                    responseMsg.RequestId = request.RequestId;
                    pipeServer.SendMessage(responseMsg.ToJson());
                    Logger.Debug($"Sent pipe response for {request.Function} (RequestId={request.RequestId})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling pipe property request: {ex.Message}");
            }
        }

        // ====================================================================
        // Function-keyed "special" handlers for HandlePipePropertyRequest above.
        // These cover Functions that are NOT backed by a FunctionalProperty.
        // Each builds and returns the response ValueSet to send back (or null
        // when there is nothing to send / it already sent + early-returned).
        // Extracted verbatim from the former if/else-if chain — behaviour is
        // unchanged; the dispatcher applies the same Function match per branch.
        // ====================================================================

        // Labs: DAService Status request
        private static global::Windows.Foundation.Collections.ValueSet HandleLabsDAServiceStatus(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            int functionValue = (int)request.Function;
            int status = GetDAServiceStatus();
            response = new global::Windows.Foundation.Collections.ValueSet();
            response.Add(nameof(Function), functionValue);
            response.Add("Content", status);
            response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            Logger.Info($"Pipe: Labs DAService status = {status}");
            return response;
        }

        // Labs: DAService Control request (Start/Stop)
        private static global::Windows.Foundation.Collections.ValueSet HandleLabsDAServiceControl(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            if (request.Content != null)
            {
                int action = Convert.ToInt32(request.Content);
                ControlDAService(action);
                // No sleep needed: ControlDAService applies the permanent startup-type change
                // synchronously (the background task only handles the slow stop/start), and
                // GetDAServiceStatus reports that StartType — so it's already accurate here.
                int status = GetDAServiceStatus();
                response = new global::Windows.Foundation.Collections.ValueSet();
                response.Add(nameof(Function), (int)Function.Labs_DAServiceStatus);
                response.Add("Content", status);
                response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                Logger.Info($"Pipe: Labs DAService control action={action}, new status={status}");
            }
            return response;
        }

        // Labs: Task View fix status request
        private static global::Windows.Foundation.Collections.ValueSet HandleLabsTaskViewFixStatus(Shared.IPC.PipeMessage request)
        {
            int status = Labs.TaskViewFixManager.GetStatus();
            var response = new global::Windows.Foundation.Collections.ValueSet();
            response.Add(nameof(Function), (int)Function.Labs_TaskViewFixStatus);
            response.Add("Content", status);
            response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            Logger.Info($"Pipe: Labs TaskViewFix status = {status}");
            return response;
        }

        // Labs: Task View fix control — Content 0 = disable, 1 = enable, 2 = run once now.
        private static global::Windows.Foundation.Collections.ValueSet HandleLabsTaskViewFixControl(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            if (request.Content != null)
            {
                int action = Convert.ToInt32(request.Content);
                Labs.TaskViewFixManager.Control(action);
                int status = Labs.TaskViewFixManager.GetStatus();
                response = new global::Windows.Foundation.Collections.ValueSet();
                response.Add(nameof(Function), (int)Function.Labs_TaskViewFixStatus);
                response.Add("Content", status);
                response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                Logger.Info($"Pipe: Labs TaskViewFix control action={action}, status={status}");
            }
            return response;
        }

        // Labs: Legion Button Remap
        private static global::Windows.Foundation.Collections.ValueSet HandleLabsLegionButtonRemap(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            string button = "L";
            bool enabled = false;
            int actionType = 0;
            string shortcut = "";

            if (request.Extra.TryGetValue("Button", out object buttonObj))
                button = buttonObj?.ToString() ?? "L";
            if (request.Extra.TryGetValue("Enabled", out object enabledObj))
                enabled = Convert.ToBoolean(enabledObj);
            if (request.Extra.TryGetValue("Action", out object actionObj))
                actionType = Convert.ToInt32(actionObj);
            if (request.Extra.TryGetValue("Shortcut", out object shortcutObj))
                shortcut = shortcutObj?.ToString() ?? "";

            bool success = ConfigureLegionButtonRemap(button, enabled, actionType, shortcut);
            response = new global::Windows.Foundation.Collections.ValueSet();
            response.Add("Success", success);
            Logger.Info($"Pipe: Legion {button} Remap - Enabled: {enabled}, Success: {success}");
            return response;
        }

        // Labs: Brightness Gesture (enabled + trigger button + axis)
        private static global::Windows.Foundation.Collections.ValueSet HandleLabsLegionRBrightnessGesture(Shared.IPC.PipeMessage request)
        {
            bool enabled = false;
            if (request.Extra.TryGetValue("Enabled", out object enabledObj))
                enabled = Convert.ToBoolean(enabledObj);

            int triggerType = 0;
            if (request.Extra.TryGetValue("Trigger", out object triggerObj))
                triggerType = Convert.ToInt32(triggerObj);

            int axisType = 0;
            if (request.Extra.TryGetValue("Axis", out object axisObj))
                axisType = Convert.ToInt32(axisObj);

            bool success = ConfigureLegionBrightnessGesture(enabled, triggerType, axisType);

            try
            {
                Settings.LocalSettingsHelper.SetValue("LegionR_BrightnessGesture", enabled);
                Settings.LocalSettingsHelper.SetValue("BrightnessGesture_Trigger", triggerType);
                Settings.LocalSettingsHelper.SetValue("BrightnessGesture_Axis", axisType);
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: Failed to persist brightness gesture settings: {ex.Message}");
            }

            var response = new global::Windows.Foundation.Collections.ValueSet();
            response.Add("Success", success);
            Logger.Info($"Pipe: Brightness Gesture - Enabled: {enabled}, Trigger: {triggerType}, Axis: {axisType}, Success: {success}");
            return response;
        }

        // Labs: Scroll Wheel Remap
        private static global::Windows.Foundation.Collections.ValueSet HandleLabsLegionScrollRemap(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            string direction = "Up";
            bool enabled = false;
            int actionType = 0;
            string shortcut = "";

            if (request.Extra.TryGetValue("Direction", out object directionObj))
                direction = directionObj?.ToString() ?? "Up";
            if (request.Extra.TryGetValue("Enabled", out object enabledObj))
                enabled = Convert.ToBoolean(enabledObj);
            if (request.Extra.TryGetValue("Action", out object actionObj))
                actionType = Convert.ToInt32(actionObj);
            if (request.Extra.TryGetValue("Shortcut", out object shortcutObj))
                shortcut = shortcutObj?.ToString() ?? "";

            bool success = ConfigureLegionScrollRemap(direction, enabled, actionType, shortcut);
            response = new global::Windows.Foundation.Collections.ValueSet();
            response.Add("Success", success);
            Logger.Info($"Pipe: Scroll {direction} Remap - Enabled: {enabled}, Success: {success}");
            return response;
        }

        // Controller Hotkey Config: Receive hotkey settings from widget for XInput monitoring
        private static global::Windows.Foundation.Collections.ValueSet HandleControllerHotkeyConfig(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            if (request.Content != null)
            {
                string configJson = request.Content.ToString();
                ApplyControllerHotkeyConfig(configJson);
            }
            return response;
        }

        // Profile Save Flags: which settings the widget wants captured per-game vs.
        // left as device-wide globals.
        private static global::Windows.Foundation.Collections.ValueSet HandleProfileSaveFlags(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            if (request.Content != null)
            {
                ApplyProfileSaveFlags(request.Content.ToString());
            }
            return response;
        }

        // Per-state TDP / TDPBoost values from the widget.
        private static global::Windows.Foundation.Collections.ValueSet HandlePowerSourceProfileValues(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            if (request.Content != null)
            {
                ApplyPowerSourceProfileValues(request.Content.ToString());
            }
            return response;
        }

        // Quick Metrics: Enable/disable metrics push timer
        private static global::Windows.Foundation.Collections.ValueSet HandleQuickMetricsEnabled(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            if (request.Content != null && performanceManager != null)
            {
                bool enabled = false;
                if (bool.TryParse(request.Content.ToString(), out enabled) || request.Content.ToString() == "True" || request.Content.ToString() == "true")
                {
                    enabled = request.Content.ToString().ToLower() == "true" || request.Content.ToString() == "True";
                }
                performanceManager.QuickMetricsEnabled = enabled;
                Logger.Info($"Pipe: Quick Metrics enabled set to: {enabled}");
            }
            return response;
        }

        // Screen Saver: Enable/disable idle-triggered screen saver
        private static global::Windows.Foundation.Collections.ValueSet HandleScreenSaverEnabled(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            if (request.Content != null)
            {
                bool enabled = request.Content.ToString().ToLower() == "true";
                SetScreenSaverEnabled(enabled);
                Logger.Info($"Pipe: Screen Saver enabled set to: {enabled}");
            }
            return response;
        }

        // Software gyro bias capture or reset (Steam-style one-shot calibration). Samples
        // ~500 ms and stores the average as the bias offset (subtracted by LegionButtonMonitor
        // for all gyro consumers). Sends results via SendGyroBiasOffsetToWidget; no response here.
        private static global::Windows.Foundation.Collections.ValueSet HandleCalibrateGyroBias(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            string action = request.Content?.ToString()?.Trim()?.ToLowerInvariant() ?? "";
            if (action == "reset")
            {
                XboxGamingBarHelper.Labs.LegionButtonMonitor.ClearGyroBias();
                SendGyroBiasOffsetToWidget();
                Logger.Info("Pipe: CalibrateGyroBias reset");
            }
            else
            {
                Logger.Info("Pipe: CalibrateGyroBias capture starting (500 ms sample window)");
                // Run async so the pipe doesn't block during the capture window.
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var xs = new System.Collections.Generic.List<double>(256);
                        var ys = new System.Collections.Generic.List<double>(256);
                        var zs = new System.Collections.Generic.List<double>(256);
                        long lastTimestamp = 0;
                        var start = DateTime.UtcNow;
                        while ((DateTime.UtcNow - start).TotalMilliseconds < 500)
                        {
                            // Average both controllers when both are present so we get a
                            // single combined bias. The forwarder uses Merge() at runtime,
                            // which averages signs-corrected left+right, so we approximate
                            // the same here by summing samples from whichever sides reply.
                            bool any = false;
                            if (XboxGamingBarHelper.Labs.LegionButtonMonitor.TryGetLatestRawGyroSample(true, out var left)
                                && left.TimestampTicksUtc != lastTimestamp)
                            {
                                xs.Add(left.GyroXDegPerSecond);
                                ys.Add(left.GyroYDegPerSecond);
                                zs.Add(left.GyroZDegPerSecond);
                                any = true;
                            }
                            if (XboxGamingBarHelper.Labs.LegionButtonMonitor.TryGetLatestRawGyroSample(false, out var right)
                                && right.TimestampTicksUtc != lastTimestamp)
                            {
                                xs.Add(right.GyroXDegPerSecond);
                                ys.Add(right.GyroYDegPerSecond);
                                zs.Add(right.GyroZDegPerSecond);
                                any = true;
                                lastTimestamp = right.TimestampTicksUtc;
                            }
                            if (!any) System.Threading.Thread.Sleep(2);
                            else System.Threading.Thread.Sleep(4); // ~250 Hz outer poll
                        }
                        int count = xs.Count;
                        if (count < 8)
                        {
                            Logger.Warn($"Pipe: CalibrateGyroBias capture insufficient samples ({count}) — leaving bias unchanged. Are the controllers attached and producing input?");
                            SendGyroBiasOffsetToWidget(); // still push so widget can show "not calibrated"
                            return;
                        }
                        // Compute median-and-MAD per axis and reject samples outside
                        // median +/- k*MAD. MAD scaled by 1.4826 ~= Gaussian sigma, k=6
                        // keeps ~99.99% of clean noise while pruning the ~one-in-thousand
                        // single-sample BMI260 spikes (~+/-15 deg/s, see capture audit
                        // 2026-05-30). Final mean is computed on the kept samples; if any
                        // axis drops > 25% the capture is rejected as "not still enough".
                        AxisStats sx = ComputeRobustAxisStats(xs);
                        AxisStats sy = ComputeRobustAxisStats(ys);
                        AxisStats sz = ComputeRobustAxisStats(zs);
                        int maxDropped = Math.Max(sx.Dropped, Math.Max(sy.Dropped, sz.Dropped));
                        if (maxDropped * 4 > count)
                        {
                            Logger.Warn($"Pipe: CalibrateGyroBias rejected — too many outliers (X dropped={sx.Dropped} Y={sy.Dropped} Z={sz.Dropped} of {count}). Was the device actually still?");
                            SendGyroBiasOffsetToWidget();
                            return;
                        }
                        float bx = (float)sx.TrimmedMean;
                        float by = (float)sy.TrimmedMean;
                        float bz = (float)sz.TrimmedMean;
                        long atUtc = DateTime.UtcNow.Ticks;
                        XboxGamingBarHelper.Labs.LegionButtonMonitor.SetGyroBias(bx, by, bz, atUtc);
                        Logger.Info($"Pipe: CalibrateGyroBias captured {count} samples, bias X={bx:F3} Y={by:F3} Z={bz:F3} deg/s");
                        Logger.Info($"  X: trimMean={bx:F3} trimStd={sx.TrimmedStd:F3} rawMean={sx.RawMean:F3} rawStd={sx.RawStd:F3} min={sx.Min:F3} max={sx.Max:F3} dropped={sx.Dropped}");
                        Logger.Info($"  Y: trimMean={by:F3} trimStd={sy.TrimmedStd:F3} rawMean={sy.RawMean:F3} rawStd={sy.RawStd:F3} min={sy.Min:F3} max={sy.Max:F3} dropped={sy.Dropped}");
                        Logger.Info($"  Z: trimMean={bz:F3} trimStd={sz.TrimmedStd:F3} rawMean={sz.RawMean:F3} rawStd={sz.RawStd:F3} min={sz.Min:F3} max={sz.Max:F3} dropped={sz.Dropped}");
                        SendGyroBiasOffsetToWidget();
                    }
                    catch (Exception capEx)
                    {
                        Logger.Warn($"Pipe: CalibrateGyroBias capture threw: {capEx.Message}");
                    }
                });
            }
            return response;
        }

        // HidHide: Check installed status
        private static global::Windows.Foundation.Collections.ValueSet HandleHidHideInstalled(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            int functionValue = (int)request.Function;
            bool installed = XboxGamingBarHelper.Labs.HidHideHelper.IsInstalled();
            response = new global::Windows.Foundation.Collections.ValueSet();
            response.Add(nameof(Function), functionValue);
            response.Add("Content", installed);
            response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            Logger.Info($"Pipe: HidHide installed status: {installed}");
            return response;
        }

        // HidHide: Install request - only install when explicitly requested with Set + "install"
        private static global::Windows.Foundation.Collections.ValueSet HandleInstallHidHide(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = null;
            bool shouldInstall = request.Command == Shared.Enums.Command.Set && request.Content == "install";

            if (!shouldInstall)
            {
                Logger.Debug("Pipe: InstallHidHide - Ignoring non-install request (Get or empty content)");
                return null;
            }

            Logger.Info("Pipe: HidHide installation requested from widget");
            _ = Task.Run(() =>
            {
                bool success = XboxGamingBarHelper.Labs.HidHideHelper.Install();
                bool installed = XboxGamingBarHelper.Labs.HidHideHelper.IsInstalled();
                var updateMsg = new Shared.IPC.PipeMessage
                {
                    Command = Shared.Enums.Command.Set,
                    Function = Function.HidHideInstalled,
                    Content = installed.ToString()
                };
                SendPipeMessage(updateMsg);
                Logger.Info($"Pipe: HidHide installation complete (success={success}), sent updated status: {installed}");
            });

            response = new global::Windows.Foundation.Collections.ValueSet();
            response.Add("Content", true); // Acknowledge request started
            return response;
        }

        // Debug: Export Per-Game Profiles
        private static global::Windows.Foundation.Collections.ValueSet HandleDebugExportProfiles(Shared.IPC.PipeMessage request)
        {
            global::Windows.Foundation.Collections.ValueSet response = new global::Windows.Foundation.Collections.ValueSet();
            try
            {
                string exportPath = ExportProfiles();
                response.Add("ExportPath", exportPath);
                Logger.Info($"Pipe: Profiles exported to {exportPath}");
            }
            catch (Exception ex)
            {
                response.Add("Error", ex.Message);
                Logger.Error($"Pipe: Failed to export profiles: {ex.Message}");
            }
            return response;
        }

        // Debug: Check for local update (AppPackages)
        private static global::Windows.Foundation.Collections.ValueSet HandleCheckLocalUpdate(Shared.IPC.PipeMessage request)
        {
            Logger.Info("Pipe: CheckLocalUpdate request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();

            try
            {
                // Resolve the AppPackages probe directory in this order:
                //   1. GOTWEAKS_APPPACKAGES_DIR env var (overrides everything — useful
                //      for any developer with a non-default checkout location).
                //   2. Walk up from the deployed helper's source-tree, looking for a
                //      "XboxGamingBarPackage\AppPackages" directory. This works on the
                //      author's machine and any contributor working from a clone.
                // On a normal user's installed system, both will fail and we return a
                // clear "Error" — the widget's debug-panel update probe handles that
                // gracefully (this whole code path is debug-only UX).
                string appPackagesPath = ResolveAppPackagesProbeDir();

                if (string.IsNullOrEmpty(appPackagesPath) || !Directory.Exists(appPackagesPath))
                {
                    response.Add("Error", $"AppPackages folder not found (set GOTWEAKS_APPPACKAGES_DIR env var to override).\nTried: {appPackagesPath ?? "<none resolved>"}");
                }
                else
                {
                    // Get all package folders and find the latest version
                    var packageFolders = Directory.GetDirectories(appPackagesPath)
                        .Where(d => Path.GetFileName(d).StartsWith("XboxGamingBarPackage_"))
                        .ToList();

                    if (packageFolders.Count == 0)
                    {
                        response.Add("Error", "No package folders found in AppPackages");
                    }
                    else
                    {
                        // Parse versions from folder names (e.g., XboxGamingBarPackage_0.3.98.0_Debug_Test)
                        string latestFolder = null;
                        string latestVersionStr = null;
                        Version latestVersion = null;

                        foreach (var folder in packageFolders)
                        {
                            var folderName = Path.GetFileName(folder);
                            var parts = folderName.Split('_');
                            if (parts.Length >= 2)
                            {
                                var versionStr = parts[1];
                                if (Version.TryParse(versionStr, out var version))
                                {
                                    if (latestVersion == null || version > latestVersion)
                                    {
                                        latestVersion = version;
                                        latestVersionStr = versionStr;
                                        latestFolder = folder;
                                    }
                                }
                            }
                        }

                        if (latestFolder == null)
                        {
                            response.Add("Error", "Could not parse version from folder names");
                        }
                        else
                        {
                            // Find .msixbundle in the folder
                            var msixbundleFiles = Directory.GetFiles(latestFolder, "*.msixbundle", SearchOption.AllDirectories);
                            if (msixbundleFiles.Length == 0)
                            {
                                response.Add("Error", $"No .msixbundle found in:\n{Path.GetFileName(latestFolder)}");
                            }
                            else
                            {
                                var msixbundlePath = msixbundleFiles[0];
                                Logger.Info($"Pipe: Found local update: version={latestVersionStr}, path={msixbundlePath}");

                                response.Add("LatestVersion", latestVersionStr);
                                response.Add("MsixbundlePath", msixbundlePath);
                                response.Add("FolderName", Path.GetFileName(latestFolder));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: Failed to check for local update: {ex.Message}");
                response.Add("Error", $"Failed: {ex.Message}");
            }
            return response;
        }

        // Debug/Development: Install Update (download and install from URL or local path)
        private static global::Windows.Foundation.Collections.ValueSet HandleInstallUpdate(Shared.IPC.PipeMessage request)
        {
            var updatePath = request.Content;
            Logger.Info($"Pipe: InstallUpdate request received: {updatePath}");
            var response = new global::Windows.Foundation.Collections.ValueSet();

            if (string.IsNullOrEmpty(updatePath))
            {
                response.Add("UpdateStatus", "Error: No URL/path provided");
            }
            else
            {
                try
                {
                    string msixbundlePath;

                    // Check if this is a local msixbundle path (debug mode)
                    if (updatePath.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase) && File.Exists(updatePath))
                    {
                        // Direct path to local msixbundle - skip download/extract
                        Logger.Info($"Pipe: [DEBUG] Using local msixbundle: {updatePath}");
                        msixbundlePath = updatePath;
                    }
                    else
                    {
                        // Download and extract from URL
                        var tempFolder = Path.Combine(Path.GetTempPath(), "GoTweaks_Update");
                        var zipPath = Path.Combine(tempFolder, "update.zip");

                        // Clean up and create temp folder
                        if (Directory.Exists(tempFolder))
                            Directory.Delete(tempFolder, true);
                        Directory.CreateDirectory(tempFolder);

                        // Download the zip file. TimedWebClient (5 min — update
                        // bundles are ~100 MB) so a blocked/stalled network errors
                        // out instead of hanging the update forever (issue #91).
                        Logger.Info($"Pipe: Downloading update from {updatePath}...");
                        using (var client = new XboxGamingBarHelper.Core.TimedWebClient(TimeSpan.FromMinutes(5)))
                        {
                            client.Headers.Add("User-Agent", "GoTweaks/1.0");
                            client.DownloadFile(updatePath, zipPath);
                        }
                        Logger.Info($"Pipe: Downloaded to {zipPath}");

                        // Extract the zip
                        var extractFolder = Path.Combine(tempFolder, "extracted");
                        Directory.CreateDirectory(extractFolder);
                        ZipFile.ExtractToDirectory(zipPath, extractFolder);
                        Logger.Info($"Pipe: Extracted to {extractFolder}");

                        // Find the .msixbundle file
                        msixbundlePath = null;
                        foreach (var file in Directory.GetFiles(extractFolder, "*.msixbundle", SearchOption.AllDirectories))
                        {
                            msixbundlePath = file;
                            break;
                        }

                        if (string.IsNullOrEmpty(msixbundlePath))
                        {
                            Logger.Error("Pipe: No .msixbundle file found in the update package");
                            response.Add("UpdateStatus", "Error: No .msixbundle found in update");
                            // Send response and return early
                            if (pipeServer != null && pipeServer.IsConnected)
                            {
                                var errMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                                errMsg.RequestId = request.RequestId;
                                pipeServer.SendMessage(errMsg.ToJson());
                            }
                            return null;
                        }
                    }

                    Logger.Info($"Pipe: Found msixbundle: {msixbundlePath}");

                    // Send response before launching installer
                    response.Add("UpdateStatus", "Installing");
                    if (pipeServer != null && pipeServer.IsConnected)
                    {
                        var successMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                        successMsg.RequestId = request.RequestId;
                        pipeServer.SendMessage(successMsg.ToJson());
                    }

                    // Launch the msixbundle installer
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = msixbundlePath,
                        UseShellExecute = true
                    };

                    Logger.Info("Pipe: Launching msixbundle installer...");
                    var installerProcess = Process.Start(startInfo);

                    // Wait for installer to fully load the package before exiting
                    Logger.Info("Pipe: Waiting for installer to load package...");
                    System.Threading.Thread.Sleep(5000); // 5 seconds for installer to fully open

                    // Exit helper so installer can replace files
                    Logger.Info("Pipe: Exiting helper for update installation...");
                    NotifyWidgetHelperExiting("UpdateInstall");
                    LogManager.Flush(); // Ensure log is written before exit

                    // Release mutex before exiting to ensure clean restart
                    try
                    {
                        singleInstanceMutex?.ReleaseMutex();
                        singleInstanceMutex?.Dispose();
                    }
                    catch { /* Ignore mutex errors during shutdown */ }

                    Environment.Exit(0);
                    return null; // Won't reach this but for clarity
                }
                catch (WebException ex)
                {
                    Logger.Error($"Pipe: Failed to download update: {ex.Message}");
                    response.Add("UpdateStatus", $"Error: Download failed - {ex.Message}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Pipe: Failed to install update: {ex.Message}");
                    response.Add("UpdateStatus", $"Error: {ex.Message}");
                }
            }
            return response;
        }

        // System Restore: Prepare for Uninstall
        private static global::Windows.Foundation.Collections.ValueSet HandlePrepareForUninstall(Shared.IPC.PipeMessage request)
        {
            int functionValue = (int)request.Function;
            Logger.Info("Pipe: PrepareForUninstall request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();

            try
            {
                string result = Services.SystemRestoreService.PrepareForUninstall(legionManager, systemManager, viiperEmulationManager);
                response.Add(nameof(Function), functionValue);
                response.Add("Content", result);
                response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                Logger.Info("Pipe: PrepareForUninstall completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: PrepareForUninstall failed: {ex.Message}");
                response.Add("Content", $"Error: {ex.Message}");
            }
            return response;
        }

        // System Restore: Get status of saved original values
        private static global::Windows.Foundation.Collections.ValueSet HandleSystemRestoreStatus(Shared.IPC.PipeMessage request)
        {
            int functionValue = (int)request.Function;
            var response = new global::Windows.Foundation.Collections.ValueSet();
            string status = Services.SystemRestoreService.GetSavedValuesStatus();
            response.Add(nameof(Function), functionValue);
            response.Add("Content", status);
            response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            Logger.Info($"Pipe: SystemRestoreStatus requested");
            return response;
        }

        // Export All Data (comprehensive backup)
        private static global::Windows.Foundation.Collections.ValueSet HandleExportAllData(Shared.IPC.PipeMessage request)
        {
            int functionValue = (int)request.Function;
            Logger.Info("Pipe: ExportAllData request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();

            try
            {
                // Widget settings may be passed in Content
                string widgetSettings = request.Content;
                string exportPath = ExportAllData(widgetSettings);
                response.Add(nameof(Function), functionValue);
                response.Add("Content", exportPath);
                response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                Logger.Info($"Pipe: ExportAllData completed: {exportPath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: ExportAllData failed: {ex.Message}");
                response.Add("Content", $"Error: {ex.Message}");
            }
            return response;
        }

        // Import All Data (restore from backup)
        private static global::Windows.Foundation.Collections.ValueSet HandleImportAllData(Shared.IPC.PipeMessage request)
        {
            int functionValue = (int)request.Function;
            Logger.Info("Pipe: ImportAllData request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();

            try
            {
                string importPath = request.Content;
                if (string.IsNullOrEmpty(importPath))
                {
                    response.Add("Content", "Error: No import path provided");
                }
                else
                {
                    var (summary, widgetSettings) = ImportAllData(importPath);
                    response.Add(nameof(Function), functionValue);
                    response.Add("Content", summary);
                    if (!string.IsNullOrEmpty(widgetSettings))
                    {
                        response.Add("WidgetSettings", widgetSettings);
                    }
                    response.Add("UpdatedTime", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                    Logger.Info($"Pipe: ImportAllData completed from {importPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: ImportAllData failed: {ex.Message}");
                response.Add("Content", $"Error: {ex.Message}");
            }
            return response;
        }

        // PawnIO Debug: Get CPU Info
        private static global::Windows.Foundation.Collections.ValueSet HandlePawnIOGetCpuInfo(Shared.IPC.PipeMessage request)
        {
            Logger.Info("Pipe: PawnIOGetCpuInfo request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();
            try
            {
                string cpuInfo = performanceManager?.GetPawnIOCpuInfo() ?? "PerformanceManager not initialized";
                response.Add("Content", cpuInfo);
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: PawnIOGetCpuInfo failed: {ex.Message}");
                response.Add("Content", $"Error: {ex.Message}");
            }
            return response;
        }

        // PawnIO Debug: Apply Settings
        private static global::Windows.Foundation.Collections.ValueSet HandlePawnIOApplySettings(Shared.IPC.PipeMessage request)
        {
            Logger.Info("Pipe: PawnIOApplySettings request received");
            var response = new global::Windows.Foundation.Collections.ValueSet();
            try
            {
                int coAll = 0, coGfx = 0, gfxClk = 0, tctlTemp = 0;
                var valueSet = request.ToValueSet();
                if (valueSet.TryGetValue("CoAll", out object coAllObj)) coAll = Convert.ToInt32(coAllObj);
                if (valueSet.TryGetValue("CoGfx", out object coGfxObj)) coGfx = Convert.ToInt32(coGfxObj);
                if (valueSet.TryGetValue("GfxClk", out object gfxClkObj)) gfxClk = Convert.ToInt32(gfxClkObj);
                if (valueSet.TryGetValue("TctlTemp", out object tctlObj)) tctlTemp = Convert.ToInt32(tctlObj);

                Logger.Info($"PawnIO Apply: CoAll={coAll}, CoGfx={coGfx}, GfxClk={gfxClk}, Tctl={tctlTemp}");
                string result = performanceManager?.ApplyPawnIODebugSettings(coAll, coGfx, gfxClk, tctlTemp)
                    ?? "PerformanceManager not initialized";
                response.Add("Content", result);
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe: PawnIOApplySettings failed: {ex.Message}");
                response.Add("Content", $"Error: {ex.Message}");
            }
            return response;
        }

        /// <summary>
        /// Returns true if the Named Pipe to the widget is connected.
        /// </summary>
        public static bool IsPipeConnected => pipeServer != null && pipeServer.IsConnected;

        /// <summary>
        /// Sends a message to the widget via Named Pipe
        /// </summary>
        public static bool SendPipeMessage(Shared.IPC.PipeMessage message)
        {
            if (pipeServer == null || !pipeServer.IsConnected)
            {
                Logger.Debug("Cannot send pipe message - not connected");
                return false;
            }
            return pipeServer.SendMessage(message.ToJson());
        }

        /// <summary>
        /// Pushes an unsolicited GoTweaks self-update payload to the widget.
        /// Widget renders a Quick-tab banner + GoTweaks Update card when
        /// IsUpdateAvailable is true (unless the user has set HideBanner).
        /// </summary>
        public static bool PushGoTweaksUpdate(Services.GoTweaksUpdateResult result)
        {
            try
            {
                if (pipeServer == null || !pipeServer.IsConnected) return false;
                if (result == null) return false;
                var payload = new global::Windows.Foundation.Collections.ValueSet
                {
                    { "GoTweaksUpdate", result.ToJson() },
                };
                var msg = Shared.IPC.PipeMessage.FromValueSet(payload);
                return pipeServer.SendMessage(msg.ToJson());
            }
            catch (Exception ex)
            {
                Logger.Debug($"PushGoTweaksUpdate failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Pushes an unsolicited "DriverUpdatesAvailable" message to the widget
        /// after the startup driver probe completes. The widget uses this to
        /// light up the Quick tab tile without having to ask. If the widget
        /// isn't connected yet (launch race), this is best-effort — we'll also
        /// push again if the widget later sends CheckDriverUpdates.
        /// </summary>
        public static bool PushDriverUpdatesAvailable(int count)
        {
            try
            {
                if (pipeServer == null || !pipeServer.IsConnected) return false;
                var payload = new global::Windows.Foundation.Collections.ValueSet
                {
                    { "DriverUpdatesAvailable", count },
                };
                var msg = Shared.IPC.PipeMessage.FromValueSet(payload);
                return pipeServer.SendMessage(msg.ToJson());
            }
            catch (Exception ex)
            {
                Logger.Debug($"PushDriverUpdatesAvailable failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Handle batch get request via Named Pipe.
        /// Returns all requested property values in a single response.
        /// If managers aren't ready yet, returns a NotReady response so widget can retry.
        /// </summary>
        private static async Task HandleBatchGetRequestViaPipe(Shared.IPC.PipeMessage request)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Check if managers are ready - if not, tell widget to wait and retry
                if (!_managersReady)
                {
                    Logger.Info("BatchGet request received but managers not ready yet - sending NotReady response");
                    var notReadyResponse = new Shared.IPC.PipeMessage
                    {
                        Command = Shared.Enums.Command.BatchGet,
                        RequestId = request.RequestId,
                        Extra = new Dictionary<string, object>
                        {
                            ["NotReady"] = true,
                            ["Message"] = "Helper managers are still initializing, please retry"
                        }
                    };
                    pipeServer?.SendMessage(notReadyResponse.ToJson());
                    return;
                }

                if (!request.Extra.TryGetValue("Functions", out object functionsObj) || !(functionsObj is string functionsJson))
                {
                    Logger.Warn("BatchGet pipe request missing Functions");
                    return;
                }

                var functionIds = System.Text.Json.JsonSerializer.Deserialize<int[]>(functionsJson);
                if (functionIds == null || functionIds.Length == 0)
                {
                    Logger.Warn("BatchGet pipe request has empty Functions array");
                    return;
                }

                // Build batch response with all property values
                var batchData = new Dictionary<string, object>();
                foreach (var funcId in functionIds)
                {
                    var func = (Shared.Enums.Function)funcId;

                    if (properties.TryGetProperty(func, out var property))
                    {
                        try
                        {
                            var value = property.GetValue();

                            // Structs must be serialized to XML to match individual property sync format
                            // Otherwise they serialize as "{}" in JSON which fails XML deserialization on widget
                            // Only serialize custom structs, not built-in value types like DateTime, TimeSpan, etc.
                            if (value != null)
                            {
                                var valueType = value.GetType();
                                if (valueType.IsValueType && !valueType.IsPrimitive && !valueType.IsEnum
                                    && valueType.Namespace != null && valueType.Namespace.StartsWith("Shared"))
                                {
                                    // Special handling for RunningGame/TrackedGame - check if valid before serializing
                                    // These structs have null strings when invalid/empty which causes XML serialization to fail
                                    bool shouldSerialize = true;
                                    if (value is Shared.Data.RunningGame rg)
                                    {
                                        // Must check ProcessId, GameId.Name AND GameId.Path - XML serializer fails on null strings
                                        shouldSerialize = rg.IsValid() && rg.GameId.IsValid() && !string.IsNullOrEmpty(rg.GameId.Path);
                                        if (!shouldSerialize)
                                        {
                                            Logger.Debug($"RunningGame not valid for XML: ProcessId={rg.ProcessId}, Name={rg.GameId.Name ?? "null"}, Path={rg.GameId.Path ?? "null"}");
                                        }
                                    }
                                    else if (value is Shared.Data.TrackedGame tg)
                                    {
                                        shouldSerialize = !string.IsNullOrEmpty(tg.DisplayName);
                                    }

                                    if (shouldSerialize)
                                    {
                                        value = Shared.Utilities.XmlHelper.ToXMLStringRuntime(value, true);
                                    }
                                    else
                                    {
                                        value = ""; // Return empty string for invalid/empty structs
                                    }
                                }
                            }

                            var propData = new Dictionary<string, object>
                            {
                                { "Content", value },
                                { "UpdatedTime", property.UpdatedTime }
                            };
                            batchData[funcId.ToString()] = propData;
                        }
                        catch (Exception propEx)
                        {
                            var innerMsg = propEx.InnerException?.Message ?? "no inner";
                            Logger.Warn($"BatchGet: Failed to serialize property {func}: {propEx.Message} (Inner: {innerMsg})");
                        }
                    }
                }

                // Send response via pipe with the same RequestId
                var response = new Shared.IPC.PipeMessage
                {
                    RequestId = request.RequestId,
                    Command = Shared.Enums.Command.Response,
                    Function = Shared.Enums.Function.None
                };
                response.Extra["BatchData"] = System.Text.Json.JsonSerializer.Serialize(batchData);

                if (pipeServer != null && pipeServer.IsConnected)
                {
                    pipeServer.SendMessage(response.ToJson());
                }

                timer.Stop();
                Logger.Info($"[TIMING] BatchGet via pipe {functionIds.Length} properties: {timer.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Logger.Error($"BatchGet via pipe failed: {ex.Message}");
            }
        }


        /// <summary>
        /// Sends a simple acknowledgment response to the widget via Named Pipe.
        /// Used for fire-and-forget messages that still need a response to avoid timeout.
        /// </summary>
        /// <summary>
        /// Tell the widget the helper is about to exit ON PURPOSE (ExitHelper / Upgrade / Update),
        /// so its pipe-disconnect handler skips the automatic relaunch for this case. Without this
        /// the widget relaunched the dying helper mid-shutdown, racing it (two helpers contending
        /// for the single pipe — issue #81). Best-effort: a dropped message just falls back to the
        /// old behavior.
        /// </summary>
        private static void NotifyWidgetHelperExiting(string reason)
        {
            try
            {
                if (pipeServer != null && pipeServer.IsConnected)
                {
                    var msg = new global::Windows.Foundation.Collections.ValueSet();
                    msg.Add("HelperExiting", reason ?? "intentional");
                    var pm = Shared.IPC.PipeMessage.FromValueSet(msg);
                    pipeServer.SendMessage(pm.ToJson());
                    Logger.Info($"Notified widget of intentional helper exit ({reason}).");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"NotifyWidgetHelperExiting failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Tell the widget the PC just woke from sleep/hibernate. After a long sleep Game Bar can
        /// drop its connection to an already-open widget window: the window still draws and still
        /// receives helper pushes, but never hears Game Bar events or input again (the user sees a
        /// widget that opens but ignores every tap). The widget reacts by asking Game Bar to close
        /// that stale window, so the next open is a clean launch. The UTC timestamp lets the widget
        /// ignore a resume signal that sat in the pipe while its process was suspended and only got
        /// delivered later, after it had already been relaunched fresh. Best-effort.
        /// </summary>
        private static void NotifyWidgetSystemResumed()
        {
            try
            {
                if (pipeServer != null && pipeServer.IsConnected)
                {
                    var msg = new global::Windows.Foundation.Collections.ValueSet();
                    msg.Add("SystemResumedUtcTicks", DateTime.UtcNow.Ticks);
                    var pm = Shared.IPC.PipeMessage.FromValueSet(msg);
                    pipeServer.SendMessage(pm.ToJson());
                    Logger.Info("Notified widget that the system resumed from sleep.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"NotifyWidgetSystemResumed failed: {ex.Message}");
            }
        }

        private struct AxisStats
        {
            public double TrimmedMean;
            public double TrimmedStd;
            public double RawMean;
            public double RawStd;
            public double Min;
            public double Max;
            public int Dropped;
        }

        // MAD-rejection threshold. 6 * 1.4826 * MAD is roughly 6 sigma for Gaussian noise,
        // which keeps essentially all clean samples and prunes the rare BMI260 single-sample
        // step (~+/-15 deg/s at rest) we caught during the 2026-05-30 capture audit.
        private const double GyroBiasMadK = 6.0;

        private static AxisStats ComputeRobustAxisStats(System.Collections.Generic.List<double> samples)
        {
            var s = new AxisStats();
            int n = samples.Count;
            if (n == 0) return s;
            double sum = 0, sumSq = 0, min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double v = samples[i];
                sum += v; sumSq += v * v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            s.RawMean = sum / n;
            s.RawStd = Math.Sqrt(Math.Max(0.0, sumSq / n - s.RawMean * s.RawMean));
            s.Min = min;
            s.Max = max;

            var sorted = new double[n];
            samples.CopyTo(sorted);
            Array.Sort(sorted);
            double median = (n % 2 == 0)
                ? 0.5 * (sorted[n / 2 - 1] + sorted[n / 2])
                : sorted[n / 2];
            var dev = new double[n];
            for (int i = 0; i < n; i++) dev[i] = Math.Abs(sorted[i] - median);
            Array.Sort(dev);
            double mad = (n % 2 == 0)
                ? 0.5 * (dev[n / 2 - 1] + dev[n / 2])
                : dev[n / 2];
            // Floor MAD so an essentially-flat run (mad ~= 0) doesn't reject all but the median
            // value as outliers. 0.05 deg/s is ~one HQ LSB scaled, well below sensor noise floor.
            double sigma = Math.Max(0.05, 1.4826 * mad);
            double lo = median - GyroBiasMadK * sigma;
            double hi = median + GyroBiasMadK * sigma;

            double tSum = 0, tSumSq = 0;
            int tN = 0;
            for (int i = 0; i < n; i++)
            {
                double v = samples[i];
                if (v < lo || v > hi) continue;
                tSum += v; tSumSq += v * v; tN++;
            }
            s.Dropped = n - tN;
            if (tN > 0)
            {
                s.TrimmedMean = tSum / tN;
                s.TrimmedStd = Math.Sqrt(Math.Max(0.0, tSumSq / tN - s.TrimmedMean * s.TrimmedMean));
            }
            else
            {
                s.TrimmedMean = s.RawMean;
                s.TrimmedStd = s.RawStd;
            }
            return s;
        }

        /// <summary>
        /// Push the current software gyro bias offset to the widget for display. JSON content
        /// matches the GyroBiasOffset Function comment:
        ///   { "x":<deg/s>, "y":<deg/s>, "z":<deg/s>, "at":<UTC ticks>, "valid":<bool> }
        /// Sent after every CalibrateGyroBias capture/reset, and on widget connect (so the
        /// status text reflects the persisted state immediately on reopen).
        /// </summary>
        // Last SetupWarnings JSON pushed to the widget. Re-pushed only when the
        // evaluation changes so the banner doesn't flicker on every timer tick.
        private static string lastSetupWarningsJson = null;

        /// <summary>
        /// Evaluates setup/environment health (missing PawnIO) and pushes the
        /// result to the widget when it changed — or unconditionally when
        /// <paramref name="force"/> (widget just connected and has no prior state).
        /// </summary>
        internal static void SendSetupWarningsToWidget(bool force = false)
        {
            try
            {
                if (pipeServer == null || !pipeServer.IsConnected) return;

                bool isLegion = legionManager?.LegionGoDetected?.Value ?? false;
                bool controllerFeatures = (legionManager?.DetectedDevice?.SupportsControllerRemap ?? false)
                                       || (legionManager?.DetectedDevice?.SupportsGyro ?? false);
                bool pawnIO = performanceManager?.IsPawnIOInstalled ?? true; // assume fine when unknown
                // usbip is needed when Controller Emulation is actually turned on OR a Legion
                // button maps to Xbox Guide (VIIPER's guide-only pad serves that route on every
                // backend since the ViGEm retirement). NOTE: EmulationBackend is "which backend
                // is selected," not "is emulation on" — since the ViGEm retirement it's
                // hard-pinned true (VIIPER) for every install regardless of whether the user has
                // ever enabled the feature, so it must not be used as the on/off signal here (it
                // used to be, which nagged every user with the "usbip-win2 missing" banner even
                // with Controller Emulation completely untouched).
                bool usbipNeeded = (controllerEmulationManager?.ControllerEmulationEnabled?.Value ?? false)
                                || (legionButtonMonitor?.HasGuideActionConfigured ?? false);
                bool usbip = settingsManager?.UsbipInstalled?.Value ?? true; // assume fine when unknown

                string json = Services.SetupHealthService.EvaluateJson(isLegion, controllerFeatures, pawnIO, usbipNeeded, usbip);
                if (!force && string.Equals(json, lastSetupWarningsJson, StringComparison.Ordinal)) return;
                lastSetupWarningsJson = json;

                var msg = new global::Windows.Foundation.Collections.ValueSet();
                msg.Add(nameof(Shared.Enums.Function), (int)Shared.Enums.Function.SetupWarnings);
                msg.Add("Content", json);
                var pm = Shared.IPC.PipeMessage.FromValueSet(msg);
                pipeServer.SendMessage(pm.ToJson());
                if (json != "[]")
                {
                    Logger.Info($"SetupWarnings pushed to widget: {json}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"SendSetupWarningsToWidget failed: {ex.Message}");
            }
        }

        private static void SendGyroBiasOffsetToWidget()
        {
            try
            {
                if (pipeServer == null || !pipeServer.IsConnected) return;
                bool valid = XboxGamingBarHelper.Labs.LegionButtonMonitor.TryGetGyroBias(
                    out float bx, out float by, out float bz, out long atUtc);
                string json =
                    "{\"x\":" + bx.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"y\":" + by.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"z\":" + bz.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"at\":" + atUtc.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"valid\":" + (valid ? "true" : "false")
                    + "}";
                var msg = new global::Windows.Foundation.Collections.ValueSet();
                msg.Add(nameof(Shared.Enums.Function), (int)Shared.Enums.Function.GyroBiasOffset);
                msg.Add("Content", json);
                var pm = Shared.IPC.PipeMessage.FromValueSet(msg);
                pipeServer.SendMessage(pm.ToJson());
            }
            catch (Exception ex)
            {
                Logger.Warn($"SendGyroBiasOffsetToWidget failed: {ex.Message}");
            }
        }

        private static void SendPipeAck(int requestId, bool success = true)
        {
            try
            {
                if (pipeServer != null && pipeServer.IsConnected && requestId > 0)
                {
                    var response = new global::Windows.Foundation.Collections.ValueSet();
                    response.Add("Success", success);
                    var responseMsg = Shared.IPC.PipeMessage.FromValueSet(response);
                    responseMsg.RequestId = requestId;
                    pipeServer.SendMessage(responseMsg.ToJson());
                    Logger.Debug($"Sent pipe ack for RequestId={requestId}, Success={success}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to send pipe ack: {ex.Message}");
            }
        }

        /// <summary>
        /// Locates the AppPackages probe directory for the debug-panel "check local update"
        /// action. Strategy in order:
        ///   1. GOTWEAKS_APPPACKAGES_DIR env var — explicit override for any contributor.
        ///   2. Walk up from the helper exe — works only when the helper runs out of the
        ///      build output under the source tree (rare; the deployed helper lives in
        ///      LocalCache and is too far from the source for this to hit).
        ///   3. Probe a list of common developer checkout layouts under %USERPROFILE%.
        ///      This is the path that works for the deployed helper on a dev machine.
        /// On a normal user's installed system all three fail and we return null — the
        /// caller surfaces a clear error and the rest of the system is unaffected (this
        /// whole code path is debug-only UX behind the Debug panel).
        /// </summary>
        private static string ResolveAppPackagesProbeDir()
        {
            try
            {
                string envOverride = Environment.GetEnvironmentVariable("GOTWEAKS_APPPACKAGES_DIR");
                if (!string.IsNullOrEmpty(envOverride))
                {
                    return envOverride;
                }

                // Walk up from the helper exe — covers the rare case where the helper
                // is run directly from the source-tree build output without deploy.
                string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    var dir = new DirectoryInfo(exeDir);
                    for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                    {
                        string candidate = Path.Combine(dir.FullName, "XboxGamingBarPackage", "AppPackages");
                        if (Directory.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }

                // Probe common dev-checkout paths under the logged-in user's profile.
                // The scheduled-task helper runs elevated as the same user, so
                // Environment.SpecialFolder.UserProfile resolves to the developer's profile.
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile))
                {
                    string[] roots =
                    {
                        Path.Combine(userProfile, "OneDrive", "Desktop", "Diego", "projects", "XboxGamingBar"),
                        Path.Combine(userProfile, "Desktop", "Diego", "projects", "XboxGamingBar"),
                        Path.Combine(userProfile, "source", "repos", "XboxGamingBar"),
                        Path.Combine(userProfile, "projects", "XboxGamingBar"),
                        Path.Combine(userProfile, "repos", "XboxGamingBar"),
                    };
                    foreach (var root in roots)
                    {
                        string candidate = Path.Combine(root, "XboxGamingBarPackage", "AppPackages");
                        if (Directory.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"ResolveAppPackagesProbeDir: {ex.Message}");
            }
            return null;
        }

    }
}
