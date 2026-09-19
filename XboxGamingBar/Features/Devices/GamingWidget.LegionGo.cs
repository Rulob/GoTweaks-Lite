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
        /// <summary>
        /// Shows or hides the Legion tab based on device detection
        /// </summary>
        private void SetLegionTabVisibility(bool visible)
        {
            if (LegionNavItem != null)
            {
                LegionNavItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Legion tab visibility set to: {visible}");
            }

            // TDP Mode card is always visible for all devices
            // Legion devices: uses hardware presets (Quiet/Balanced/Performance/Custom)
            // Generic devices: uses TDP value presets (8W/15W/25W/Custom)
            if (TDPModeCard != null)
            {
                TDPModeCard.Visibility = Visibility.Visible;
                Logger.Info($"TDP Mode card visibility set to: Visible (Legion={visible})");

                // Update XY focus bindings - TDP Mode card is always present now
                UpdatePerformanceTabXYFocus(true);
            }

            // Show/hide Manufacturer WMI option in TDP Method dropdown based on Legion detection
            if (TdpMethodWmiItem != null)
            {
                TdpMethodWmiItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"TDP Method WMI option visibility set to: {visible}");

                // Auto-default only BEFORE the helper has supplied a value. After
                // helper sync, the helper's persisted choice is authoritative — its
                // queued NotifyPropertyChanged dispatcher entry will set SelectedIndex.
                // Racing it with SelectedIndex=0 here would push WMI=0 UP and clobber
                // the helper's PawnIO selection (issue #79 round-3 regression).
                bool helperOwnsTdpMethod = tdpMethod != null && tdpMethod.HasReceivedHelperSync;

                // If Legion detected and WMI option now visible, select it if not already selected.
                // Skip when helper owns the value — its sync will land the right index momentarily.
                if (visible && TdpMethodComboBox != null && TdpMethodComboBox.SelectedIndex < 0 && !helperOwnsTdpMethod)
                {
                    TdpMethodComboBox.SelectedIndex = 0; // ManufacturerWMI
                }
                // If Legion not detected and WMI was selected, switch to PawnIO.
                // Same rationale: helper-driven values must not be overridden here.
                else if (!visible && TdpMethodComboBox != null && !helperOwnsTdpMethod)
                {
                    var selectedItem = TdpMethodComboBox.SelectedItem as ComboBoxItem;
                    if (selectedItem?.Tag is string tag && tag == "ManufacturerWMI")
                    {
                        // Find and select PawnIO
                        for (int i = 0; i < TdpMethodComboBox.Items.Count; i++)
                        {
                            if (TdpMethodComboBox.Items[i] is ComboBoxItem item && item.Tag is string t && t == "PawnIO")
                            {
                                TdpMethodComboBox.SelectedIndex = i;
                                break;
                            }
                        }
                    }
                }
            }

            // Update the TDP Method description based on Legion detection
            if (TdpMethodDescription != null)
            {
                if (visible)
                {
                    TdpMethodDescription.Text = "Select TDP control method. Manufacturer WMI (Legion) and PawnIO are anti-cheat safe.";
                }
                else
                {
                    TdpMethodDescription.Text = "Select TDP control method. PawnIO is anti-cheat safe. WinRing0 may trigger anti-cheat.";
                }
            }

            // Refresh Quick Settings tiles to show/hide Legion-specific tiles
            RefreshQuickSettingsForLegion();
        }

        /// <summary>
        /// Updates the device name display in the Legion tab header.
        /// </summary>
        private void SetLegionDeviceName(string name)
        {
            if (LegionDeviceNameText != null && !string.IsNullOrEmpty(name))
            {
                LegionDeviceNameText.Text = name;
                Logger.Info($"Legion device name set to: {name}");
            }
        }

        /// <summary>
        /// Shows or hides the Controller Remapping section based on device support.
        /// Legion Go S has a different HID structure, so controller remapping doesn't work.
        /// </summary>
        private void SetControllerRemappingSectionVisibility(bool visible)
        {
            if (ControllerRemappingSection != null)
            {
                ControllerRemappingSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Controller Remapping section visibility set to: {visible}");

                if (visible)
                {
                    RefreshLegionEnhancedRemapUi();
                }
            }
        }

        /// <summary>
        /// Shows or hides the Lighting section based on device support.
        /// Legion Go S has a different HID structure, so RGB lighting control doesn't work.
        /// </summary>
        private void SetLightingSectionVisibility(bool visible)
        {
            if (LightingSection != null)
            {
                LightingSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Lighting section visibility set to: {visible}");
            }
        }

        /// <summary>
        /// Shows or hides the Gyro Settings card based on device support.
        /// Legion Go S has a different HID structure, so gyro configuration doesn't work.
        /// </summary>
        private void SetGyroSectionVisibility(bool visible)
        {
            if (GyroSection != null)
            {
                GyroSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Gyro section visibility set to: {visible}");
            }
        }

        /// <summary>
        /// Fires a one-shot "CalibrateLegionGyro" pipe message. The helper sends the
        /// HID output report to both controllers; the controller firmware captures the
        /// new bias while the user holds the pads still.
        /// </summary>
        private async void LegionCalibrateGyroButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Logger.Info("Legion gyro calibration requested from widget");

            // Brief UI feedback: disable the button while the request is in flight.
            Button button = LegionCalibrateGyroButton;
            string originalText = button?.Content?.ToString() ?? "Calibrate gyro";
            if (button != null) { button.IsEnabled = false; button.Content = "Calibrating…"; }
            if (LegionCalibrateGyroStatus != null)
                LegionCalibrateGyroStatus.Text = "Hold the controllers still…";

            try
            {
                if (App.PipeClient?.IsConnected == true)
                {
                    var request = new Windows.Foundation.Collections.ValueSet();
                    request.Add("CalibrateLegionGyro", true);
                    await App.SendMessageAsync(request);
                    if (LegionCalibrateGyroStatus != null)
                        LegionCalibrateGyroStatus.Text = "Calibration command sent. Keep the pads still for a moment.";
                }
                else
                {
                    if (LegionCalibrateGyroStatus != null)
                        LegionCalibrateGyroStatus.Text = "Not connected to helper — try again after reconnecting.";
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"CalibrateLegionGyro send failed: {ex.Message}");
                if (LegionCalibrateGyroStatus != null)
                    LegionCalibrateGyroStatus.Text = $"Calibration send failed: {ex.Message}";
            }
            finally
            {
                await Task.Delay(1200);
                if (button != null) { button.IsEnabled = true; button.Content = originalText; }
            }
        }

        private void SetScrollWheelSectionVisibility(bool visible)
        {
            if (ScrollWheelSection != null)
            {
                ScrollWheelSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Scroll wheel section visibility set to: {visible}");
            }
        }

        // ---- Driver Updates (Lenovo) --------------------------------------------------

        private void DriverUpdatesExpandToggle_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (DriverUpdatesContent == null || DriverUpdatesExpandIcon == null) return;
            bool expand = DriverUpdatesExpandToggle?.IsChecked == true;
            DriverUpdatesContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            // \uE70E = chevron down (collapsed), \uE70D = chevron up (expanded)
            DriverUpdatesExpandIcon.Glyph = expand ? "\uE70E" : "\uE70D";
        }

        private async void DriverUpdatesCheckButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (DriverUpdatesCheckButton == null) return;

            string originalContent = DriverUpdatesCheckButton.Content?.ToString() ?? "Check for updates";
            DriverUpdatesCheckButton.IsEnabled = false;
            DriverUpdatesCheckButton.Content = "Checking…";
            if (DriverUpdatesStatusText != null)
                DriverUpdatesStatusText.Text = "Reading machine info and contacting Lenovo…";
            if (DriverUpdatesList != null)
                DriverUpdatesList.Visibility = Visibility.Collapsed;

            try
            {
                if (!App.IsConnected)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper not connected. Try again once the widget reconnects.";
                    return;
                }

                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("CheckDriverUpdates", true);
                var response = await App.SendMessageAsync(request);
                if (response == null)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "No response from helper.";
                    return;
                }

                if (!response.TryGetValue("DriverUpdateResult", out var payloadObj) || !(payloadObj is string payload))
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper returned an unexpected response shape.";
                    return;
                }

                RenderDriverUpdateResult(payload);
            }
            catch (Exception ex)
            {
                Logger.Warn($"DriverUpdatesCheckButton_Click failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Check failed: {ex.Message}";
            }
            finally
            {
                DriverUpdatesCheckButton.IsEnabled = true;
                DriverUpdatesCheckButton.Content = originalContent;
            }
        }

        private async void DriverUpdatesOpenPageButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                // If we already know the MT code from a previous Check, use that URL
                // directly; otherwise ask the helper for a fresh snapshot so we can
                // route to the correct machine-specific page.
                string url = _lastDriverPageUrl;
                if (string.IsNullOrEmpty(url) && App.IsConnected)
                {
                    var request = new Windows.Foundation.Collections.ValueSet();
                    request.Add("CheckDriverUpdates", true);
                    var response = await App.SendMessageAsync(request);
                    if (response != null && response.TryGetValue("DriverUpdateResult", out var payloadObj) && payloadObj is string payload)
                    {
                        RenderDriverUpdateResult(payload);
                        url = _lastDriverPageUrl;
                    }
                }
                if (string.IsNullOrEmpty(url))
                {
                    url = "https://pcsupport.lenovo.com/";
                }
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                Logger.Warn($"DriverUpdatesOpenPageButton_Click failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Couldn't open browser: {ex.Message}";
            }
        }

        private string _lastDriverPageUrl;

        /// <summary>
        /// Parses the helper's DriverUpdateResult JSON (shipped over the pipe as a
        /// camelCase JSON string) and writes it into the UI. Uses Windows.Data.Json
        /// because the UWP widget targets C# 7.3 without System.Text.Json. Defensive
        /// against missing fields — any field the helper couldn't populate shows "—".
        /// </summary>
        private async void RenderDriverUpdateResult(string json)
        {
            // Windows.Data.Json + Xaml property setters both require the UI
            // thread (WinRT single-apartment). This method is reachable from
            // the pipe-read threadpool path (PrefetchDriverUpdatesAsync /
            // UpdateDriverUpdatesTile on startup push) so force a dispatch
            // before we touch either. Without this the heartbeat-watcher
            // reconnect after a long UAC gap threw RPC_E_WRONG_THREAD and
            // left the driver list empty.
            if (Dispatcher != null && !Dispatcher.HasThreadAccess)
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => RenderDriverUpdateResult(json));
                return;
            }
            try
            {
                if (!Windows.Data.Json.JsonObject.TryParse(json, out var root))
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper returned a response the widget couldn't parse.";
                    return;
                }

                string GetStr(string name) =>
                    (root.TryGetValue(name, out var v) && v.ValueType == Windows.Data.Json.JsonValueType.String)
                        ? v.GetString() ?? "" : "";
                bool GetBool(string name) =>
                    root.TryGetValue(name, out var v) && v.ValueType == Windows.Data.Json.JsonValueType.Boolean && v.GetBoolean();

                string mt = GetStr("machineTypeCode");
                string model = GetStr("model");
                string modelVersion = GetStr("modelVersion");
                string bios = GetStr("biosVersion");
                string pageUrl = GetStr("driverPageUrl");
                bool isLenovo = GetBool("isLenovo");
                bool liveFetch = GetBool("liveFetchSucceeded");
                string error = GetStr("errorMessage");

                _lastDriverPageUrl = string.IsNullOrEmpty(pageUrl) ? "https://pcsupport.lenovo.com/" : pageUrl;
                if (DriverUpdatesMachineType != null) DriverUpdatesMachineType.Text = string.IsNullOrEmpty(mt) ? "—" : mt;
                if (DriverUpdatesModel != null)
                {
                    string modelText = string.IsNullOrEmpty(model) ? (string.IsNullOrEmpty(modelVersion) ? "—" : modelVersion) : model;
                    DriverUpdatesModel.Text = modelText;
                }
                if (DriverUpdatesBios != null) DriverUpdatesBios.Text = string.IsNullOrEmpty(bios) ? "—" : bios;

                if (!isLenovo)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = string.IsNullOrEmpty(error)
                            ? "This feature only works on Lenovo devices."
                            : $"Not a Lenovo device: {error}";
                    if (DriverUpdatesList != null) DriverUpdatesList.Visibility = Visibility.Collapsed;
                    return;
                }

                // Render the driver list if we got one. If Lenovo's API didn't respond
                // (live fetch failed or returned empty), fall back to prompting the
                // user to open the driver page in a browser.
                var items = new System.Collections.Generic.List<DriverDisplay>();
                if (root.TryGetValue("drivers", out var driversVal) && driversVal.ValueType == Windows.Data.Json.JsonValueType.Array)
                {
                    foreach (var elem in driversVal.GetArray())
                    {
                        if (elem.ValueType != Windows.Data.Json.JsonValueType.Object) continue;
                        var d = elem.GetObject();
                        string GetD(string key) =>
                            (d.TryGetValue(key, out var vv) && vv.ValueType == Windows.Data.Json.JsonValueType.String)
                                ? vv.GetString() ?? "" : "";
                        int statusCode = 0;
                        if (d.TryGetValue("updateStatus", out var usVal) && usVal.ValueType == Windows.Data.Json.JsonValueType.Number)
                            statusCode = (int)usVal.GetNumber();
                        string installed = GetD("installedVersion");
                        string downloadUrl = GetD("downloadUrl");
                        var (installLabel, installVis) = InstallButtonForStatus(statusCode, downloadUrl);
                        items.Add(new DriverDisplay
                        {
                            Name = GetD("name"),
                            Category = GetD("category"),
                            Version = GetD("version"),
                            ReleaseDate = GetD("releaseDate"),
                            DownloadUrl = downloadUrl,
                            Severity = SeverityLabel(GetD("severity")),
                            InstalledVersion = string.IsNullOrWhiteSpace(installed) ? "—" : installed,
                            StatusLabel = StatusLabelFor(statusCode),
                            StatusColor = StatusColorFor(statusCode),
                            StatusTextColor = StatusTextColorFor(statusCode),
                            InstallButtonLabel = installLabel,
                            InstallButtonVisibility = installVis,
                        });
                    }
                }

                // Remember the full list so the utilities/diagnostics checkbox
                // can filter it on the fly without another helper round-trip.
                _allDriverDisplays = items;

                // Restore checkbox state from LocalSettings (first render after
                // widget startup) — no-op on later renders since the state
                // already matches.
                if (DriverUpdatesShowUtilitiesCheckbox != null)
                {
                    var persisted = DriverUpdatesShowUtilities;
                    if ((DriverUpdatesShowUtilitiesCheckbox.IsChecked == true) != persisted)
                        DriverUpdatesShowUtilitiesCheckbox.IsChecked = persisted;
                }

                ApplyDriverFilters();
                UpdateUpdateAllButtonVisibility();

                if (DriverUpdatesStatusText != null)
                {
                    if (items.Count > 0)
                    {
                        int upToDate = 0, update = 0, unknown = 0, notInstalled = 0;
                        foreach (var it in items)
                        {
                            switch (it.StatusLabel)
                            {
                                case "Up to date": upToDate++; break;
                                case "Update": update++; break;
                                case "Not installed": notInstalled++; break;
                                default: unknown++; break;
                            }
                        }
                        DriverUpdatesStatusText.Text = $"{items.Count} drivers checked — {upToDate} up to date, {update} update available, {notInstalled} not installed, {unknown} unknown.";
                    }
                    else if (liveFetch)
                    {
                        DriverUpdatesStatusText.Text = "Lenovo returned no drivers for this machine type.";
                    }
                    else
                    {
                        DriverUpdatesStatusText.Text = "Lenovo's live driver list is unreachable. Use Open Lenovo driver page to browse on lenovo.com.";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"RenderDriverUpdateResult parse failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Couldn't parse response: {ex.Message}";
            }
        }

        private sealed class DriverDisplay
        {
            public string Name { get; set; }
            public string Category { get; set; }
            public string Version { get; set; }
            public string ReleaseDate { get; set; }
            public string DownloadUrl { get; set; }
            /// <summary>Human-readable severity label ("Critical", "Recommended", "Optional").</summary>
            public string Severity { get; set; }
            /// <summary>Installed driver version on this device (or "—" when unmatched).</summary>
            public string InstalledVersion { get; set; }
            /// <summary>Short badge label ("Up to date", "Update", "Not installed", "Unknown").</summary>
            public string StatusLabel { get; set; }
            /// <summary>Solid-color hex (#AARRGGBB) for the status pill background.</summary>
            public Windows.UI.Xaml.Media.Brush StatusColor { get; set; }
            /// <summary>Text color for the status pill label — black on the up-to-date green for contrast, white otherwise.</summary>
            public Windows.UI.Xaml.Media.Brush StatusTextColor { get; set; }
            /// <summary>"Install" when missing, "Update" when outdated — empty when neither applies (hides the button).</summary>
            public string InstallButtonLabel { get; set; }
            /// <summary>Visibility of the Install/Update button — hidden when the driver is up-to-date or has no download URL.</summary>
            public Windows.UI.Xaml.Visibility InstallButtonVisibility { get; set; }
        }

        // Cached full driver list so the "Show utilities and diagnostics"
        // checkbox can toggle visibility without re-querying the helper.
        // Populated by RenderDriverUpdateResult, consumed by ApplyDriverFilters.
        private System.Collections.Generic.List<DriverDisplay> _allDriverDisplays = new System.Collections.Generic.List<DriverDisplay>();

        // Categories suppressed by default. User toggles via the checkbox —
        // persisted in LocalSettings so the preference survives widget
        // restarts without needing a dedicated property.
        private static readonly System.Collections.Generic.HashSet<string> _lowSignalDriverCategories =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "Diagnostic",
                "Software and Utilities",
                "Tool",
            };

        private const string DriverUpdatesShowUtilitiesKey = "DriverUpdates_ShowUtilities";
        // Kept for back-compat — same storage key, new meaning: "CheckOnStart".
        // Historically this was "UpdateOnStart" (auto-install). Changed after
        // users asked for a way to skip the Lenovo check entirely — the name
        // stays so existing LocalSettings values carry over. Default true so
        // first-install users still see the banner after startup probe.
        private const string DriverUpdatesUpdateOnStartKey = "DriverUpdates_UpdateOnStart";
        private const string DriverUpdatesHideBannerKey    = "DriverUpdates_HideBanner";

        private static bool GetBoolSetting(string key, bool defaultValue)
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue(key, out var v) && v is bool b) return b;
            }
            catch { }
            return defaultValue;
        }

        private static void SetBoolSetting(string key, bool value)
        {
            try { Windows.Storage.ApplicationData.Current.LocalSettings.Values[key] = value; }
            catch { }
        }

        private bool DriverUpdatesCheckOnStart
        {
            // Default true: users who haven't explicitly opted out expect the
            // helper's startup probe to populate the banner automatically.
            get => GetBoolSetting(DriverUpdatesUpdateOnStartKey, true);
            set => SetBoolSetting(DriverUpdatesUpdateOnStartKey, value);
        }
        private bool DriverUpdatesHideBanner
        {
            get => GetBoolSetting(DriverUpdatesHideBannerKey, false);
            set => SetBoolSetting(DriverUpdatesHideBannerKey, value);
        }

        private async void DriverUpdatesUpdateOnStartCheckbox_Changed(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (_isLoadingUpdatePreferenceCheckboxes) return;
            if (DriverUpdatesUpdateOnStartCheckbox == null) return;
            bool on = DriverUpdatesUpdateOnStartCheckbox.IsChecked == true;
            DriverUpdatesCheckOnStart = on;
            // Forward to helper so its next startup honours the toggle.
            // Helper persists via its own LocalSettingsHelper (separate store
            // from widget LocalSettings) and reads the value before running
            // the probe, so the following launch skips the Lenovo fetch when
            // the box is unchecked.
            try
            {
                if (App.IsConnected)
                {
                    var req = new Windows.Foundation.Collections.ValueSet();
                    req.Add("SetDriverCheckOnStart", on);
                    await App.SendMessageAsync(req);
                }
            }
            catch (Exception ex) { Logger.Warn($"SetDriverCheckOnStart forward failed: {ex.Message}"); }
        }

        private void DriverUpdatesHideBannerCheckbox_Changed(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (_isLoadingUpdatePreferenceCheckboxes) return;
            if (DriverUpdatesHideBannerCheckbox == null) return;
            DriverUpdatesHideBanner = DriverUpdatesHideBannerCheckbox.IsChecked == true;
            // Re-evaluate tile visibility right now so the setting has an
            // immediate effect without waiting for the next helper push.
            if (QuickDriverUpdatesTile != null && DriverUpdatesHideBanner)
            {
                QuickDriverUpdatesTile.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
            }
        }

        private bool DriverUpdatesShowUtilities
        {
            get
            {
                try
                {
                    var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
                    if (settings.Values.TryGetValue(DriverUpdatesShowUtilitiesKey, out var v) && v is bool b) return b;
                }
                catch { }
                return false;
            }
            set
            {
                try
                {
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values[DriverUpdatesShowUtilitiesKey] = value;
                }
                catch { }
            }
        }

        /// <summary>
        /// Called from the pipe-message handler when the helper pushes a
        /// startup-probe result. Shows/hides the Quick-tab tile and updates
        /// the count badge on the UI thread.
        /// </summary>
        internal async void UpdateDriverUpdatesTile(int count)
        {
            try
            {
                bool hideBanner = DriverUpdatesHideBanner;
                bool checkOnStart = DriverUpdatesCheckOnStart;

                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (QuickDriverUpdatesTile == null) return;
                    // Sync the checkboxes themselves with persisted state so
                    // the user sees the current setting on first render. Guarded
                    // like every other programmatic write to these checkboxes
                    // (see _isLoadingUpdatePreferenceCheckboxes) so this doesn't
                    // re-fire the Changed handlers and pipe a redundant Set*OnStart
                    // message to the helper on every driver-update push.
                    _isLoadingUpdatePreferenceCheckboxes = true;
                    try
                    {
                        if (DriverUpdatesUpdateOnStartCheckbox != null && DriverUpdatesUpdateOnStartCheckbox.IsChecked != checkOnStart)
                            DriverUpdatesUpdateOnStartCheckbox.IsChecked = checkOnStart;
                        if (DriverUpdatesHideBannerCheckbox != null && DriverUpdatesHideBannerCheckbox.IsChecked != hideBanner)
                            DriverUpdatesHideBannerCheckbox.IsChecked = hideBanner;
                    }
                    finally
                    {
                        _isLoadingUpdatePreferenceCheckboxes = false;
                    }

                    bool visible = count > 0
                                   && legionGoDetected != null
                                   && legionGoDetected.Value
                                   && !hideBanner;
                    QuickDriverUpdatesTile.Visibility = visible
                        ? Windows.UI.Xaml.Visibility.Visible
                        : Windows.UI.Xaml.Visibility.Collapsed;
                    if (QuickDriverUpdatesCountText != null)
                        QuickDriverUpdatesCountText.Text = count.ToString();
                    if (QuickDriverUpdatesTitleText != null)
                        QuickDriverUpdatesTitleText.Text = count == 1
                            ? "1 driver update available"
                            : count + " driver updates available";

                    // Keep the in-card "Update all" button in sync with the
                    // same count — it's visible whenever there's at least one
                    // installable row (Install or Update).
                    UpdateUpdateAllButtonVisibility();
                });

                // Pre-populate the driver list so a later tile/tab click shows
                // it instantly instead of requiring another Check-for-updates
                // press. Helper serves this from its startup-probe cache so
                // there's no Lenovo round-trip here.
                if (count > 0 && (_allDriverDisplays == null || _allDriverDisplays.Count == 0))
                {
                    await PrefetchDriverUpdatesAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"UpdateDriverUpdatesTile failed: {ex.Message}");
            }
        }

        /// <summary>
        /// A row qualifies for "Update all" only when:
        ///   1. It has UpdateAvailable status (not NotInstalled — installing
        ///      drivers Windows doesn't have yet would be surprising, and the
        ///      user wasn't asking for that).
        ///   2. It's currently visible in the list — the utilities/diagnostics
        ///      filter + Tool category exclusion on the card shouldn't leak
        ///      into a bulk install. User's previous run launched all 24
        ///      candidate installers at once, including hidden ones, which
        ///      was the opposite of what they asked for.
        /// </summary>
        private bool IsUpdateAllCandidate(DriverDisplay d)
        {
            if (d == null) return false;
            // Status label is set by StatusLabelFor — "Update" is the
            // UpdateAvailable case, "Install" is NotInstalled, "Up to date"
            // is UpToDate, "Unknown" otherwise.
            if (!string.Equals(d.StatusLabel, "Update", StringComparison.Ordinal)) return false;
            if (string.IsNullOrWhiteSpace(d.DownloadUrl)) return false;
            // Respect the "Show utilities and diagnostics" checkbox: when
            // unchecked we hide those categories + Tool from the list, so
            // Update-all must hide them too.
            if (!DriverUpdatesShowUtilities && _lowSignalDriverCategories.Contains(d.Category ?? "")) return false;
            return true;
        }

        /// <summary>
        /// Shows "Update all" button when at least one visible row is an
        /// actionable UpdateAvailable (the same set IsUpdateAllCandidate
        /// returns true for — keeps the button visibility consistent with
        /// what the button would actually install).
        /// </summary>
        private void UpdateUpdateAllButtonVisibility()
        {
            if (DriverUpdatesUpdateAllButton == null) return;
            int updateCount = 0;
            foreach (var d in _allDriverDisplays)
            {
                if (IsUpdateAllCandidate(d)) updateCount++;
            }
            DriverUpdatesUpdateAllButton.Visibility = updateCount > 0
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;
            // Surface the count so the user knows exactly what "Update all"
            // is about to touch — no more surprise 24-installer launches.
            DriverUpdatesUpdateAllButton.Content = updateCount > 1
                ? $"Update all ({updateCount})"
                : "Update all";
        }

        /// <summary>
        /// Clicking the Quick-tab driver-updates tile programmatically checks
        /// the Legion nav radio button, which NavRadioButton_Checked picks up
        /// and uses to switch to the Legion tab (scrolling to top).
        /// </summary>
        private async void QuickDriverUpdatesTile_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                // Switch to Legion tab first (idempotent if already selected). This is
                // user-driven (they tapped a Quick-tab shortcut), so pre-arm with the
                // destination tag so the nav drift guard accepts the change.
                if (LegionNavItem != null && LegionNavItem.IsChecked != true)
                {
                    ArmUserNavIntent(LegionNavItem.Tag as string);
                    LegionNavItem.IsChecked = true;
                }

                // If we haven't yet populated the driver list in this session
                // (widget was re-opened since the helper pushed the count),
                // request from helper now. Helper serves from cache after the
                // first live fetch, so this is cheap — no Lenovo round-trip.
                if (_allDriverDisplays == null || _allDriverDisplays.Count == 0)
                {
                    await PrefetchDriverUpdatesAsync();
                }

                // Let XAML lay out and the ScrollViewer recognise its new
                // content before we try to bring the card into view — otherwise
                // the viewport math is still pointing at the old tab.
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                {
                    try
                    {
                        if (DriverUpdatesCard != null)
                        {
                            DriverUpdatesCard.StartBringIntoView(new Windows.UI.Xaml.BringIntoViewOptions
                            {
                                VerticalAlignmentRatio = 0.0,
                                AnimationDesired = true,
                            });
                        }
                        else if (LegionScrollViewer != null)
                        {
                            // Fallback: no x:Name match, just scroll to the
                            // bottom since the card sits at the end of the tab.
                            LegionScrollViewer.ChangeView(null, LegionScrollViewer.ExtentHeight, null, false);
                        }
                    }
                    catch (Exception ex) { Logger.Debug($"Scroll-to-card failed: {ex.Message}"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"QuickDriverUpdatesTile_Click failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Silently fetches the driver list from the helper cache and renders it
        /// without toggling any button states. Used when the user clicks the
        /// Quick-tab tile for the first time — they expect to see the list
        /// already populated, not have to press "Check for updates" again.
        /// </summary>
        private async Task PrefetchDriverUpdatesAsync()
        {
            try
            {
                if (!App.IsConnected) return;
                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("CheckDriverUpdates", true);
                var response = await App.SendMessageAsync(request);
                if (response == null) return;
                if (response.TryGetValue("DriverUpdateResult", out var payloadObj) && payloadObj is string payload)
                {
                    RenderDriverUpdateResult(payload);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"PrefetchDriverUpdatesAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// "Update all" click: collect every row that has an Install/Update
        /// button visible, send all URLs in one batch pipe message to the
        /// helper. Helper downloads them in parallel then launches sequentially.
        /// </summary>
        private async void DriverUpdatesUpdateAllButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (DriverUpdatesUpdateAllButton == null) return;
            var urls = new System.Collections.Generic.List<string>();
            foreach (var d in _allDriverDisplays)
            {
                // Update-all is strictly VISIBLE + UpdateAvailable. We
                // deliberately don't touch NotInstalled rows (the user
                // doesn't necessarily want Windows to gain drivers it
                // hasn't picked up yet) and we respect the category filter
                // (hidden utilities/diagnostics/tools never get queued).
                if (IsUpdateAllCandidate(d)) urls.Add(d.DownloadUrl);
            }
            if (urls.Count == 0)
            {
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = "No visible driver updates to install.";
                return;
            }

            string originalLabel = DriverUpdatesUpdateAllButton.Content?.ToString() ?? "Update all";
            DriverUpdatesUpdateAllButton.IsEnabled = false;
            DriverUpdatesUpdateAllButton.Content = $"Updating {urls.Count}\u2026";
            if (DriverUpdatesStatusText != null)
                DriverUpdatesStatusText.Text = $"Downloading {urls.Count} installers\u2026";

            try
            {
                if (!App.IsConnected)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper not connected — can't install.";
                    return;
                }

                var request = new Windows.Foundation.Collections.ValueSet();
                // ValueSet can't nest arrays across the pipe contract — join
                // with newlines, helper splits on \n.
                request.Add("BatchInstallDrivers", string.Join("\n", urls));
                var response = await App.SendMessageAsync(request);

                string message = "Done.";
                if (response != null && response.TryGetValue("DriverBatchInstallResult", out var payloadObj) && payloadObj is string payload)
                {
                    try
                    {
                        if (Windows.Data.Json.JsonObject.TryParse(payload, out var root))
                        {
                            string msg = root.TryGetValue("message", out var m)
                                         && m.ValueType == Windows.Data.Json.JsonValueType.String
                                         ? m.GetString() : "";
                            if (!string.IsNullOrWhiteSpace(msg)) message = msg;
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"Batch install parse failed: {ex.Message}"); }
                }
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = message;
            }
            catch (Exception ex)
            {
                Logger.Warn($"DriverUpdatesUpdateAllButton_Click failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Update all failed: {ex.Message}";
            }
            finally
            {
                DriverUpdatesUpdateAllButton.Content = originalLabel;
                DriverUpdatesUpdateAllButton.IsEnabled = true;
            }
        }

        private void DriverUpdatesShowUtilitiesCheckbox_Changed(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (_isLoadingUpdatePreferenceCheckboxes) return;
            if (DriverUpdatesShowUtilitiesCheckbox == null) return;
            DriverUpdatesShowUtilities = DriverUpdatesShowUtilitiesCheckbox.IsChecked == true;
            ApplyDriverFilters();
        }

        /// <summary>
        /// Filters <see cref="_allDriverDisplays"/> by the utilities/diagnostics
        /// checkbox and rebinds the visible list. Kept separate from the
        /// parse path so the checkbox can toggle without re-hitting Lenovo.
        /// </summary>
        private void ApplyDriverFilters()
        {
            if (DriverUpdatesList == null) return;
            bool showUtilities = DriverUpdatesShowUtilities;

            var visible = new System.Collections.Generic.List<DriverDisplay>();
            foreach (var d in _allDriverDisplays)
            {
                if (!showUtilities && _lowSignalDriverCategories.Contains(d.Category ?? ""))
                    continue;
                visible.Add(d);
            }

            DriverUpdatesList.ItemsSource = visible;
            DriverUpdatesList.Visibility = visible.Count > 0
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;

            // Eligible-for-Update-all set tracks the same filter, so refresh
            // the button + its count label whenever the filter flips.
            UpdateUpdateAllButtonVisibility();
        }

        /// <summary>
        /// Click handler for the per-row Install/Update button. Sends the
        /// download URL to the helper, which runs elevated and can launch
        /// the Lenovo installer without an extra UAC prompt. Updates the
        /// button label to "Installing…" during the pipe round-trip so the
        /// user has feedback while the download runs.
        /// </summary>
        private async void DriverInstallButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            var button = sender as Windows.UI.Xaml.Controls.Button;
            if (button == null) return;
            var url = button.Tag as string;
            if (string.IsNullOrWhiteSpace(url))
            {
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = "No download URL for that driver.";
                return;
            }

            string originalLabel = button.Content?.ToString() ?? "Install";
            button.IsEnabled = false;
            button.Content = "Installing\u2026";

            try
            {
                if (!App.IsConnected)
                {
                    if (DriverUpdatesStatusText != null)
                        DriverUpdatesStatusText.Text = "Helper not connected — can't install.";
                    return;
                }

                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("InstallDriverUpdate", url);
                var response = await App.SendMessageAsync(request);
                string message = "Install started.";
                if (response != null && response.TryGetValue("DriverInstallResult", out var payloadObj) && payloadObj is string payload)
                {
                    try
                    {
                        if (Windows.Data.Json.JsonObject.TryParse(payload, out var root))
                        {
                            bool success = root.TryGetValue("success", out var s) &&
                                           s.ValueType == Windows.Data.Json.JsonValueType.Boolean && s.GetBoolean();
                            string msg = root.TryGetValue("message", out var m) &&
                                         m.ValueType == Windows.Data.Json.JsonValueType.String ? m.GetString() : "";
                            message = string.IsNullOrWhiteSpace(msg)
                                ? (success ? "Installer launched." : "Install failed.")
                                : msg;
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"DriverInstallButton parse failed: {ex.Message}"); }
                }
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = message;
            }
            catch (Exception ex)
            {
                Logger.Warn($"DriverInstallButton_Click failed: {ex.Message}");
                if (DriverUpdatesStatusText != null)
                    DriverUpdatesStatusText.Text = $"Install failed: {ex.Message}";
            }
            finally
            {
                button.Content = originalLabel;
                button.IsEnabled = true;
            }
        }

        /// <summary>
        /// Returns ("Install", Visible) for NotInstalled drivers, ("Update",
        /// Visible) for UpdateAvailable, and ("", Collapsed) otherwise.
        /// No button for up-to-date / unknown status since there's nothing
        /// actionable to download.
        /// </summary>
        private static (string label, Windows.UI.Xaml.Visibility vis) InstallButtonForStatus(int statusCode, string downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
                return ("", Windows.UI.Xaml.Visibility.Collapsed);
            switch (statusCode)
            {
                case 2: return ("Update", Windows.UI.Xaml.Visibility.Visible);   // UpdateAvailable
                case 3: return ("Install", Windows.UI.Xaml.Visibility.Visible);  // NotInstalled
                default: return ("", Windows.UI.Xaml.Visibility.Collapsed);
            }
        }

        /// <summary>
        /// Maps Lenovo's numeric severity type (1/2/3) from the per-package XML
        /// into a short human label for the widget. Unknown values pass through
        /// so we don't hide information if Lenovo adds a new level.
        /// </summary>
        private static string SeverityLabel(string raw)
        {
            switch (raw?.Trim())
            {
                case "1": return "Critical";
                case "2": return "Recommended";
                case "3": return "Optional";
                default:  return string.IsNullOrWhiteSpace(raw) ? "" : raw;
            }
        }

        // UpdateStatus enum mirrors DriverUpdateStatus in the helper:
        //   0 = Unknown, 1 = UpToDate, 2 = UpdateAvailable, 3 = NotInstalled
        private static string StatusLabelFor(int code)
        {
            switch (code)
            {
                case 1: return "Up to date";
                case 2: return "Update";
                case 3: return "Not installed";
                default: return "Unknown";
            }
        }

        private static Windows.UI.Xaml.Media.Brush StatusColorFor(int code)
        {
            // Green for up-to-date, orange for "update available", dark gray
            // for not-installed / unknown. The up-to-date green matches the
            // Quick Settings tile severity green (tileSeverityGreenBrush).
            byte r, g, b;
            switch (code)
            {
                case 1: r = 0x6C; g = 0xCB; b = 0x5F; break; // #6CCB5F success green (Quick Settings tile green)
                case 2: r = 0xFF; g = 0xB0; b = 0x60; break; // #FFB060 warning orange
                case 3: r = 0x66; g = 0x66; b = 0x66; break; // #666666 neutral gray
                default: r = 0x55; g = 0x55; b = 0x55; break; // #555555 unknown
            }
            return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, r, g, b));
        }

        private static readonly Windows.UI.Xaml.Media.SolidColorBrush _driverStatusTextBlack =
            new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
        private static readonly Windows.UI.Xaml.Media.SolidColorBrush _driverStatusTextWhite =
            new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        private static Windows.UI.Xaml.Media.Brush StatusTextColorFor(int code)
        {
            // The up-to-date green (#6CCB5F) is light enough that white text loses
            // contrast — black reads clearly on it. The orange/gray chips stay white.
            return code == 1 ? _driverStatusTextBlack : _driverStatusTextWhite;
        }

        private void SetControllerBatterySectionVisibility(bool visible)
        {
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            if (LeftControllerCard != null) LeftControllerCard.Visibility = vis;
            if (RightControllerCard != null) RightControllerCard.Visibility = vis;
            Logger.Info($"Controller battery section visibility set to: {visible}");
        }

        private void SetTouchpadVibrationSectionVisibility(bool visible)
        {
            if (TouchpadVibrationSection != null)
            {
                TouchpadVibrationSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"Touchpad & Vibration section visibility set to: {visible}");
            }
        }
        /// <summary>
        /// Updates XY focus bindings in Performance tab based on Legion detection
        /// </summary>
        private void UpdatePerformanceTabXYFocus(bool isLegion)
        {
            // Master TDP slider removed: PerformanceOverlay -> TDPMode dropdown. From the dropdown the
            // focus continues to the Custom sliders (when visible) or OSPowerMode, wired in XAML.
            if (PerformanceOverlayToggle != null && TDPModeComboBox != null)
            {
                PerformanceOverlayToggle.XYFocusDown = TDPModeComboBox;
                TDPModeComboBox.XYFocusUp = PerformanceOverlayToggle;
            }
        }

        /// <summary>
        /// Updates XY focus navigation for the Performance tab based on current state.
        /// Flow: Nav -> PerGameProfile Toggle (if game detected) -> Performance Overlay -> ...
        /// </summary>
        private void UpdatePerformanceTabXYNavigation()
        {
            // Early exit if UI elements aren't ready
            if (PerformanceNavItem == null || PerformanceOverlayToggle == null) return;

            bool gameDetected = runningGame?.Value.IsValid() == true;

            Logger.Debug($"UpdatePerformanceTabXYNavigation: gameDetected={gameDetected}");

            if (gameDetected && PerGameProfileToggle != null)
            {
                // Game detected: Nav -> PerGameProfile Toggle -> Overlay
                PerformanceNavItem.XYFocusDown = PerGameProfileToggle;
                PerGameProfileToggle.XYFocusUp = PerformanceNavItem;
                PerGameProfileToggle.XYFocusDown = PerformanceOverlayToggle;
                PerformanceOverlayToggle.XYFocusUp = PerGameProfileToggle;
            }
            else
            {
                // No game: Nav -> Overlay (skip disabled PerGameProfile)
                PerformanceNavItem.XYFocusDown = PerformanceOverlayToggle;
                PerformanceOverlayToggle.XYFocusUp = PerformanceNavItem;
            }
        }

        /// <summary>
        /// Updates WinRing0 option visibility in TDP Method dropdown based on file availability.
        /// </summary>
        private void UpdateWinRing0Visibility(bool available)
        {
            if (TdpMethodWinRing0Item != null)
            {
                TdpMethodWinRing0Item.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                Logger.Info($"WinRing0 TDP option visibility set to: {available}");

                // If WinRing0 was selected but is no longer available, switch to WMI or PawnIO
                if (!available && TdpMethodComboBox != null)
                {
                    var selectedItem = TdpMethodComboBox.SelectedItem as ComboBoxItem;
                    if (selectedItem?.Tag is string tag && tag == "WinRing0")
                    {
                        // Try to select ManufacturerWMI first, then PawnIO
                        if (TdpMethodWmiItem?.Visibility == Visibility.Visible)
                        {
                            TdpMethodComboBox.SelectedItem = TdpMethodWmiItem;
                        }
                        else if (TdpMethodPawnIOItem?.Visibility == Visibility.Visible)
                        {
                            TdpMethodComboBox.SelectedItem = TdpMethodPawnIOItem;
                        }
                    }
                }
            }

            // Ensure a valid option is selected after visibility changes
            EnsureValidTdpMethodSelected();
        }

        /// <summary>
        /// Ensures a valid (visible and enabled) TDP method is selected in the dropdown.
        /// IMPORTANT: Never auto-select WinRing0 - user must explicitly choose it.
        /// </summary>
        private void EnsureValidTdpMethodSelected()
        {
            if (TdpMethodComboBox == null) return;

            // Same race as SetLegionTabVisibility (issue #79 round-3 PawnIO regression).
            // This path runs from UpdateWinRing0Visibility / UpdatePawnIOInstalledUI,
            // which fire during BatchSync DOWN. If the TdpMethodProperty's own
            // NotifyPropertyChanged dispatcher entry hasn't run yet, SelectedIndex
            // is still -1 and we'd auto-pick WMI here — fires SelectionChanged
            // and pushes 0 UP, clobbering helper's PawnIO=1. Skip auto-pick when
            // helper owns the value; its NPC dispatcher will land the right index.
            if (tdpMethod != null && tdpMethod.HasReceivedHelperSync)
            {
                Logger.Debug("EnsureValidTdpMethodSelected: helper has synced — skipping auto-pick, helper's value will land via NotifyPropertyChanged");
                return;
            }

            var selectedItem = TdpMethodComboBox.SelectedItem as ComboBoxItem;
            var selectedIndex = TdpMethodComboBox.SelectedIndex;

            // If current selection is valid (visible and enabled), do nothing
            if (selectedItem != null && selectedItem.Visibility == Visibility.Visible && selectedItem.IsEnabled)
            {
                return;
            }

            // If ManufacturerWMI is selected but collapsed, wait for Legion detection
            // Don't auto-select PawnIO - Legion detection will make WMI visible if it's a Legion device
            if (selectedItem != null && selectedItem == TdpMethodWmiItem && selectedItem.Visibility == Visibility.Collapsed)
            {
                Logger.Debug("EnsureValidTdpMethodSelected: ManufacturerWMI selected but collapsed, waiting for Legion detection");
                return;
            }

            // If selectedIndex is 0 (ManufacturerWMI position) but selectedItem isn't matching,
            // it means WMI was intended but may be collapsed - wait for Legion detection
            if (selectedIndex == 0 && TdpMethodWmiItem?.Visibility == Visibility.Collapsed)
            {
                Logger.Debug("EnsureValidTdpMethodSelected: SelectedIndex=0 (WMI) but WMI collapsed, waiting for Legion detection");
                return;
            }

            // If nothing is selected yet and WMI is collapsed, wait for Legion detection
            // This handles the case where the ComboBox rejected the initial Collapsed selection
            if (selectedItem == null && TdpMethodWmiItem?.Visibility == Visibility.Collapsed)
            {
                Logger.Debug("EnsureValidTdpMethodSelected: No selection and WMI collapsed, waiting for Legion detection");
                return;
            }

            // Find the first visible and enabled option and select it
            // Priority: ManufacturerWMI > PawnIO (if installed)
            // NEVER auto-select WinRing0 - it's a legacy option that may trigger anti-cheat
            if (TdpMethodWmiItem?.Visibility == Visibility.Visible && TdpMethodWmiItem.IsEnabled)
            {
                TdpMethodComboBox.SelectedItem = TdpMethodWmiItem;
                Logger.Info("TDP Method auto-selected: ManufacturerWMI");
            }
            else if (TdpMethodPawnIOItem?.Visibility == Visibility.Visible && TdpMethodPawnIOItem.IsEnabled)
            {
                TdpMethodComboBox.SelectedItem = TdpMethodPawnIOItem;
                Logger.Info("TDP Method auto-selected: PawnIO");
            }
            else
            {
                // Don't auto-select WinRing0 - user must explicitly choose it
                Logger.Warn("No safe TDP method available - user must select WinRing0 manually if desired");
            }
        }

        /// <summary>
        /// Updates the PawnIO install button state and dropdown option based on driver installation status.
        /// PawnIO option is always visible but disabled if not installed.
        /// </summary>
        private void UpdatePawnIOInstalledUI(bool installed)
        {
            // PawnIO option is always visible, but enable/disable based on installation status
            // This prevents WinRing0 from being auto-selected when PawnIO detection is delayed
            if (TdpMethodPawnIOItem != null)
            {
                // Keep PawnIO visible always - just update text to show status
                TdpMethodPawnIOItem.Visibility = Visibility.Visible;
                TdpMethodPawnIOItem.IsEnabled = installed;
                TdpMethodPawnIOItem.Content = installed ? "PawnIO" : "PawnIO (Not Installed)";
                Logger.Info($"PawnIO TDP option enabled: {installed}");

                // If PawnIO was selected but is no longer installed, switch to WMI only
                // NEVER auto-switch to WinRing0 - user must explicitly choose it
                if (!installed && TdpMethodComboBox != null)
                {
                    var selectedItem = TdpMethodComboBox.SelectedItem as ComboBoxItem;
                    if (selectedItem?.Tag is string tag && tag == "PawnIO")
                    {
                        // Try to select ManufacturerWMI, don't fall back to WinRing0
                        if (TdpMethodWmiItem?.Visibility == Visibility.Visible)
                        {
                            TdpMethodComboBox.SelectedItem = TdpMethodWmiItem;
                        }
                        // If WMI not available, leave selection as-is or clear it
                        // User will need to reinstall PawnIO or manually select WinRing0
                    }
                }
            }

            if (InstallPawnIOButton != null)
            {
                InstallPawnIOButton.Content = installed ? "Installed" : "Install";
                InstallPawnIOButton.IsEnabled = !installed;
                Logger.Info($"PawnIO install button updated: installed={installed}");

                // Update XY navigation to skip disabled button
                // TDPSettingsExpandButton.XYFocusUp must always point to SystemNavItem (for navigating out of card)
                if (TdpMethodComboBox != null && TDPSettingsExpandButton != null)
                {
                    TdpMethodComboBox.XYFocusDown = InstallPawnIOButton;
                    // Always allow navigating up from card header to nav bar
                    TDPSettingsExpandButton.XYFocusUp = SystemNavItem;
                }
            }

            if (PawnIOStatusText != null)
            {
                PawnIOStatusText.Text = installed ? "Installed" : "Not installed";
                PawnIOStatusText.Foreground = installed
                    ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)) // #6CCB5F - matches Quick Settings tile green
                    : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x07));
            }

            // Ensure a valid option is selected after visibility changes
            EnsureValidTdpMethodSelected();
        }

        /// <summary>
        /// Handles the PawnIO install button click.
        /// After installation, the helper restarts to reinitialize with PawnIO support.
        /// </summary>
        private async void InstallPawnIOButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("InstallPawnIOButton clicked - triggering PawnIO installation");

                // Update button to show installing state
                if (InstallPawnIOButton != null)
                {
                    InstallPawnIOButton.Content = "Installing...";
                    InstallPawnIOButton.IsEnabled = false;
                }

                // Trigger the installation via the property
                installPawnIO?.TriggerInstall();

                // Wait for helper to complete installation and exit
                // The helper exits after successful PawnIO installation
                Logger.Info("Waiting for PawnIO installation to complete...");
                await Task.Delay(5000);

                // Check if helper is still connected, if not, relaunch it
                // The helper will have exited after successful installation
                Logger.Info("Relaunching helper after PawnIO installation...");
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();

                // Wait for helper to start and reinitialize
                await Task.Delay(2000);
                Logger.Info("Helper relaunched after PawnIO installation");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during PawnIO installation: {ex.Message}");
                // Reset button state on error
                if (InstallPawnIOButton != null)
                {
                    InstallPawnIOButton.Content = "Install";
                    InstallPawnIOButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Labs Guide-remap prerequisite: usbip-win2 status + one-click install.
        /// Drives the repurposed ViGEmBusStatusText / ViGEmBusInstallButton pair
        /// in the Labs section (control names kept from the ViGEm era).
        /// </summary>
        private void UpdateLabsUsbipUI(bool installed)
        {
            if (ViGEmBusStatusText != null)
            {
                ViGEmBusStatusText.Text = installed ? "Status: Installed" : "Status: Not Installed";
                ViGEmBusStatusText.Foreground = installed
                    ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)) // #6CCB5F - matches Quick Settings tile green
                    : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
            }

            if (ViGEmBusInstallButton != null)
            {
                ViGEmBusInstallButton.Content = installed ? "Installed" : "Install usbip-win2";
                ViGEmBusInstallButton.IsEnabled = !installed;
            }

            // System tab -> Prerequisites card (same install trigger, different UI location).
            if (PrereqUsbipStatusText != null)
            {
                PrereqUsbipStatusText.Text = installed ? "Installed" : "Not installed";
                PrereqUsbipStatusText.Foreground = installed
                    ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)) // #6CCB5F - matches Quick Settings tile green
                    : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x07));
            }
            if (PrereqUsbipInstallButton != null)
            {
                PrereqUsbipInstallButton.Content = installed ? "Installed" : "Install";
                PrereqUsbipInstallButton.IsEnabled = !installed;
            }

            UpdateControllerEmulationPrereqGate();
        }

        /// <summary>
        /// System tab -> Prerequisites card usbip-win2 install button. Same trigger as the Labs
        /// card's button; both reflect the same underlying <see cref="usbipInstalled"/> property.
        /// </summary>
        private void PrereqUsbipInstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("Prereq usbip install button clicked - triggering usbip-win2 installation");
                installUsbip?.TriggerInstall();
                if (PrereqUsbipInstallButton != null)
                {
                    PrereqUsbipInstallButton.Content = "Installing...";
                    PrereqUsbipInstallButton.IsEnabled = false;
                }
                if (PrereqUsbipStatusText != null)
                {
                    PrereqUsbipStatusText.Text = "Installing...";
                }
                ScheduleInstallButtonTimeout(PrereqUsbipInstallButton, PrereqUsbipStatusText, "Install", "Install may have failed - check the log");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during usbip installation: {ex.Message}");
            }
        }

        private void LabsUsbipInstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("Labs usbip install button clicked - triggering usbip-win2 installation");
                installUsbip?.TriggerInstall();
                if (ViGEmBusInstallButton != null)
                {
                    ViGEmBusInstallButton.Content = "Installing...";
                    ViGEmBusInstallButton.IsEnabled = false;
                }
                if (ViGEmBusStatusText != null)
                {
                    ViGEmBusStatusText.Text = "Status: Installing...";
                }
                ScheduleInstallButtonTimeout(ViGEmBusInstallButton, ViGEmBusStatusText, "Install usbip-win2", "Status: Install may have failed - check the log");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during usbip installation: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the HidHide install button state based on installation status.
        /// </summary>
        private void UpdateHidHideInstalledUI(bool installed)
        {
            // System tab -> Prerequisites card (same install trigger, different UI location).
            if (PrereqHidHideStatusText != null)
            {
                PrereqHidHideStatusText.Text = installed ? "Installed" : "Not installed";
                PrereqHidHideStatusText.Foreground = installed
                    ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)) // #6CCB5F - matches Quick Settings tile green
                    : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x07));
            }
            if (PrereqHidHideInstallButton != null)
            {
                PrereqHidHideInstallButton.Content = installed ? "Installed" : "Install";
                PrereqHidHideInstallButton.IsEnabled = !installed;
            }

            Logger.Info($"HidHide install UI updated: installed={installed}");

            UpdateControllerEmulationPrereqGate();
        }

        private const string UsbipEverConfirmedInstalledKey = "UsbipEverConfirmedInstalled";
        private const string HidHideEverConfirmedInstalledKey = "HidHideEverConfirmedInstalled";

        /// <summary>
        /// Gates the whole Controller Emulation card behind usbip-win2 + HidHide being installed:
        /// while either is missing, the expand button is disabled (card can't be opened) and the
        /// "Enable Controller Emulation" toggle row is replaced by an inline warning with an
        /// Install button per missing component, so the fix is one click away instead of sending
        /// the user hunting through the System tab's Prerequisites card.
        ///
        /// The "confirmed installed" flags are persisted in LocalSettings, not just an in-memory
        /// field, and once true are NEVER re-checked against the live property again. Two reasons:
        /// (1) the helper's install-status check (registry/CLI-path lookup for HidHide, driver-list
        /// lookup for usbip-win2) is a pull-based re-check fired on every request rather than a
        /// stable cached value, and has been observed to blip "not installed" for a couple of
        /// seconds even when genuinely installed (2026-07-11, confirmed via widget + helper logs:
        /// HidHide flapped False -> True -> False -> True within ~2s of a fresh helper start, then
        /// stayed True for the rest of that session). (2) Game Bar can silently recreate this whole
        /// GamingWidget instance (documented elsewhere in this codebase - see the Quick Metrics and
        /// desktop-app-close fixes), which would reset a plain in-memory latch back to false on
        /// every close/reopen, right back into the same blip window - which is exactly what made
        /// the in-memory-only version of this fix look like clicking "Install" was doing something
        /// (it wasn't; the real status just happened to arrive around the same time).
        /// </summary>
        private void UpdateControllerEmulationPrereqGate()
        {
            bool usbipOk = GetBoolSetting(UsbipEverConfirmedInstalledKey, false);
            if (!usbipOk && usbipInstalled?.Value == true)
            {
                usbipOk = true;
                SetBoolSetting(UsbipEverConfirmedInstalledKey, true);
            }
            bool hidHideOk = GetBoolSetting(HidHideEverConfirmedInstalledKey, false);
            if (!hidHideOk && hidHideInstalled?.Value == true)
            {
                hidHideOk = true;
                SetBoolSetting(HidHideEverConfirmedInstalledKey, true);
            }
            bool prereqsMet = usbipOk && hidHideOk;

            if (ControllerEmulationEnableRow != null)
            {
                ControllerEmulationEnableRow.Visibility = prereqsMet ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ControllerEmulationPrereqWarning != null)
            {
                ControllerEmulationPrereqWarning.Visibility = prereqsMet ? Visibility.Collapsed : Visibility.Visible;
            }
            if (ControllerEmulationPrereqUsbipRow != null)
            {
                ControllerEmulationPrereqUsbipRow.Visibility = usbipOk ? Visibility.Collapsed : Visibility.Visible;
            }
            if (ControllerEmulationPrereqHidHideRow != null)
            {
                ControllerEmulationPrereqHidHideRow.Visibility = hidHideOk ? Visibility.Collapsed : Visibility.Visible;
            }

            if (ControllerEmulationExpandButton != null)
            {
                ControllerEmulationExpandButton.IsEnabled = prereqsMet;

                // Safety net: if a component was uninstalled while the card was open, force it
                // closed instead of leaving a now-unusable body expanded.
                if (!prereqsMet && ControllerEmulationExpandButton.IsChecked == true)
                {
                    ControllerEmulationExpandButton.IsChecked = false;
                    if (ControllerEmulationContent != null) ControllerEmulationContent.Visibility = Visibility.Collapsed;
                    if (ViiperEmulationContent != null) ViiperEmulationContent.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void ControllerEmulationPrereqUsbipButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("Controller Emulation prereq warning: usbip install button clicked");
                installUsbip?.TriggerInstall();
                if (ControllerEmulationPrereqUsbipButton != null)
                {
                    ControllerEmulationPrereqUsbipButton.Content = "Installing...";
                    ControllerEmulationPrereqUsbipButton.IsEnabled = false;
                }
                ScheduleInstallButtonTimeout(ControllerEmulationPrereqUsbipButton, null, "Install", null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during usbip installation: {ex.Message}");
            }
        }

        private void ControllerEmulationPrereqHidHideButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("Controller Emulation prereq warning: HidHide install button clicked");
                installHidHide?.TriggerInstall();
                if (ControllerEmulationPrereqHidHideButton != null)
                {
                    ControllerEmulationPrereqHidHideButton.Content = "Installing...";
                    ControllerEmulationPrereqHidHideButton.IsEnabled = false;
                }
                ScheduleInstallButtonTimeout(ControllerEmulationPrereqHidHideButton, null, "Install", null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during HidHide installation: {ex.Message}");
            }
        }

        /// <summary>
        /// System tab -> Prerequisites card HidHide install button. Same trigger as the Controller
        /// Emulation tab's button; both reflect the same underlying <see cref="hidHideInstalled"/> property.
        /// </summary>
        private void PrereqHidHideInstallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Info("Prereq HidHide install button clicked - triggering HidHide installation");

                if (PrereqHidHideInstallButton != null)
                {
                    PrereqHidHideInstallButton.Content = "Installing...";
                    PrereqHidHideInstallButton.IsEnabled = false;
                }
                if (PrereqHidHideStatusText != null)
                {
                    PrereqHidHideStatusText.Text = "Installing...";
                }

                installHidHide?.TriggerInstall();
                Logger.Info("HidHide installation triggered, waiting for helper response...");
                ScheduleInstallButtonTimeout(PrereqHidHideInstallButton, PrereqHidHideStatusText, "Install", "Install may have failed - check the log");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during HidHide installation: {ex.Message}");
                if (PrereqHidHideInstallButton != null)
                {
                    PrereqHidHideInstallButton.Content = "Install";
                    PrereqHidHideInstallButton.IsEnabled = true;
                }
                if (PrereqHidHideStatusText != null)
                {
                    PrereqHidHideStatusText.Text = "Error";
                }
            }
        }

        private const int PREREQ_INSTALL_TIMEOUT_MS = 45000;

        /// <summary>
        /// Safety net for the one-click usbip-win2/HidHide installers. GenericProperty.SetValue
        /// skips PropertyChanged whenever the pushed value equals the cached one (echo
        /// suppression) - so if an install attempt fails, the "still not installed" push never
        /// changes the value and the widget never gets notified, leaving the button stuck on
        /// "Installing..." forever with no recovery short of restarting the whole app. After a
        /// timeout, if the button still reads "Installing...", force it back so the user can see
        /// something went wrong and retry.
        /// </summary>
        private async void ScheduleInstallButtonTimeout(Button button, TextBlock statusText, string idleContent, string idleStatusText)
        {
            await Task.Delay(PREREQ_INSTALL_TIMEOUT_MS);
            if (button != null && (button.Content as string) == "Installing...")
            {
                Logger.Warn("Prerequisite install did not report completion within the timeout - resetting button (install may have failed; check the helper log)");
                button.Content = idleContent;
                button.IsEnabled = true;
                if (statusText != null)
                {
                    statusText.Text = idleStatusText;
                }
            }
        }

        /// <summary>
        /// Kept for callers that still toggle Custom-mode UI visibility — the three Legion-tab
        /// SPL/SPPL/FPPT cards have been removed (replaced by the Performance-tab master TDP slider
        /// + per-profile TDP Boost deltas), so this is now just a focus-chain bookkeeping shim:
        /// PerformanceMode dropdown navigates straight to the Fan controls in every mode.
        /// </summary>
        private void SetCustomTDPVisibility(bool visible)
        {
            // Show/hide the three Performance-tab SPL/SPPT/FPPT sliders for Custom mode.
            UpdateCustomTDPCardsVisibility();

            // Fan curve card stays fully interactive in every power mode now. Per-mode
            // storage means each mode has its own saved curve + EC-override unlock state,
            // and the user can edit any mode's slot from the dropdown without changing
            // the running power mode. The old Custom-only gate predated per-mode storage
            // and would mislead users into thinking fan control is only available in
            // Custom — keep it always enabled.
            if (LegionFanCurveCard != null)
            {
                LegionFanCurveCard.IsHitTestVisible = true;
                LegionFanCurveCard.Opacity = 1.0;
            }
            if (FanCurvePresetComboBox != null)
            {
                FanCurvePresetComboBox.IsEnabled = true;
            }
        }

        /// <summary>
        /// Toggles the ColorPicker visibility
        /// </summary>
        private void LegionColorExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LegionColorPicker != null)
                {
                    bool isExpanded = LegionColorPicker.Visibility == Visibility.Visible;
                    LegionColorPicker.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;

                    // Update button icon (chevron down/up)
                    if (LegionColorExpandButton != null)
                    {
                        LegionColorExpandButton.Content = isExpanded ? "\uE70D" : "\uE70E";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionColorExpandButton_Click: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles ColorPicker color changes and updates the preview
        /// </summary>
        private void LegionColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
        {
            try
            {
                // Update color preview
                if (LegionColorPreview != null)
                {
                    LegionColorPreview.Background = new SolidColorBrush(args.NewColor);
                }

                legionLightColor?.OnColorChanged(args.NewColor);

                // Save to controller profile (handler is detached during profile loading).
                // Debounced - ColorChanged fires continuously while dragging on the spectrum/
                // ring, same as a Slider's ValueChanged.
                ControllerSliderSettingChanged(sender, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionColorPicker_ColorChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles brightness slider changes
        /// </summary>
        private void LegionBrightnessSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (LegionBrightnessSlider != null && LegionBrightnessValue != null)
                {
                    int brightness = (int)LegionBrightnessSlider.Value;
                    LegionBrightnessValue.Text = $"{brightness}%";
                }
                // Save to controller profile
                ControllerSettingChanged(sender, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionBrightnessSlider_ValueChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles speed slider changes
        /// </summary>
        private void LegionSpeedSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (LegionSpeedSlider != null && LegionSpeedValue != null)
                {
                    int speed = (int)LegionSpeedSlider.Value;
                    LegionSpeedValue.Text = $"{speed}%";
                }

                // Save to controller profile (ControllerSettingChanged checks for loading state)
                ControllerSettingChanged(sender, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionSpeedSlider_ValueChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles light mode ComboBox selection - shows/hides appropriate controls
        /// Mode options visibility:
        /// - Off (0): hide all
        /// - Solid (1): Color + Brightness
        /// - Pulse (2): Color + Speed
        /// - Dynamic (3): Brightness + Speed
        /// - Spiral (4): Brightness + Speed
        /// </summary>
        private void LegionLightModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                UpdateLegionLightControlsVisibility();

                // Save to controller profile (handler is detached during profile loading)
                ControllerSettingChanged(sender, null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LegionLightModeComboBox_SelectionChanged: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the visibility of Legion light controls based on the selected mode
        /// </summary>
        private void UpdateLegionLightControlsVisibility()
        {
            if (LegionLightModeComboBox == null || LegionColorCard == null ||
                LegionBrightnessCard == null || LegionSpeedCard == null)
                return;

            int mode = LegionLightModeComboBox.SelectedIndex;

            // Off (0): hide all
            // Solid (1): Color + Brightness
            // Pulse (2): Color + Brightness + Speed
            // Dynamic (3): Brightness + Speed
            // Spiral (4): Brightness + Speed

            bool showColor = mode == 1 || mode == 2; // Solid, Pulse
            bool showBrightness = mode >= 1; // All modes except Off have brightness
            bool showSpeed = mode == 2 || mode == 3 || mode == 4; // Pulse, Dynamic, Spiral

            LegionColorCard.Visibility = showColor ? Visibility.Visible : Visibility.Collapsed;
            LegionBrightnessCard.Visibility = showBrightness ? Visibility.Visible : Visibility.Collapsed;
            LegionSpeedCard.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;

            Logger.Info($"Legion light mode {mode}: Color={showColor}, Brightness={showBrightness}, Speed={showSpeed}");
        }

        /// <summary>
        /// Handles TDP Mode ComboBox selection in Performance tab (Legion devices only)
        /// </summary>
        private int lastTDPModeIndex = 1; // Track last index to avoid redundant updates (init to XAML default: Balanced)
        private double savedCustomTDP = 15; // Saved custom TDP value when switching away from Custom mode
        private void TDPModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TDPModeComboBox == null) return;

            int selectedIndex = TDPModeComboBox.SelectedIndex;
            if (selectedIndex < 0) return;

            Logger.Debug($"TDPModeComboBox_SelectionChanged: selectedIndex={selectedIndex}, lastTDPModeIndex={lastTDPModeIndex}, isApplyingHelperUpdate={isApplyingHelperUpdate}, isLoadingProfile={isLoadingProfile}");

            // Block only during active pipe operations (sync/message handling) or profile loading.
            // Use isApplyingHelperUpdate instead of isInitialSync so user clicks work during the
            // init window (20-50s). Programmatic changes from pipe sync are blocked by isApplyingHelperUpdate,
            // and post-sync ComboBox updates are blocked by the equality check (lastTDPModeIndex set first).
            if (isApplyingHelperUpdate || isLoadingProfile)
            {
                lastTDPModeIndex = selectedIndex;
                return;
            }

            // Helper pushed a new performance mode: legionPerformanceMode updated this dropdown
            // directly (it now binds to TDPModeComboBox). Treat it like the old Legion-tab mirror
            // handler did — keep dependent UI in sync, but don't re-send to helper or save the profile.
            if (legionPerformanceMode?.IsUpdatingUI == true)
            {
                lastTDPModeIndex = selectedIndex;
                UpdateTDPSliderEnabledState();
                try { SyncFanCurvePresetComboToActiveMode(); }
                catch (Exception ex) { Logger.Debug($"SyncFanCurvePresetComboToActiveMode failed: {ex.Message}"); }
                return;
            }

            // Skip if this is the same index as last time (avoid redundant processing)
            if (selectedIndex == lastTDPModeIndex)
            {
                Logger.Debug($"TDPModeComboBox_SelectionChanged skipped: selectedIndex={selectedIndex} == lastTDPModeIndex={lastTDPModeIndex}");
                return;
            }

            // Check if switching away from Custom mode (to save slider value)
            // Use lastTDPModeIndex to check the PREVIOUS selection, not the new one
            bool wasCustomMode = lastTDPModeIndex >= 0 && IsCustomTdpModeIndex(lastTDPModeIndex);
            if (wasCustomMode && CustomTDPSlowSlider != null)
            {
                savedCustomTDP = CustomTDPSlowSlider.Value;
                Logger.Info($"Saved custom TDP (SPL) value: {savedCustomTDP}W before switching to preset mode");
            }

            lastTDPModeIndex = selectedIndex;

            // Map the selected dropdown index to TDP and Legion mode values
            bool isCustomMode = IsCustomTdpModeSelected();
            int presetTdpValue = GetCurrentPresetTdpValue();
            int legionModeValue = GetCurrentPresetLegionMode();

            Logger.Info($"TDP Mode selection changed to index {selectedIndex}: isCustom={isCustomMode}, presetTDP={presetTdpValue}W, legionMode={legionModeValue}");

            bool isLegion = legionGoDetected?.Value == true;

            if (isLegion)
            {
                // Legion device: use hardware presets via WMI
                // For built-in presets with LegionModeValue (1/2/3), use hardware mode
                // For custom presets (LegionModeValue == 255), use Custom mode + software TDP

                // Send Legion mode to helper - force send even if cached value matches
                if (legionPerformanceMode != null)
                {
                    if (legionPerformanceMode.Value == legionModeValue)
                    {
                        legionPerformanceMode.ForceSetValue(legionModeValue);
                    }
                    else
                    {
                        legionPerformanceMode.SetValue(legionModeValue);
                    }
                }

                // Legacy "custom preset" path (legionMode 255 but not the Custom item) — no longer
                // reachable in the fixed 4-mode model (255 ⟺ Custom), kept defensively.
                if (legionModeValue == 255 && !isCustomMode && presetTdpValue > 0)
                {
                    tdp?.SetValue(presetTdpValue);
                    Logger.Info($"Legion device: Applied custom preset TDP {presetTdpValue}W via software");
                }
                // For actual Custom mode on Legion the boost sliders keep their values; the
                // entering-Custom hardware apply runs in UpdateTDPSliderEnabledState below.
            }
            else
            {
                // Generic (non-Legion) device: apply the preset TDP via the Function.TDP wire path.
                // (No master slider UI in this build; Custom on non-Legion has no manual control.)
                if (!isCustomMode)
                {
                    int targetTDP = presetTdpValue > 0 ? presetTdpValue : 15;
                    if (tdp != null)
                    {
                        tdp.SetValue(targetTDP);
                        Logger.Info($"Generic device: Applied TDP preset {targetTDP}W (mode index {selectedIndex})");
                    }
                }
            }

            // Update Custom card visibility / display based on mode
            UpdateTDPSliderEnabledState();

            // Save profile when TDP Mode changes (if not during initialization or helper update)
            // Allow save if user-initiated from Quick Tab tile (bypasses isApplyingHelperUpdate)
            if (!isInitialSync && !isLoadingProfile && SaveTDP && (!isApplyingHelperUpdate || isUserInitiatedTDPModeChange))
            {
                // Don't save to game profile if per-game profile is disabled.
                // During game close, helper sends global mode via pipe → ComboBox changes → handler fires.
                // At this point, perGameProfile.Value is already false (pipe message processed) but
                // currentProfileName still points to the game profile (SwitchProfile hasn't run yet).
                // Without this check, the global mode gets saved to the game profile, corrupting it.
                bool isGameProfile = currentProfileName?.StartsWith("Game_") == true;
                if (isGameProfile && perGameProfile?.Value != true)
                {
                    Logger.Warn($"TDP Mode save skipped: per-game profile is disabled but currentProfileName is still '{currentProfileName}' (game closing)");
                }
                else
                {
                    Logger.Info($"Saving TDP Mode change to profile: {currentProfileName}");
                    SaveCurrentSettingsToProfile(currentProfileName);
                }
            }
            else
            {
                Logger.Warn($"TDP Mode save skipped: isInitialSync={isInitialSync}, isApplyingHelperUpdate={isApplyingHelperUpdate}, isLoadingProfile={isLoadingProfile}, SaveTDP={SaveTDP}, isUserInitiatedTDPModeChange={isUserInitiatedTDPModeChange}");
            }
        }

        /// <summary>
        /// Reacts to a TDP Mode change: shows/hides the Custom power-limit card, refreshes the live
        /// readout, (re)asserts the Custom limits on entering Custom, and fixes the XY focus chain.
        /// The master single TDP slider was removed — Custom is the only manual TDP control (Legion).
        /// </summary>
        private void UpdateTDPSliderEnabledState()
        {
            // Show/hide the Custom power-limit sliders based on mode/device.
            UpdateCustomTDPCardsVisibility();

            bool isCustomMode = IsCustomTdpModeSelected();
            bool isLegion = legionGoDetected?.Value == true;
            // Mode change came from helper pipe sync (not user interaction)?
            bool isHelperModeSync = legionPerformanceMode?.IsUpdatingUI == true;

            if (!isCustomMode)
            {
                // Preset mode: the Custom card is hidden. Keep the readout sane (shown only if the
                // card becomes visible again) and route focus straight past the hidden sliders.
                int modeIndex = TDPModeComboBox?.SelectedIndex ?? 1;
                string[] defaultModeNames = { "Quiet", "Balanced", "Performance" };
                string modeName = (modeIndex >= 0 && modeIndex < defaultModeNames.Length) ? defaultModeNames[modeIndex] : "Balanced";
                if (CurrentTDPValueText != null) CurrentTDPValueText.Text = $"{modeName} mode";

                if (TDPModeComboBox != null && OSPowerModeComboBox != null)
                {
                    TDPModeComboBox.XYFocusDown = OSPowerModeComboBox;
                    OSPowerModeComboBox.XYFocusUp = TDPModeComboBox;
                }
                Logger.Debug($"Custom card hidden - using {modeName} mode");
            }
            else
            {
                // Custom mode: pre-fill the live readout from the helper's last-known value (the
                // helper pushes the real SPL/SPPT/FPPT on the next CurrentTDP update), and refresh
                // the boost labels.
                if (CurrentTDPValueText != null)
                {
                    CurrentTDPValueText.Text = !string.IsNullOrEmpty(currentTdp?.Value) ? currentTdp.Value : "-- W";
                }
                UpdateCustomTDPBoostRanges();
                UpdateCustomTDPValueLabels();

                if (isLegion)
                {
                    // The TDP / SPPT Boost / FPPT Boost sliders own the limits (Lenovo WMI). Force a
                    // push on entering Custom so the hardware matches the sliders even if the triplet
                    // equals the last push (e.g. Performance↔Custom round-trip). Skip during helper
                    // sync / profile load (those drive their own values).
                    if (!isLoadingProfile && !isHelperModeSync)
                    {
                        ApplyCustomTDPSlidersToHelper(force: true);
                    }
                }

                // Focus chain in Custom mode: TDPMode -> Custom sliders -> OSPowerMode (the slider-to-
                // slider links are wired in XAML).
                if (TDPModeComboBox != null && OSPowerModeComboBox != null
                    && CustomTDPSlowSlider != null && CustomTDPPeakSlider != null)
                {
                    TDPModeComboBox.XYFocusDown = CustomTDPSlowSlider;
                    OSPowerModeComboBox.XYFocusUp = CustomTDPPeakSlider;
                }
            }
        }

        // ===== Custom Power Limits (Performance tab, Legion-only, Custom mode) =====
        //
        // UI model: three sliders — TDP (base SPL, 5-50W), SPPT Boost (+0..+10W) and FPPT Boost
        // (+0..+15W). The effective limits are SPPT = TDP + SPPT Boost and FPPT = TDP + FPPT Boost,
        // with the invariant SPPT <= FPPT enforced (i.e. SPPT Boost <= FPPT Boost) and both effective
        // limits clamped to CustomTDPCeilingW. Those absolute SPL/SPPT/FPPT values are what we push
        // to the helper (live, via WMI) and persist per profile.

        // Guards re-entrant ValueChanged logic while we set slider values programmatically
        // (profile load, boost clamping). Distinct from the WidgetSliderProperty.IsUpdatingUI flag,
        // which no longer applies now that these sliders are plain (unbound) controls.
        private bool isUpdatingCustomTDPSliders;

        // Last absolute (SPL, SPPT, FPPT) we pushed to the helper, to skip redundant WMI writes
        // while dragging. (-1, -1, -1) means "nothing sent yet".
        private (int spl, int sppt, int fppt) lastSentCustomTDP = (-1, -1, -1);

        // Hard ceiling for the effective Custom power limits (W). The base TDP (SPL) slider runs up
        // to this value; the SPPT/FPPT boosts can never push the EFFECTIVE limit past it — e.g.
        // TDP=50 + SPPT Boost=+10 + FPPT Boost=+15 still clamps to 50/50/50, not 50/60/65.
        private const int CustomTDPCeilingW = 50;

        /// <summary>
        /// Shows the TDP / SPPT Boost / FPPT Boost sliders (and hides the master single slider) only
        /// on a Legion device in Custom mode. In every other case the master "TDP Power Limit" card
        /// is shown instead.
        /// </summary>
        private void UpdateCustomTDPCardsVisibility()
        {
            bool isLegion = legionGoDetected?.Value == true;
            bool isCustom = IsCustomTdpModeSelected();
            bool showCustom = isLegion && isCustom;

            if (CustomTDPCard != null)
            {
                CustomTDPCard.Visibility = showCustom ? Visibility.Visible : Visibility.Collapsed;
                if (showCustom) UpdateCustomTDPValueLabels();
            }

            if (AutoTDPCard != null)
            {
                AutoTDPCard.Visibility = showCustom ? Visibility.Visible : Visibility.Collapsed;
                UpdateCustomTDPSlidersEnabledForAutoTDP();
            }
        }

        /// <summary>
        /// Reads the TDP / SPPT Boost / FPPT Boost sliders and returns the absolute SPL/SPPT/FPPT
        /// limits, enforcing the SPPT &lt;= FPPT invariant.
        /// </summary>
        private (int spl, int sppt, int fppt) ComputeCustomTDPAbsolute()
        {
            int spl = CustomTDPSlowSlider != null ? (int)CustomTDPSlowSlider.Value : 15;
            int spptBoost = CustomTDPFastSlider != null ? (int)CustomTDPFastSlider.Value : 0;
            int fpptBoost = CustomTDPPeakSlider != null ? (int)CustomTDPPeakSlider.Value : 0;
            if (spptBoost > fpptBoost) spptBoost = fpptBoost; // SPPT can never exceed FPPT
            // Safety net: the boost slider maxima are already capped to the ceiling headroom by
            // UpdateCustomTDPBoostRanges (so spl + boost <= CustomTDPCeilingW normally), but clamp
            // here too in case a slider was set out of range. min() preserves SPPT <= FPPT.
            int sppt = Math.Min(spl + spptBoost, CustomTDPCeilingW);
            int fppt = Math.Min(spl + fpptBoost, CustomTDPCeilingW);
            return (spl, sppt, fppt);
        }

        /// <summary>
        /// Pushes the current absolute Custom power limits to the helper (which writes them straight
        /// through Lenovo WMI). Applied live on every slider change so the limits track the sliders
        /// in real time. Redundant writes (unchanged triplet) are skipped while dragging.
        /// </summary>
        /// <param name="force">When true, send all three even if the values match the last push
        /// (used when (re)entering Custom mode so the hardware is guaranteed to match the sliders).</param>
        private void ApplyCustomTDPSlidersToHelper(bool force = false)
        {
            var (spl, sppt, fppt) = ComputeCustomTDPAbsolute();
            var prev = lastSentCustomTDP;
            if (!force && prev == (spl, sppt, fppt)) return;
            lastSentCustomTDP = (spl, sppt, fppt);

            // Per-limit dedup: each ForceSetValue is a separate ~300ms WMI write on the helper, so
            // only push the limits that actually changed. A boost-slider drag then costs ONE write
            // (just SPPT or FPPT); only a TDP (SPL) drag moves all three.
            if (force || spl != prev.spl) legionCustomTDPSlow?.ForceSetValue(spl);
            if (force || sppt != prev.sppt) legionCustomTDPFast?.ForceSetValue(sppt);
            if (force || fppt != prev.fppt) legionCustomTDPPeak?.ForceSetValue(fppt);
            Logger.Info($"Applied Custom power limits to helper: SPL={spl}W, SPPT={sppt}W, FPPT={fppt}W (force={force})");
        }

        // ===== Live-apply throttle =====
        // Slider drags fire ValueChanged on every integer tick. Each push is up to three serialized
        // ~300ms WMI writes on the helper, so applying on every tick floods the pipe and the hardware
        // lags seconds behind a fast drag. Throttle to a fixed cadence with leading + trailing edges:
        // the first move applies immediately (responsive), further moves within the window coalesce
        // and re-apply at each window boundary, and the final value always lands within one window of
        // release. ~250ms ≈ the helper's worst-case triplet write time, so the queue can't grow.
        private const int CUSTOM_TDP_THROTTLE_MS = 250;
        private Windows.UI.Xaml.DispatcherTimer customTDPThrottleTimer;
        private bool customTDPApplyPending;

        private void RequestCustomTDPApply()
        {
            if (customTDPThrottleTimer == null)
            {
                customTDPThrottleTimer = new Windows.UI.Xaml.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(CUSTOM_TDP_THROTTLE_MS)
                };
                customTDPThrottleTimer.Tick += CustomTDPThrottleTimer_Tick;
            }

            if (!customTDPThrottleTimer.IsEnabled)
            {
                // Leading edge: apply immediately and open the throttle window.
                customTDPApplyPending = false;
                ApplyCustomTDPSlidersToHelper();
                customTDPThrottleTimer.Start();
            }
            else
            {
                // Within the window: coalesce; the latest value goes out at the next boundary.
                customTDPApplyPending = true;
            }
        }

        private void CustomTDPThrottleTimer_Tick(object sender, object e)
        {
            if (customTDPApplyPending)
            {
                customTDPApplyPending = false;
                ApplyCustomTDPSlidersToHelper();
                // Keep the window open so continued dragging stays throttled.
            }
            else
            {
                customTDPThrottleTimer.Stop();
            }
        }

        /// <summary>
        /// Sets the three Custom sliders (and their value labels) from a profile's absolute
        /// SPL/SPPT/FPPT without triggering a send to the helper. SPPT/FPPT are converted back to
        /// boosts (clamped to the slider ranges). Used during profile load; the hardware apply is
        /// driven separately by the Legion mode-apply flow.
        /// </summary>
        private void SetCustomTDPSlidersSilent(double spl, double sppt, double fppt)
        {
            int splV = (int)spl;
            int spptBoost = (int)Math.Round(sppt - spl);
            int fpptBoost = (int)Math.Round(fppt - spl);
            if (fpptBoost < spptBoost) fpptBoost = spptBoost; // keep the invariant on load too

            isUpdatingCustomTDPSliders = true;
            try
            {
                SetOneSliderClamped(CustomTDPSlowSlider, splV);
                // Cap the boost maxima to the loaded SPL's headroom BEFORE placing the boost values,
                // so they clamp into the valid range (effective <= 50 W).
                UpdateCustomTDPBoostRanges();
                SetOneSliderClamped(CustomTDPPeakSlider, fpptBoost); // set FPPT first so SPPT clamps against it
                SetOneSliderClamped(CustomTDPFastSlider, spptBoost);
            }
            finally
            {
                isUpdatingCustomTDPSliders = false;
            }

            UpdateCustomTDPValueLabels();
            // Keep the wire-channel cache aligned with what we loaded so the next live push isn't
            // skipped as "unchanged" against a stale triplet, and the OSD/tile read the right SPL.
            var (a, b, c) = ComputeCustomTDPAbsolute();
            lastSentCustomTDP = (a, b, c);
            legionCustomTDPSlow?.SetValueSilent(a);
            legionCustomTDPFast?.SetValueSilent(b);
            legionCustomTDPPeak?.SetValueSilent(c);
        }

        private static void SetOneSliderClamped(Windows.UI.Xaml.Controls.Slider slider, int value)
        {
            if (slider == null) return;
            if (value < slider.Minimum) value = (int)slider.Minimum;
            if (value > slider.Maximum) value = (int)slider.Maximum;
            slider.Value = value;
        }

        /// <summary>
        /// Refreshes the three value labels to show the base TDP and the effective (absolute)
        /// SPPT/FPPT next to their boosts.
        /// </summary>
        private void UpdateCustomTDPValueLabels()
        {
            var (spl, sppt, fppt) = ComputeCustomTDPAbsolute();
            if (CustomTDPSlowValue != null) CustomTDPSlowValue.Text = $"{spl} W";
            if (CustomTDPFastValue != null) CustomTDPFastValue.Text = $"+{sppt - spl} W  (= {sppt} W)";
            if (CustomTDPPeakValue != null) CustomTDPPeakValue.Text = $"+{fppt - spl} W  (= {fppt} W)";
        }

        /// <summary>
        /// Caps the SPPT/FPPT Boost slider maximums to the headroom under the 50 W ceiling
        /// (headroom = CustomTDPCeilingW - SPL). At SPL=50 both boosts cap at 0; at SPL ≤ 35 they
        /// keep their full +10 / +15 ranges. This keeps the slider position equal to the effective
        /// boost — no slider/label mismatch and no save→reload drift — while guaranteeing the
        /// effective SPPT/FPPT can never exceed the ceiling. Preserves SPPT Boost ≤ FPPT Boost.
        /// </summary>
        private void UpdateCustomTDPBoostRanges()
        {
            if (CustomTDPSlowSlider == null) return;
            int spl = (int)CustomTDPSlowSlider.Value;
            int headroom = Math.Max(0, CustomTDPCeilingW - spl);

            // Suppress re-entrant ValueChanged: lowering Maximum below a slider's Value makes WinUI
            // clamp the Value, which would otherwise fire a live apply mid-adjustment.
            bool prev = isUpdatingCustomTDPSliders;
            isUpdatingCustomTDPSliders = true;
            try
            {
                if (CustomTDPPeakSlider != null) CustomTDPPeakSlider.Maximum = Math.Min(15, headroom); // FPPT first
                if (CustomTDPFastSlider != null) CustomTDPFastSlider.Maximum = Math.Min(10, headroom); // SPPT
                // Re-assert SPPT Boost ≤ FPPT Boost after the maxima (and thus values) shrank.
                if (CustomTDPFastSlider != null && CustomTDPPeakSlider != null
                    && CustomTDPFastSlider.Value > CustomTDPPeakSlider.Value)
                {
                    CustomTDPFastSlider.Value = CustomTDPPeakSlider.Value;
                }
            }
            finally
            {
                isUpdatingCustomTDPSliders = prev;
            }
        }

        private void CustomTDPSlowSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isUpdatingCustomTDPSliders) return;
            // SPL changed → adjust the boost headroom (keeps effective SPPT/FPPT <= 50 W), then the
            // effective SPPT/FPPT (SPL + boost) move with it. Refresh labels, push all three live
            // and persist.
            UpdateCustomTDPBoostRanges();
            UpdateCustomTDPValueLabels();
            OnCustomTDPSliderChanged();
            // SPL drives the Quick-tab "Custom (NW)" tile — refresh it so the tile tracks the
            // sustained limit live instead of the stale master slider.
            UpdateQuickSettingsTileStates();
        }

        private void CustomTDPFastSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isUpdatingCustomTDPSliders) return;
            // Raising SPPT Boost above FPPT Boost drags FPPT Boost UP to match (keeps SPPT <= FPPT).
            // FPPT Boost's max is always >= SPPT Boost's max (same SPL headroom, higher static cap),
            // so FPPT can match any value SPPT is allowed to reach.
            if (CustomTDPPeakSlider != null && e.NewValue > CustomTDPPeakSlider.Value)
            {
                isUpdatingCustomTDPSliders = true;
                try
                {
                    CustomTDPPeakSlider.Value = e.NewValue;
                    // Safety net: if FPPT got clamped by its own Maximum below SPPT, pull SPPT back down.
                    if (CustomTDPPeakSlider.Value < CustomTDPFastSlider.Value)
                        CustomTDPFastSlider.Value = CustomTDPPeakSlider.Value;
                }
                finally { isUpdatingCustomTDPSliders = false; }
            }
            UpdateCustomTDPValueLabels();
            OnCustomTDPSliderChanged();
        }

        private void CustomTDPPeakSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isUpdatingCustomTDPSliders) return;
            // Lowering FPPT Boost below SPPT Boost drags SPPT Boost down with it (keeps SPPT <= FPPT).
            if (CustomTDPFastSlider != null && e.NewValue < CustomTDPFastSlider.Value)
            {
                isUpdatingCustomTDPSliders = true;
                try { CustomTDPFastSlider.Value = e.NewValue; }
                finally { isUpdatingCustomTDPSliders = false; }
            }
            UpdateCustomTDPValueLabels();
            OnCustomTDPSliderChanged();
        }

        /// <summary>
        /// Applies the Custom power limits to the helper live (via WMI) and persists them to the
        /// current profile after a user-driven slider change. Skipped during helper sync / profile
        /// loads (which set the sliders themselves).
        /// </summary>
        private void OnCustomTDPSliderChanged()
        {
            if (isApplyingHelperUpdate || isLoadingProfile || isInitialSync) return;
            if (WidgetSliderProperty.HelperSyncCount > 0) return;

            // Live hardware apply (only meaningful on a Legion in Custom mode; the helper ignores
            // these writes outside Custom mode anyway). Throttled so a fast drag doesn't flood the
            // pipe / WMI — see RequestCustomTDPApply.
            if (legionGoDetected?.Value == true && IsCustomTdpModeSelected())
            {
                RequestCustomTDPApply();
            }

            // Profile-storage save is a separate, much heavier operation (a full ~24-key
            // ApplicationData.LocalSettings write) than the hardware apply above, and unlike it
            // was never debounced - a full SPL drag (5-50W, 45 ticks) used to fire ~45 full
            // profile writes in under a second. Debounce it on its own timer, independent of the
            // already-throttled RequestCustomTDPApply.
            if (customTDPSaveDebounceTimer == null)
            {
                customTDPSaveDebounceTimer = new DispatcherTimer();
                customTDPSaveDebounceTimer.Interval = TimeSpan.FromMilliseconds(CUSTOM_TDP_SAVE_DEBOUNCE_MS);
                customTDPSaveDebounceTimer.Tick += CustomTDPSaveDebounceTimer_Tick;
            }
            customTDPSaveDebounceTimer.Stop();
            customTDPSaveDebounceTimer.Start();
        }

        private void CustomTDPSaveDebounceTimer_Tick(object sender, object e)
        {
            customTDPSaveDebounceTimer?.Stop();
            FlushPendingCustomTDPSave();
        }

        /// <summary>
        /// Performs the debounced Custom TDP profile save immediately (same guards as the debounce
        /// timer tick). Shared by the timer and by <see cref="FlushPendingProfileSaves"/> so a
        /// pending change isn't silently lost if the widget instance is torn down before the timer
        /// fires - Game Bar can recreate GamingWidget at any time (see App.xaml.cs OnSuspending).
        /// </summary>
        private void FlushPendingCustomTDPSave()
        {
            // Re-check the same guards - state may have changed during the debounce wait.
            if (isApplyingHelperUpdate || isLoadingProfile || isInitialSync) return;
            if (WidgetSliderProperty.HelperSyncCount > 0) return;
            if (!SaveTDP) return;

            bool isGameProfile = currentProfileName?.StartsWith("Game_") == true;
            if (isGameProfile && perGameProfile?.Value != true) return;

            SaveCurrentSettingsToProfile(currentProfileName);
        }

        /// <summary>
        /// Flushes any pending debounced profile-save timers immediately. Called from
        /// App.xaml.cs's OnSuspending right before the GamingWidget instance is discarded, so a
        /// slider change made just before Game Bar suspends/recreates the widget still gets
        /// persisted instead of silently reverting to the previous saved value.
        /// </summary>
        internal void FlushPendingProfileSaves()
        {
            if (customTDPSaveDebounceTimer != null && customTDPSaveDebounceTimer.IsEnabled)
            {
                customTDPSaveDebounceTimer.Stop();
                FlushPendingCustomTDPSave();
            }
        }

    }
}
