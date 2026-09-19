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

        private void PerformanceOverlayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (PerformanceOverlayToggle == null) return;
            // Both fire while we're programmatically reflecting a load/helper-pushed value into
            // the toggle - skip re-sending/re-saving what we just received.
            if (isLoadingPerformanceOverlaySetting || isApplyingHelperUpdate) return;

            bool isOn = PerformanceOverlayToggle.IsOn;

            if (osdProvider == 1) // AMD
            {
                // AMD's overlay is a separate driver-level system with its own on/off + 4-level
                // cycle (Ctrl+Shift+O / Ctrl+Shift+X) - this toggle can only turn it fully on/off.
                if (!isOn && amdOverlayLevel > 0)
                {
                    SendAMDOverlayToggle();
                    amdOverlayLevel = 0;
                    SaveAMDOverlayLevel();
                    Logger.Info("AMD Overlay toggled OFF");
                }
                else if (isOn && amdOverlayLevel == 0)
                {
                    SendAMDOverlayToggle();
                    amdOverlayLevel = 1;
                    SaveAMDOverlayLevel();
                    Logger.Info("AMD Overlay toggled ON");
                }
                UpdateQuickSettingsTileStates();
            }
            else // RTSS
            {
                osd?.SetValue(isOn ? 1 : 0);
            }

            SavePerformanceOverlaySetting();

            // Also update current profile's OverlayLevel if SaveOverlayLevel is enabled
            // This ensures the profile stays in sync with the user's selection
            if (SaveOverlayLevel && !string.IsNullOrEmpty(currentProfileName))
            {
                var profile = GetProfile(currentProfileName);
                if (profile != null)
                {
                    profile.OverlayLevel = isOn ? 1 : 0;
                    SaveProfileToStorage(currentProfileName, profile);
                    Logger.Debug($"Updated profile '{currentProfileName}' OverlayLevel to {(isOn ? 1 : 0)}");
                }
            }
        }

        private void LoadPerformanceOverlaySetting()
        {
            try
            {
                if (PerformanceOverlayToggle == null) return;
                isLoadingPerformanceOverlaySetting = true;
                var settings = ApplicationData.Current.LocalSettings;
                // Backward-compatible with the old 0-3 (Off/Basic/Detailed/Full) level value -
                // anything above 0 now just means "on".
                if (settings.Values.TryGetValue("PerformanceOverlayLevel", out object val) && val is int level)
                {
                    bool isOn = level > 0;
                    PerformanceOverlayToggle.IsOn = isOn;
                    // Also set the osd property value directly to avoid debounce delay
                    // This ensures Quick Settings and helper have the correct value immediately
                    osd?.SetValue(isOn ? 1 : 0);
                    Logger.Debug($"Loaded PerformanceOverlay state: {(isOn ? "On" : "Off")}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading PerformanceOverlay setting: {ex.Message}");
            }
            finally
            {
                isLoadingPerformanceOverlaySetting = false;
            }
        }

        private void SavePerformanceOverlaySetting()
        {
            try
            {
                if (PerformanceOverlayToggle == null) return;
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values["PerformanceOverlayLevel"] = PerformanceOverlayToggle.IsOn ? 1 : 0;
                Logger.Debug($"Saved PerformanceOverlay state: {(PerformanceOverlayToggle.IsOn ? "On" : "Off")}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving PerformanceOverlay setting: {ex.Message}");
            }
        }

        private void SaveAMDOverlayLevel()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values["AMD_OverlayLevel"] = amdOverlayLevel;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving AMD overlay level: {ex.Message}");
            }
        }

        /// <summary>
        /// The `osd` property is a <see cref="XboxGamingBar.Data.OSDProperty"/>, which (like all
        /// WidgetSliderProperty-backed properties) needs a real Slider control to bind to - it's
        /// the hidden <c>PerformanceOverlaySlider</c> in XAML, never shown to the user. This
        /// mirrors any externally-driven change (helper push, profile switch) from that slider
        /// into the visible toggle.
        /// </summary>
        private void PerformanceOverlaySlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (PerformanceOverlayToggle == null) return;

            bool isOn = e.NewValue > 0;
            if (PerformanceOverlayToggle.IsOn == isOn) return;

            isApplyingHelperUpdate = true;
            try
            {
                PerformanceOverlayToggle.IsOn = isOn;
            }
            finally
            {
                isApplyingHelperUpdate = false;
            }
        }

    }
}
