using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using Microsoft.UI.Xaml.Controls;
using NLog;
using Shared.Data;
using Shared.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.System.Power;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.UI;
using XboxGamingBar.Data;
using XboxGamingBar.Event;
using XboxGamingBar.IPC;
using XboxGamingBar.QuickSettings;
using Shared.Enums;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        private void DebugExpandButton_Click(object sender, RoutedEventArgs e)
        {
            isDebugExpanded = !isDebugExpanded;

            if (DebugContent != null)
            {
                DebugContent.Visibility = isDebugExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (DebugExpandIcon != null)
            {
                DebugExpandIcon.Glyph = isDebugExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private bool isAboutExpanded = false;

        private void AboutExpandButton_Click(object sender, RoutedEventArgs e)
        {
            isAboutExpanded = !isAboutExpanded;

            if (AboutContent != null)
            {
                AboutContent.Visibility = isAboutExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (AboutExpandIcon != null)
            {
                AboutExpandIcon.Glyph = isAboutExpanded ? "\uE70E" : "\uE70D";
            }

            // Update version text dynamically. Friendly release number (bumped by hand once
            // per release, matches the GitHub tag / releases.md) is the headline; the raw
            // auto-incrementing MSIX build number stays visible in parentheses for support/
            // debugging (pinpoints the exact build between two releases).
            if (isAboutExpanded && AboutVersionText != null)
            {
                try
                {
                    var version = Windows.ApplicationModel.Package.Current.Id.Version;
                    string build = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                    AboutVersionText.Text = $"{Shared.Constants.UpdateConstants.FriendlyVersion} (build {build})";
                }
                catch
                {
                    // Keep default version text
                }
            }
        }

        private async void RestartHelperButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RestartHelperButton.IsEnabled = false;
                RestartHelperButton.Content = "Restarting...";

                // Send exit command to helper via IPC
                if (App.IsConnected)
                {
                    var message = new Windows.Foundation.Collections.ValueSet();
                    message.Add("ExitHelper", true);

                    Logger.Info("Sending ExitHelper command to helper");
                    var response = await App.SendMessageAsync(message);

                    if (response != null)
                    {
                        Logger.Info("Helper acknowledged exit command");
                    }

                    // Disconnect the pipe so we can detect when helper is truly gone
                    App.PipeClient?.Disconnect();
                }

                // Wait for helper to exit and release mutex
                // Helper waits 3 seconds before force-killing, so we wait 4 seconds to be safe
                Logger.Info("Waiting for helper to exit...");
                await Task.Delay(4000);

                // Verify helper has disconnected
                if (App.IsConnected)
                {
                    Logger.Warn("Helper still connected after exit command - forcing disconnect");
                    App.PipeClient?.Disconnect();
                    await Task.Delay(1000);
                }

                // Launch new helper instance through the guarded path so it shares the
                // isLaunchingHelper / alive-check guards with the pipe-disconnect relaunch and
                // can't spawn a second helper racing this one (issue #81). forceLaunch: we just
                // killed the helper and waited, so the alive-check should pass — force ensures we
                // launch even if a stale heartbeat lingers.
                Logger.Info("Launching new helper instance (guarded)");
                await LaunchHelperWithGuardsAsync("Restart Helper button", forceLaunch: true);

                await Task.Delay(1500);
                RestartHelperButton.Content = "Restart Helper";
                RestartHelperButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to restart helper: {ex.Message}");
                RestartHelperButton.Content = "Restart Helper";
                RestartHelperButton.IsEnabled = true;
            }
        }

        private async void ExportLogsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ExportLogsButton.IsEnabled = false;
                ExportLogsButton.Content = "Exporting...";

                // Send export logs command to helper via IPC
                if (App.IsConnected)
                {
                    var message = new Windows.Foundation.Collections.ValueSet();
                    message.Add("ExportLogs", true);

                    Logger.Info("Sending ExportLogs command to helper");
                    var response = await App.SendMessageAsync(message);

                    if (response != null)
                    {
                        bool success = false;
                        if (response.TryGetValue("Success", out object successObj) && successObj is bool successVal)
                            success = successVal;

                        if (success)
                        {
                            var path = response.TryGetValue("Path", out object pathObj) ? pathObj as string : "Desktop";
                            Logger.Info($"Logs exported successfully to: {path}");
                            ExportLogsButton.Content = "Exported!";
                        }
                        else
                        {
                            var error = response.TryGetValue("Error", out object errorObj) ? errorObj as string : "Unknown error";
                            Logger.Error($"Export logs failed: {error}");
                            ExportLogsButton.Content = "Export Failed";
                        }
                    }
                    else
                    {
                        Logger.Error("Export logs request failed - no response");
                        ExportLogsButton.Content = "Export Failed";
                    }
                }
                else
                {
                    Logger.Error("Cannot export logs - no connection to helper");
                    ExportLogsButton.Content = "No Helper";
                }

                await Task.Delay(2000);
                ExportLogsButton.Content = "Export Logs";
                ExportLogsButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to export logs: {ex.Message}");
                ExportLogsButton.Content = "Export Failed";
                await Task.Delay(2000);
                ExportLogsButton.Content = "Export Logs";
                ExportLogsButton.IsEnabled = true;
            }
        }

        private async void KillGoTweaksButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("Kill GoTweaks requested by user");

                // Send exit command to helper using all available methods
                bool exitSent = false;

                // Try via Named Pipe
                if (App.PipeClient?.IsConnected == true)
                {
                    var message = new Windows.Foundation.Collections.ValueSet();
                    message.Add("ExitHelper", true);
                    Logger.Info("Sending ExitHelper via Named Pipe");
                    await App.SendMessageAsync(message);
                    exitSent = true;
                }
                // Not connected - try temporary pipe connection
                else
                {
                    Logger.Info("Not connected - attempting temporary pipe connection for ExitHelper");
                    try
                    {
                        using (var tempPipe = new System.IO.Pipes.NamedPipeClientStream(".", "GoTweaksHelper", System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
                        {
                            var connectTask = tempPipe.ConnectAsync(2000);
                            if (await Task.WhenAny(connectTask, Task.Delay(2500)) == connectTask)
                            {
                                using (var writer = new System.IO.StreamWriter(tempPipe, System.Text.Encoding.UTF8, 4096, leaveOpen: true))
                                {
                                    writer.AutoFlush = true;
                                    await writer.WriteLineAsync("{\"RequestId\":0,\"ExitHelper\":true}");
                                }
                                Logger.Info("Sent ExitHelper via temporary pipe connection");
                                exitSent = true;
                            }
                        }
                    }
                    catch (Exception pipeEx)
                    {
                        Logger.Warn($"Temporary pipe connection failed: {pipeEx.Message}");
                    }
                }

                if (exitSent)
                {
                    // Give helper time to exit (helper waits 3 seconds before force-killing)
                    Logger.Info("Waiting for helper to exit...");
                    await Task.Delay(4000);
                }
                else
                {
                    Logger.Warn("Could not send ExitHelper - helper may still be running");
                }

                // Exit the widget application
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to kill GoTweaks: {ex.Message}");
                // Still try to exit even if helper communication failed
                Application.Current.Exit();
            }
        }

        /// <summary>
        /// Compares two version strings (e.g., "v0.3.902" vs "v0.3.1001.0").
        /// Returns true if latestVersion is newer than currentVersion.
        /// </summary>
        private bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            // Strip 'v' prefix if present
            var latest = latestVersion.TrimStart('v', 'V');
            var current = currentVersion.TrimStart('v', 'V');

            // Split into parts
            var latestParts = latest.Split('.');
            var currentParts = current.Split('.');

            // Compare each part numerically
            int maxLength = Math.Max(latestParts.Length, currentParts.Length);
            for (int i = 0; i < maxLength; i++)
            {
                int latestNum = 0;
                int currentNum = 0;

                if (i < latestParts.Length && int.TryParse(latestParts[i], out int lp))
                    latestNum = lp;
                if (i < currentParts.Length && int.TryParse(currentParts[i], out int cp))
                    currentNum = cp;

                if (latestNum > currentNum)
                    return true;
                if (latestNum < currentNum)
                    return false;
            }

            return false; // Versions are equal
        }

        /// <summary>
        /// Pulls the dotted build version out of a release asset filename
        /// ("GoTweaks_0.3.2491.0.zip" → "0.3.2491.0"). Returns "" when the name
        /// carries no parsable version. The comparison against the installed MSIX
        /// version uses THIS, not the release tag — so the tag (e.g. "1.0") is a
        /// free-form cosmetic label and never drives the numeric check.
        /// </summary>
        private static string ExtractVersionFromFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d+\.\d+\.\d+(?:\.\d+)?)");
            return m.Success ? m.Groups[1].Value : "";
        }

        private async void CheckForUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CheckForUpdateButton.IsEnabled = false;
                CheckForUpdateButton.Content = "Checking...";
                UpdateStatusText.Visibility = Visibility.Visible;
                UpdateStatusText.Text = "Checking for updates...";
                UpdateButton.Visibility = Visibility.Collapsed;
                _pendingUpdateZipUrl = null;
                _pendingUpdateVersion = null;
                _pendingUpdateIsRemote = false;

                var pv = Package.Current.Id.Version;
                // Raw MSIX build version — used for the actual comparison/logging, never shown
                // to the user directly (see FriendlyVersion doc comment for why).
                var cv = $"v{pv.Major}.{pv.Minor}.{pv.Build}.{pv.Revision}";
                var friendlyCurrent = Shared.Constants.UpdateConstants.FriendlyVersion;

                // Updates are disabled while no release repo is configured (see
                // Shared.Constants.UpdateConstants).
                if (!Shared.Constants.UpdateConstants.UpdatesEnabled)
                {
                    UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 160, 160, 160));
                    UpdateStatusText.Text = $"You're up to date! ({friendlyCurrent})";
                    CheckForUpdateButton.Content = "Check for Update";
                    CheckForUpdateButton.IsEnabled = true;
                    return;
                }

                // Consolidated update path: the helper's GoTweaksUpdateService is the SINGLE
                // brain — it checks GitHub, compares by the .msixbundle asset-filename build
                // version (not the cosmetic release tag), and installs silently via
                // Add-AppxPackage. This button now just asks it (ForceRefresh) and renders the
                // result, so the manual check and the Quick-tab startup banner share one path.
                if (!App.IsConnected)
                {
                    UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                    UpdateStatusText.Text = "Helper not connected — can't check for updates.";
                    CheckForUpdateButton.Content = "Check for Update";
                    CheckForUpdateButton.IsEnabled = true;
                    return;
                }

                var req = new Windows.Foundation.Collections.ValueSet
                {
                    { "CheckGoTweaksUpdate", true },
                    { "ForceRefresh", true },
                };
                var resp = await App.SendMessageAsync(req);

                string payload = null;
                if (resp != null && resp.TryGetValue("GoTweaksUpdate", out object p))
                    payload = p?.ToString();

                if (string.IsNullOrEmpty(payload) || !Windows.Data.Json.JsonObject.TryParse(payload, out var root))
                {
                    UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                    UpdateStatusText.Text = "Update check failed (no response from helper).";
                }
                else
                {
                    bool isUpdate = root.GetNamedBoolean("isUpdateAvailable", false);
                    bool checkFailed = root.GetNamedBoolean("checkFailed", false);
                    string latest = root.GetNamedString("latestVersion", "");
                    string tag = root.GetNamedString("latestTag", "");
                    string url = root.GetNamedString("downloadUrl", "");
                    string label = string.IsNullOrWhiteSpace(tag) ? latest : tag;

                    Logger.Info($"Update check (via helper): current={cv}, update={isUpdate}, checkFailed={checkFailed}, label={label}, url={(string.IsNullOrEmpty(url) ? "(none)" : "ok")}");

                    if (isUpdate && !string.IsNullOrWhiteSpace(url))
                    {
                        UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)); // #6CCB5F - matches Quick Settings tile green
                        UpdateStatusText.Text = $"New version available: {label}\nCurrent: {friendlyCurrent}";
                        _goTweaksDownloadUrl = url;          // .msixbundle URL — unified install
                        _pendingUpdateVersion = label;
                        _pendingUpdateIsRemote = true;
                        UpdateButton.Visibility = Visibility.Visible;
                    }
                    else if (checkFailed)
                    {
                        // The check itself didn't complete (network error, GitHub API rate-limit,
                        // unparsable response) - don't claim "up to date" when we don't actually
                        // know that.
                        UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                        UpdateStatusText.Text = "Update check failed (GitHub unreachable or rate-limited) — try again later.";
                    }
                    else
                    {
                        UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 160, 160, 160));
                        UpdateStatusText.Text = $"You're up to date! ({friendlyCurrent})";
                    }
                }

                CheckForUpdateButton.Content = "Check for Update";
                CheckForUpdateButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to check for update: {ex.Message}");
                UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                UpdateStatusText.Text = $"Failed to check for updates: {ex.Message}";
                CheckForUpdateButton.Content = "Check for Update";
                CheckForUpdateButton.IsEnabled = true;
            }
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!App.IsConnected)
            {
                UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                UpdateStatusText.Text = "Helper not connected";
                return;
            }

            try
            {
                UpdateButton.IsEnabled = false;

                // REMOTE GitHub update → unified helper install (download the .msixbundle +
                // silent Add-AppxPackage). Same path the Quick-tab banner uses.
                if (_pendingUpdateIsRemote)
                {
                    if (string.IsNullOrEmpty(_goTweaksDownloadUrl))
                    {
                        Logger.Warn("Update clicked but no pending download URL");
                        UpdateButton.IsEnabled = true;
                        return;
                    }

                    UpdateButton.Content = "Installing...";
                    UpdateStatusText.Text = $"Installing {_pendingUpdateVersion}...";

                    var request = new Windows.Foundation.Collections.ValueSet
                    {
                        { "InstallGoTweaksUpdate", _goTweaksDownloadUrl },
                    };
                    var response = await App.SendMessageAsync(request);

                    string message = "Installing update — the widget will reload when finished.";
                    if (response != null
                        && response.TryGetValue("GoTweaksUpdateInstallResult", out object payloadObj)
                        && payloadObj is string payload
                        && Windows.Data.Json.JsonObject.TryParse(payload, out var root))
                    {
                        string msg = root.GetNamedString("message", "");
                        if (!string.IsNullOrWhiteSpace(msg)) message = msg;
                        bool ok = root.GetNamedBoolean("success", true);
                        if (!ok)
                        {
                            UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                            UpdateStatusText.Text = message;
                            UpdateButton.Content = "Update";
                            UpdateButton.IsEnabled = true;
                            return;
                        }
                    }
                    UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)); // #6CCB5F - matches Quick Settings tile green
                    UpdateStatusText.Text = message;
                    return;
                }

                // LOCAL dev build (AppPackages probe) → extract/shell-open path via InstallUpdate.
                if (string.IsNullOrEmpty(_pendingUpdateZipUrl))
                {
                    Logger.Warn("Update clicked but no pending update URL");
                    UpdateButton.IsEnabled = true;
                    return;
                }

                UpdateButton.Content = "Downloading...";
                UpdateStatusText.Text = $"Downloading {_pendingUpdateVersion}...";

                var message2 = new Windows.Foundation.Collections.ValueSet();
                message2.Add("Command", (int)Shared.Enums.Command.Set);
                message2.Add("Function", (int)Shared.Enums.Function.InstallUpdate);
                message2.Add("Content", _pendingUpdateZipUrl);
                var result = await App.SendMessageAsync(message2);

                if (result != null && result.TryGetValue("UpdateStatus", out object status))
                {
                    var statusStr = status?.ToString() ?? "";
                    if (statusStr == "Installing")
                    {
                        UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)); // #6CCB5F - matches Quick Settings tile green
                        UpdateStatusText.Text = "Installing update... Please follow the installer prompts.";
                        UpdateButton.Content = "Installing...";
                    }
                    else if (statusStr.StartsWith("Error"))
                    {
                        UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                        UpdateStatusText.Text = statusStr;
                        UpdateButton.Content = "Update";
                        UpdateButton.IsEnabled = true;
                    }
                }
                else
                {
                    UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                    UpdateStatusText.Text = "Failed to communicate with helper";
                    UpdateButton.Content = "Update";
                    UpdateButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start update: {ex.Message}");
                UpdateStatusText.Foreground = new SolidColorBrush(Windows.UI.Colors.Orange);
                UpdateStatusText.Text = $"Update failed: {ex.Message}";
                UpdateButton.Content = "Update";
                UpdateButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Automatically checks for updates on startup if the setting is enabled.
        /// Shows a banner if an update is available.
        /// </summary>
        private async Task CheckForUpdatesOnStartupAsync()
        {
            try
            {
                // Single "check for updates automatically" preference (also gates the
                // helper's Quick-tab banner probe; see GoTweaksUpdateOnStartCheckbox).
                bool autoCheckEnabled = GoTweaksCheckOnStart;

                if (!autoCheckEnabled)
                {
                    Logger.Info("Auto-update check is disabled, skipping startup check");
                    return;
                }

                Logger.Info("Checking for updates on startup...");

                // Small delay to let the UI settle first
                await Task.Delay(2000);

                var packageVersion = Package.Current.Id.Version;
                var currentVersion = $"v{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}";

                // REMOTE startup update notification is now owned entirely by the helper's
                // GoTweaksUpdateService → Quick-tab banner (HandleGoTweaksUpdatePush). This
                // startup path only probes the LOCAL AppPackages folder for the dev
                // build → install iteration loop, so there's a single remote banner instead
                // of the widget and helper both hitting GitHub and both raising a banner.

                // Probe the helper's local AppPackages folder for a newer debug build so the
                // developer iteration loop (build → install) surfaces an update banner without
                // having to click "Check for Update (Debug)". Silently skipped if the helper
                // isn't connected yet.
                string localVersionStr = null;
                string localMsixPath = null;
                string localFolderName = null;
                try
                {
                    if (App.IsConnected)
                    {
                        var localMsg = new Windows.Foundation.Collections.ValueSet
                        {
                            { "Command", (int)Shared.Enums.Command.Get },
                            { "Function", (int)Shared.Enums.Function.CheckLocalUpdate },
                        };
                        var localResult = await App.SendMessageAsync(localMsg);
                        if (localResult != null
                            && !localResult.ContainsKey("Error")
                            && localResult.TryGetValue("LatestVersion", out object lvObj)
                            && localResult.TryGetValue("MsixbundlePath", out object lpObj))
                        {
                            localVersionStr = lvObj?.ToString();
                            localMsixPath = lpObj?.ToString();
                            localFolderName = localResult.TryGetValue("FolderName", out object lfObj) ? lfObj?.ToString() : "";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Startup update check: local debug probe failed: {ex.Message}");
                }

                Logger.Info($"Startup update check: current={currentVersion}, local={(localVersionStr != null ? "v" + localVersionStr : "(n/a)")} (remote handled by helper Quick-tab banner)");

                // Only the LOCAL dev build can raise the System-tab banner here — remote updates
                // are surfaced by the helper's Quick-tab banner instead (single remote path).
                bool localIsNewer = !string.IsNullOrEmpty(localVersionStr)
                    && Version.TryParse(localVersionStr, out var localParsed)
                    && localParsed > new Version(packageVersion.Major, packageVersion.Minor, packageVersion.Build, packageVersion.Revision);

                if (localIsNewer)
                {
                    var localBannerVersion = $"v{localVersionStr} [Debug]";
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        _pendingUpdateZipUrl = localMsixPath; // local .msixbundle
                        _pendingUpdateVersion = localBannerVersion;
                        _pendingUpdateIsRemote = false;       // local install path (Function.InstallUpdate)
                        ShowUpdateBanner(localBannerVersion);
                    });
                    Logger.Info($"Update available (local debug): {localBannerVersion}, folder={localFolderName}, path={localMsixPath}");
                }
                else
                {
                    Logger.Info("No update available");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to check for updates on startup: {ex.Message}");
                // Silently fail - don't show error to user for automatic check
            }
        }

        /// <summary>
        /// Shows the update available banner with the new version.
        /// </summary>
        private void ShowUpdateBanner(string newVersion)
        {
            if (UpdateAvailableBanner != null && UpdateAvailableText != null)
            {
                UpdateAvailableText.Text = $"Update Available: {newVersion}";
                UpdateAvailableBanner.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Hides the update available banner.
        /// </summary>
        private void HideUpdateBanner()
        {
            if (UpdateAvailableBanner != null)
            {
                UpdateAvailableBanner.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Handles the Update button click on the update banner.
        /// </summary>
        private async void UpdateBannerButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_pendingUpdateZipUrl))
            {
                Logger.Warn("Update banner clicked but no pending update URL");
                return;
            }

            try
            {
                UpdateBannerButton.IsEnabled = false;
                UpdateBannerButton.Content = "Updating...";

                if (App.IsConnected)
                {
                    var message = new Windows.Foundation.Collections.ValueSet();
                    message.Add("Command", (int)Shared.Enums.Command.Set);
                    message.Add("Function", (int)Shared.Enums.Function.InstallUpdate);
                    message.Add("Content", _pendingUpdateZipUrl);
                    var result = await App.SendMessageAsync(message);

                    if (result != null && result.TryGetValue("UpdateStatus", out object status))
                    {
                        var statusStr = status?.ToString() ?? "";
                        if (statusStr == "Installing")
                        {
                            UpdateBannerButton.Content = "Installing...";
                            Logger.Info("Update installation started from banner");
                        }
                        else if (statusStr.StartsWith("Error"))
                        {
                            Logger.Error($"Update failed: {statusStr}");
                            UpdateBannerButton.Content = "Failed";
                            await Task.Delay(2000);
                            UpdateBannerButton.Content = "Update";
                            UpdateBannerButton.IsEnabled = true;
                        }
                    }
                    else
                    {
                        UpdateBannerButton.Content = "Failed";
                        await Task.Delay(2000);
                        UpdateBannerButton.Content = "Update";
                        UpdateBannerButton.IsEnabled = true;
                    }
                }
                else
                {
                    Logger.Warn("Helper not connected for update");
                    UpdateBannerButton.Content = "No Helper";
                    await Task.Delay(2000);
                    UpdateBannerButton.Content = "Update";
                    UpdateBannerButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start update from banner: {ex.Message}");
                UpdateBannerButton.Content = "Update";
                UpdateBannerButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Handles the dismiss button click on the update banner.
        /// </summary>
        private void DismissUpdateBannerButton_Click(object sender, RoutedEventArgs e)
        {
            HideUpdateBanner();
        }

        private async void ExportAllDataButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ExportAllDataButton.IsEnabled = false;
                ExportAllDataButton.Content = "Exporting...";

                if (!App.IsConnected)
                {
                    ExportAllDataButton.Content = "Helper not connected";
                    await Task.Delay(2000);
                    ExportAllDataButton.Content = "Export All Data";
                    ExportAllDataButton.IsEnabled = true;
                    return;
                }

                // Gather widget LocalSettings to include in export
                string widgetSettingsJson = GatherWidgetSettingsForExport();

                // Send request to helper to export all data
                var message = new Windows.Foundation.Collections.ValueSet();
                message.Add("Command", (int)Shared.Enums.Command.Set);
                message.Add("Function", (int)Shared.Enums.Function.ExportAllData);
                message.Add("Content", widgetSettingsJson);
                var result = await App.SendMessageAsync(message);

                if (result != null && result.TryGetValue("Content", out object contentObj))
                {
                    string resultText = contentObj?.ToString() ?? "";
                    if (resultText.StartsWith("Error:"))
                    {
                        ExportAllDataButton.Content = "Failed";
                        Logger.Error($"Export failed: {resultText}");
                    }
                    else
                    {
                        ExportAllDataButton.Content = "Exported!";
                        Logger.Info($"All data exported to: {resultText}");
                    }
                }
                else
                {
                    ExportAllDataButton.Content = "Failed";
                }

                await Task.Delay(2000);
                ExportAllDataButton.Content = "Export All Data";
                ExportAllDataButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to export all data: {ex.Message}");
                ExportAllDataButton.Content = "Export All Data";
                ExportAllDataButton.IsEnabled = true;
            }
        }

        private async void ImportAllDataButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Open folder picker to select backup folder
                var folderPicker = new Windows.Storage.Pickers.FolderPicker();
                folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
                folderPicker.FileTypeFilter.Add("*");

                var folder = await folderPicker.PickSingleFolderAsync();
                if (folder == null)
                    return; // User cancelled

                // Check if this looks like a valid backup folder
                var manifestFile = await folder.TryGetItemAsync("manifest.json");
                if (manifestFile == null)
                {
                    var warningDialog = new Windows.UI.Popups.MessageDialog(
                        "The selected folder doesn't appear to be a valid GoTweaks Lite backup.\n\n" +
                        "Please select a folder created by 'Export All Data' (e.g., GoTweaks_Backup_2024-...).",
                        "Invalid Backup Folder");
                    await warningDialog.ShowAsync();
                    return;
                }

                // Show confirmation dialog
                var dialog = new Windows.UI.Popups.MessageDialog(
                    $"Import data from:\n{folder.Name}\n\n" +
                    "This will:\n" +
                    "• Import all per-game profiles\n" +
                    "• Import global settings\n" +
                    "• Import helper settings\n" +
                    "• Apply widget settings\n\n" +
                    "Existing data will be overwritten. Continue?",
                    "Import All Data");

                dialog.Commands.Add(new Windows.UI.Popups.UICommand("Import"));
                dialog.Commands.Add(new Windows.UI.Popups.UICommand("Cancel"));
                dialog.DefaultCommandIndex = 1;
                dialog.CancelCommandIndex = 1;

                var confirmResult = await dialog.ShowAsync();
                if (confirmResult.Label == "Cancel")
                    return;

                ImportAllDataButton.IsEnabled = false;
                ImportAllDataButton.Content = "Importing...";

                if (!App.IsConnected)
                {
                    ImportAllDataButton.Content = "Helper not connected";
                    await Task.Delay(2000);
                    ImportAllDataButton.Content = "Import All Data";
                    ImportAllDataButton.IsEnabled = true;
                    return;
                }

                // Send request to helper to import all data
                var message = new Windows.Foundation.Collections.ValueSet();
                message.Add("Command", (int)Shared.Enums.Command.Set);
                message.Add("Function", (int)Shared.Enums.Function.ImportAllData);
                message.Add("Content", folder.Path);
                var result = await App.SendMessageAsync(message);

                if (result != null && result.TryGetValue("Content", out object contentObj))
                {
                    string summary = contentObj?.ToString() ?? "Import completed";

                    // Check if widget settings were returned
                    if (result.TryGetValue("WidgetSettings", out object widgetSettingsObj))
                    {
                        string widgetSettingsJson = widgetSettingsObj?.ToString();
                        if (!string.IsNullOrEmpty(widgetSettingsJson))
                        {
                            ApplyImportedWidgetSettings(widgetSettingsJson);
                            summary += "\n\nWidget settings have been applied.";
                        }
                    }

                    // Show result dialog
                    var resultDialog = new Windows.UI.Popups.MessageDialog(summary, "Import Complete");
                    await resultDialog.ShowAsync();

                    ImportAllDataButton.Content = "Imported!";
                }
                else
                {
                    ImportAllDataButton.Content = "Failed";
                }

                await Task.Delay(2000);
                ImportAllDataButton.Content = "Import All Data";
                ImportAllDataButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to import all data: {ex.Message}");
                ImportAllDataButton.Content = "Import All Data";
                ImportAllDataButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Gathers widget LocalSettings as JSON for export.
        /// </summary>
        private string GatherWidgetSettingsForExport()
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                var jsonObj = new Windows.Data.Json.JsonObject();

                // Export all known settings keys
                var keysToExport = new[]
                {
                    // OSD settings
                    "OSDConfig", "OLEDConfig",
                    // Profile settings
                    "ProfileMatchByExe", "ProfileGamesOnly", "ProfileCustomGamePath", "ProfileBlacklistPaths",
                    // Legion settings
                    "LegionL_Action", "LegionL_Shortcut", "LegionL_Command",
                    "LegionR_Action", "LegionR_Shortcut", "LegionR_Command",
                    "LegionTouchpadVibration", "LegionDesktopControls",
                    // Controller hotkey settings
                    "ControllerHotkeyConfig",
                    // Display settings
                    "RefreshRateProfile"
                };

                foreach (var key in keysToExport)
                {
                    if (settings.Values.ContainsKey(key))
                    {
                        var value = settings.Values[key];
                        if (value is bool boolVal)
                            jsonObj[key] = Windows.Data.Json.JsonValue.CreateBooleanValue(boolVal);
                        else if (value is int intVal)
                            jsonObj[key] = Windows.Data.Json.JsonValue.CreateNumberValue(intVal);
                        else if (value is double doubleVal)
                            jsonObj[key] = Windows.Data.Json.JsonValue.CreateNumberValue(doubleVal);
                        else if (value is string strVal)
                            jsonObj[key] = Windows.Data.Json.JsonValue.CreateStringValue(strVal);
                    }
                }

                return jsonObj.Stringify();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to gather widget settings for export: {ex.Message}");
                return "{}";
            }
        }

        /// <summary>
        /// Applies imported widget settings from JSON.
        /// </summary>
        private void ApplyImportedWidgetSettings(string json)
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;

                if (!Windows.Data.Json.JsonObject.TryParse(json, out Windows.Data.Json.JsonObject jsonObj))
                {
                    Logger.Error("Failed to parse imported widget settings JSON");
                    return;
                }

                int importedCount = 0;
                foreach (var key in jsonObj.Keys)
                {
                    try
                    {
                        var jsonValue = jsonObj[key];
                        object value = null;

                        switch (jsonValue.ValueType)
                        {
                            case Windows.Data.Json.JsonValueType.Boolean:
                                value = jsonValue.GetBoolean();
                                break;
                            case Windows.Data.Json.JsonValueType.Number:
                                // Try to preserve int vs double
                                double numVal = jsonValue.GetNumber();
                                if (numVal == Math.Floor(numVal) && numVal >= int.MinValue && numVal <= int.MaxValue)
                                    value = (int)numVal;
                                else
                                    value = numVal;
                                break;
                            case Windows.Data.Json.JsonValueType.String:
                                value = jsonValue.GetString();
                                break;
                        }

                        if (value != null)
                        {
                            settings.Values[key] = value;
                            importedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to import setting '{key}': {ex.Message}");
                    }
                }

                Logger.Info($"Applied {importedCount} widget settings from import");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to apply imported widget settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Dark, rounded Windows 11-style ContentDialog, replacing the legacy
        /// Windows.UI.Popups.MessageDialog (which always renders with the OS light
        /// system chrome regardless of app theme - looks like a Windows 10 dialog).
        /// The dialog panel gets literal colors matching the widget's own card palette;
        /// the Primary/Close buttons deliberately keep the system ContentDialog's own
        /// default template (correct native size/padding/corner-radius/hover states) and
        /// only get their Background/Foreground recolored via the documented named-parts
        /// pattern (PrimaryButton/CloseButton, found post-Opened) - building a from-scratch
        /// Button Style here previously produced mismatched proportions (too tall, uneven
        /// widths between the two buttons) that didn't match the rest of the app's buttons.
        /// </summary>
        private ContentDialog BuildWin11Dialog(string title, string message, string primaryText, string closeText)
        {
            var dialog = new ContentDialog
            {
                Title = new TextBlock
                {
                    Text = title,
                    FontSize = 16,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White)
                },
                Content = new TextBlock
                {
                    Text = message,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xD0, 0xD0, 0xD0)),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ElementTheme.Dark,
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x30, 0x34, 0x3A)),
                Foreground = new SolidColorBrush(Colors.White),
                CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x50, 0x55, 0x5C)),
                BorderThickness = new Thickness(1)
            };

            dialog.Opened += (s, args) =>
            {
                if (!string.IsNullOrEmpty(primaryText) && dialog.FindName("PrimaryButton") is Button primaryBtn)
                {
                    primaryBtn.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
                    primaryBtn.Foreground = new SolidColorBrush(Colors.White);
                }
                if (dialog.FindName("CloseButton") is Button closeBtn)
                {
                    closeBtn.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0x43, 0x4B));
                    closeBtn.Foreground = new SolidColorBrush(Colors.White);
                }
            };

            return dialog;
        }

        /// <summary>Win11-styled confirm dialog. Returns true if the primary (non-Cancel) button was pressed.</summary>
        private async Task<bool> ShowWin11ConfirmDialogAsync(string title, string message, string primaryText = "Continue", string closeText = "Cancel")
        {
            var dialog = BuildWin11Dialog(title, message, primaryText, closeText);
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        /// <summary>Win11-styled info dialog with a single "OK" button.</summary>
        private async Task ShowWin11InfoDialogAsync(string title, string message)
        {
            var dialog = BuildWin11Dialog(title, message, primaryText: null, closeText: "OK");
            await dialog.ShowAsync();
        }

        private async void PrepareForUninstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Show confirmation dialog
                bool proceed = await ShowWin11ConfirmDialogAsync(
                    "Prepare for Uninstall",
                    "This will:\n\n" +
                    "• Remove the scheduled task\n" +
                    "• Restore original CPU Boost, EPP, Max/Min CPU State and Power Mode settings\n" +
                    "• Re-enable Legion Space service (if disabled)\n" +
                    "• Re-enable the touchscreen (if disabled)\n" +
                    "• Restore the Lenovo fan curve\n\n" +
                    "This does not remove the deployed helper copy - for a full cleanup, run " +
                    "Uninstall-GoTweaks.ps1 after uninstalling.\n\n" +
                    "After this, you can safely uninstall the app.");

                if (!proceed)
                    return;

                PrepareForUninstallButton.IsEnabled = false;
                PrepareForUninstallButton.Content = "Restoring...";

                if (!App.IsConnected)
                {
                    PrepareForUninstallButton.Content = "Helper not connected";
                    await Task.Delay(2000);
                    PrepareForUninstallButton.Content = "Prepare for Uninstall";
                    PrepareForUninstallButton.IsEnabled = true;
                    return;
                }

                // Send request to helper to prepare for uninstall
                var message = new Windows.Foundation.Collections.ValueSet();
                message.Add("Command", (int)Shared.Enums.Command.Set);
                message.Add("Function", (int)Shared.Enums.Function.PrepareForUninstall);
                var response = await App.SendMessageAsync(message);

                if (response != null && response.TryGetValue("Content", out object contentObj))
                {
                    string resultText = contentObj?.ToString() ?? "Completed";
                    Logger.Info($"PrepareForUninstall result:\n{resultText}");

                    // Show result in a dialog
                    await ShowWin11InfoDialogAsync("Uninstall Preparation Complete", resultText);

                    PrepareForUninstallButton.Content = "Done!";
                }
                else
                {
                    PrepareForUninstallButton.Content = "Failed";
                }

                await Task.Delay(2000);
                PrepareForUninstallButton.Content = "Prepare for Uninstall";
                PrepareForUninstallButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to prepare for uninstall: {ex.Message}");
                PrepareForUninstallButton.Content = "Prepare for Uninstall";
                PrepareForUninstallButton.IsEnabled = true;
            }
        }
    }
}
