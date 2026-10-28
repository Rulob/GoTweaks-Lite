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

        private void RegisterCardFocusHandlers()
        {
            // Get brushes from resources
            cardDefaultBorderBrush = (SolidColorBrush)Resources["CardBorderBrush"];
            cardFocusBorderBrush = (SolidColorBrush)Resources["CardFocusBorderBrush"];

            // Register focus handler on navigation items to clear card focus when tabs get focus
            foreach (var item in MainNavPanel.Children)
            {
                if (item is RadioButton radioButton)
                {
                    radioButton.GotFocus += NavItem_GotFocus;
                }
            }

            // Quick tab - optional built-in brightness slider + its Customize toggle
            PanelBrightnessSlider.GotFocus += Control_GotFocus;
            PanelBrightnessSlider.LostFocus += Control_LostFocus;
            PanelBrightnessToggle.GotFocus += StandaloneControl_GotFocus;
            QuickMetricsToggle.GotFocus += StandaloneControl_GotFocus;

            // Register GotFocus/LostFocus on interactive controls
            // Performance tab - Active Profile card
            PerGameProfileToggle.GotFocus += Control_GotFocus;
            PerGameProfileToggle.LostFocus += Control_LostFocus;

            // Performance tab - Performance Overlay card
            PerformanceOverlayToggle.GotFocus += Control_GotFocus;
            PerformanceOverlayToggle.LostFocus += Control_LostFocus;

            // Performance tab - TDP Mode card (Legion only)
            TDPModeComboBox.GotFocus += Control_GotFocus;
            TDPModeComboBox.LostFocus += Control_LostFocus;

            // Performance tab - Custom power-limit sliders (master TDP slider removed)
            CustomTDPSlowSlider.GotFocus += Control_GotFocus;
            CustomTDPSlowSlider.LostFocus += Control_LostFocus;
            CustomTDPFastSlider.GotFocus += Control_GotFocus;
            CustomTDPFastSlider.LostFocus += Control_LostFocus;
            CustomTDPPeakSlider.GotFocus += Control_GotFocus;
            CustomTDPPeakSlider.LostFocus += Control_LostFocus;

            // Performance tab - AutoTDP card
            AutoTDPToggle.GotFocus += Control_GotFocus;
            AutoTDPToggle.LostFocus += Control_LostFocus;
            AutoTDPTargetFPSSlider.GotFocus += Control_GotFocus;
            AutoTDPTargetFPSSlider.LostFocus += Control_LostFocus;
            AutoTDPMinSlider.GotFocus += Control_GotFocus;
            AutoTDPMinSlider.LostFocus += Control_LostFocus;
            AutoTDPMaxSlider.GotFocus += Control_GotFocus;
            AutoTDPMaxSlider.LostFocus += Control_LostFocus;
            AutoTDPPauseWhenUnfocusedToggle.GotFocus += Control_GotFocus;
            AutoTDPPauseWhenUnfocusedToggle.LostFocus += Control_LostFocus;

            // Performance tab - CPU Boost card
            CPUBoostToggle.GotFocus += Control_GotFocus;
            CPUBoostToggle.LostFocus += Control_LostFocus;

            // Performance tab - CPU EPP card
            CPUEPPSlider.GotFocus += Control_GotFocus;
            CPUEPPSlider.LostFocus += Control_LostFocus;

            // Performance tab - CPU State card
            MinCPUStateComboBox.GotFocus += Control_GotFocus;
            MinCPUStateComboBox.LostFocus += Control_LostFocus;
            MaxCPUStateComboBox.GotFocus += Control_GotFocus;
            MaxCPUStateComboBox.LostFocus += Control_LostFocus;

            // Performance tab - FPS Limit card
            FPSLimitToggle.GotFocus += Control_GotFocus;
            FPSLimitToggle.LostFocus += Control_LostFocus;
            FPSLimitSlider.GotFocus += Control_GotFocus;
            FPSLimitSlider.LostFocus += Control_LostFocus;

            // Performance tab - OS Power Mode card
            OSPowerModeComboBox.GotFocus += Control_GotFocus;
            OSPowerModeComboBox.LostFocus += Control_LostFocus;

            // Profiles tab - Power Source Profile card
            PowerSourceProfileToggle.GotFocus += Control_GotFocus;
            PowerSourceProfileToggle.LostFocus += Control_LostFocus;

            // System tab - Profile Settings card (checkboxes have individual focus, not card focus)
            // These use FocusableCheckBoxStyle which shows its own focus visual
            ProfileSaveTDPCheckBox.GotFocus += StandaloneControl_GotFocus;
            ProfileSaveCPUBoostCheckBox.GotFocus += StandaloneControl_GotFocus;
            ProfileSaveCPUEPPCheckBox.GotFocus += StandaloneControl_GotFocus;
            ProfileSaveCPUStateCheckBox.GotFocus += StandaloneControl_GotFocus;

            // System tab - Power & Sleep card
            PowerAndSleepExpandButton.GotFocus += Control_GotFocus;
            PowerAndSleepExpandButton.LostFocus += Control_LostFocus;
            PowerButtonActionACComboBox.GotFocus += Control_GotFocus;
            PowerButtonActionACComboBox.LostFocus += Control_LostFocus;
            PowerButtonActionDCComboBox.GotFocus += Control_GotFocus;
            PowerButtonActionDCComboBox.LostFocus += Control_LostFocus;
            DisplayTimeoutACComboBox.GotFocus += Control_GotFocus;
            DisplayTimeoutACComboBox.LostFocus += Control_LostFocus;
            DisplayTimeoutDCComboBox.GotFocus += Control_GotFocus;
            DisplayTimeoutDCComboBox.LostFocus += Control_LostFocus;
            HibernateTimeoutACComboBox.GotFocus += Control_GotFocus;
            HibernateTimeoutACComboBox.LostFocus += Control_LostFocus;
            HibernateTimeoutDCComboBox.GotFocus += Control_GotFocus;
            HibernateTimeoutDCComboBox.LostFocus += Control_LostFocus;

            // System tab - OSD Customization card
            OSDCustomizeExpandButton.GotFocus += Control_GotFocus;
            OSDCustomizeExpandButton.LostFocus += Control_LostFocus;

            // Legion tab - Touchpad card
            LegionTouchpadToggle.GotFocus += Control_GotFocus;
            LegionTouchpadToggle.LostFocus += Control_LostFocus;

            // Legion tab - Vibration card
            LegionVibrationComboBox.GotFocus += Control_GotFocus;
            LegionVibrationComboBox.LostFocus += Control_LostFocus;

            // Legion tab - Light Mode card
            LegionLightModeComboBox.GotFocus += Control_GotFocus;
            LegionLightModeComboBox.LostFocus += Control_LostFocus;

            // Legion tab - Light Color card (ColorPicker)
            LegionColorExpandButton.GotFocus += Control_GotFocus;
            LegionColorExpandButton.LostFocus += Control_LostFocus;
            LegionColorPicker.GotFocus += Control_GotFocus;
            LegionColorPicker.LostFocus += Control_LostFocus;

            // Legion tab - Brightness card
            LegionBrightnessSlider.GotFocus += Control_GotFocus;
            LegionBrightnessSlider.LostFocus += Control_LostFocus;

            // Legion tab - Performance Mode "Power Profile" dropdown removed; the Performance tab's
            // TDP Mode dropdown is now the single performance-mode control.

            // Legion tab - Custom TDP card removed; SPL/SPPL/FPPT now live on Performance tab.

            // Legion tab - Fan Full Speed card
            LegionFanFullSpeedToggle.GotFocus += Control_GotFocus;
            LegionFanFullSpeedToggle.LostFocus += Control_LostFocus;

            // Legion tab - Power Light card
            LegionPowerLightToggle.GotFocus += Control_GotFocus;
            LegionPowerLightToggle.LostFocus += Control_LostFocus;

            // Legion tab - Charge Limit card
            LegionChargeLimitToggle.GotFocus += Control_GotFocus;
            LegionChargeLimitToggle.LostFocus += Control_LostFocus;
        }

        private void Control_GotFocus(object sender, RoutedEventArgs e)
        {
            // Card focus highlighting disabled - only controls show focus visuals.
            // Ensure the focused control scrolls into view so gamepad navigation down a long
            // tab never leaves the focus ring off-screen.
            BringFocusedControlIntoView(sender);
        }

        private void Control_LostFocus(object sender, RoutedEventArgs e)
        {
            // Don't clear immediately - let GotFocus of next control handle it
            // This prevents flicker when focus moves between controls in same card
        }

        private void NavItem_GotFocus(object sender, RoutedEventArgs e)
        {
            // Clear card highlight when navigation tabs get focus
            ClearCardFocus();
        }

        private void StandaloneControl_GotFocus(object sender, RoutedEventArgs e)
        {
            // Clear card highlight when standalone controls (not in cards) get focus
            ClearCardFocus();
            BringFocusedControlIntoView(sender);
        }

        /// <summary>
        /// Scrolls the just-focused control into view within its ScrollViewer. Keeps the gamepad
        /// focus ring visible when navigating down long tabs. StartBringIntoView is a no-op when
        /// the element is already fully visible, so this is safe to call on every GotFocus.
        /// </summary>
        private void BringFocusedControlIntoView(object sender)
        {
            try
            {
                (sender as FrameworkElement)?.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 0.5 // center the control in the viewport
                });
            }
            catch { /* best-effort scroll assist */ }
        }

        private void ClearCardFocus()
        {
            if (currentFocusedCard != null)
            {
                currentFocusedCard.BorderBrush = cardDefaultBorderBrush;
                currentFocusedCard = null;
            }
        }

        private Border FindParentCard(DependencyObject element)
        {
            while (element != null)
            {
                if (element is Border border && border.Style == (Style)Resources["CardStyle"])
                {
                    return border;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

    }
}
