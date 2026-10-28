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

        private void SubscribeToSettingsChanges()
        {
            // Performance settings
            // (Master TDP slider removed — the Custom power-limit sliders self-save via their own
            //  ValueChanged handlers → OnCustomTDPSliderChanged → SaveCurrentSettingsToProfile.)
            CPUBoostToggle.Toggled += SettingChanged;
            CPUEPPSlider.ValueChanged += SettingChangedDebounced;
            MinCPUStateComboBox.SelectionChanged += SettingChanged;
            MaxCPUStateComboBox.SelectionChanged += SettingChanged;
            FPSLimitToggle.Toggled += FPSLimitToggle_Toggled;
            FPSLimitSlider.ValueChanged += FPSLimitSlider_ValueChanged;

            // Legion controller button mapping settings
            InitializeButtonMappingEvents("Y1");
            InitializeButtonMappingEvents("Y2");
            InitializeButtonMappingEvents("Y3");
            InitializeButtonMappingEvents("M1");
            InitializeButtonMappingEvents("M2");
            InitializeButtonMappingEvents("M3");
            InitializeButtonMappingEvents("Desktop");
            InitializeButtonMappingEvents("Page");

            if (LegionDesktopControlsToggle != null)
                LegionDesktopControlsToggle.Toggled += LegionDesktopControls_Toggled;
            if (LegionVibrationComboBox != null)
                LegionVibrationComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionVibrationModeComboBox != null)
                LegionVibrationModeComboBox.SelectionChanged += ControllerSettingChanged;

            // Gyro settings (per-game profile)
            if (LegionGyroTargetComboBox != null)
                LegionGyroTargetComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionGyroSensitivityXSlider != null)
                LegionGyroSensitivityXSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionGyroSensitivityYSlider != null)
                LegionGyroSensitivityYSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionGyroInvertXToggle != null)
                LegionGyroInvertXToggle.Toggled += ControllerSettingChanged;
            if (LegionGyroInvertYToggle != null)
                LegionGyroInvertYToggle.Toggled += ControllerSettingChanged;
            if (LegionGyroMappingTypeComboBox != null)
                LegionGyroMappingTypeComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionGyroActivationModeComboBox != null)
                LegionGyroActivationModeComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionGyroActivationButtonComboBox != null)
                LegionGyroActivationButtonComboBox.SelectionChanged += ControllerSettingChanged;

            // Advanced gyro settings (per-game profile)
            if (LegionGyroDeadzoneSlider != null)
                LegionGyroDeadzoneSlider.ValueChanged += ControllerSliderSettingChanged;

            // Stick deadzones (per-game profile)
            if (LegionLeftStickDeadzoneSlider != null)
                LegionLeftStickDeadzoneSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionRightStickDeadzoneSlider != null)
                LegionRightStickDeadzoneSlider.ValueChanged += ControllerSliderSettingChanged;

            // Trigger travel (per-game profile)
            if (LegionLeftTriggerStartSlider != null)
                LegionLeftTriggerStartSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionLeftTriggerEndSlider != null)
                LegionLeftTriggerEndSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionRightTriggerStartSlider != null)
                LegionRightTriggerStartSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionRightTriggerEndSlider != null)
                LegionRightTriggerEndSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionHairTriggersToggle != null)
                LegionHairTriggersToggle.Toggled += LegionHairTriggers_Toggled;

            // Joystick as mouse (per-game profile)
            if (LegionJoystickAsMouseComboBox != null)
                LegionJoystickAsMouseComboBox.SelectionChanged += ControllerSettingChanged;
            if (LegionJoystickMouseSensSlider != null)
                LegionJoystickMouseSensSlider.ValueChanged += ControllerSliderSettingChanged;

            // Lighting settings (per-game profile)
            if (LegionPowerLightToggle != null)
                LegionPowerLightToggle.Toggled += ControllerSettingChanged;
            if (LegionLightModeComboBox != null)
                LegionLightModeComboBox.SelectionChanged += ControllerSettingChanged;
            // LegionColorPicker's save/send is triggered from LegionColorPicker_ColorChanged
            // (GamingWidget.LegionGo.cs), which also updates the preview swatch - no separate
            // subscription here (a second one used to double-fire ControllerSettingChanged on
            // every ColorChanged tick, which itself fires continuously while dragging).
            if (LegionBrightnessSlider != null)
                LegionBrightnessSlider.ValueChanged += ControllerSliderSettingChanged;
            if (LegionSpeedSlider != null)
                LegionSpeedSlider.ValueChanged += ControllerSliderSettingChanged;

            foreach (string buttonName in LegionRemapButtonNames)
            {
                UpdateButtonGamepadComboControls(buttonName);
            }
        }

        private void SettingChanged(object sender, object e)
        {
            // Don't save during profile loading, switching, initial sync, when helper is updating values,
            // or when any property is syncing from helper pipe
            if (isLoadingProfile || isSwitchingProfile || isApplyingHelperUpdate || isInitialSync
                || WidgetSliderProperty.HelperSyncCount > 0)
            {
                Logger.Debug($"Skipping auto-save during profile operation (loading={isLoadingProfile}, switching={isSwitchingProfile}, helperUpdate={isApplyingHelperUpdate}, initialSync={isInitialSync})");
                return;
            }

            // Auto-save to current profile
            SaveCurrentSettingsToProfile(currentProfileName);
        }

        /// <summary>
        /// Debounced entry point for slider ValueChanged events (CPU EPP, AMD sharpness/
        /// resolution/FPS sliders). Raw ValueChanged fires continuously while the thumb moves,
        /// and SettingChanged does a synchronous full-profile write on the UI thread - without
        /// this, dragging one of these sliders end-to-end fires dozens of writes in under a
        /// second. Coalesces to a single SettingChanged call ~300ms after the drag settles,
        /// matching the FPSLimitSlider debounce pattern (GamingWidget.QuickSettings.Actions.cs).
        /// </summary>
        private void SettingChangedDebounced(object sender, object e)
        {
            if (isLoadingProfile || isSwitchingProfile || isApplyingHelperUpdate || isInitialSync
                || WidgetSliderProperty.HelperSyncCount > 0)
            {
                return;
            }

            if (settingsSaveDebounceTimer == null)
            {
                settingsSaveDebounceTimer = new DispatcherTimer();
                settingsSaveDebounceTimer.Interval = TimeSpan.FromMilliseconds(SETTINGS_SAVE_DEBOUNCE_MS);
                settingsSaveDebounceTimer.Tick += SettingsSaveDebounceTimer_Tick;
            }

            settingsSaveDebounceTimer.Stop();
            settingsSaveDebounceTimer.Start();
        }

        private void SettingsSaveDebounceTimer_Tick(object sender, object e)
        {
            settingsSaveDebounceTimer?.Stop();
            // Re-checks the same guards - state may have changed during the debounce wait.
            SettingChanged(sender, e);
        }

    }
}
