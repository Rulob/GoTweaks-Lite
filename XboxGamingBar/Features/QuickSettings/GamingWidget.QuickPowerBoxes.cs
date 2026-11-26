using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;

namespace XboxGamingBar
{
    // Quick Settings: the TDP Mode and AutoTDP boxes above the tiles (1.9.7c). Each header shows
    // the current state and expands the controls. They drive the Performance tab's own controls
    // (TDPModeComboBox, CustomTDPSlowSlider, AutoTDPToggle, AutoTDPTargetFPSSlider), so the
    // sending, locking and saving logic stays in one place, and they follow those controls back.
    public sealed partial class GamingWidget
    {
        private bool isUpdatingQuickPowerBoxes;
        private static readonly int[] QuickTdpModeValues = { 1, 2, 3, 255 }; // Quiet, Balanced, Performance, Custom

        /// <summary>
        /// Switches the TDP mode the way the Performance tab dropdown does (used by the TDP Mode
        /// box and the TDP Mode tile / tile hotkey). Ignored while AutoTDP holds the mode.
        /// </summary>
        private void ApplyTdpModeFromQuickSettings(int nextMode)
        {
            if (AutoTDPToggle?.IsOn == true) return;
            bool isLegion = legionGoDetected?.Value == true;

            if (isLegion && legionPerformanceMode != null)
            {
                legionPerformanceMode.SetValue(nextMode);
            }

            int nextIndex = Array.IndexOf(QuickTdpModeValues, nextMode);
            if (nextIndex >= 0 && TDPModeComboBox != null)
            {
                isUserInitiatedTDPModeChange = true;
                TDPModeComboBox.SelectedIndex = nextIndex;
                isUserInitiatedTDPModeChange = false;
            }

            // Entering Custom on Legion: re-assert the Custom limits once the mode has landed.
            if (isLegion && nextMode == 255)
            {
                ScheduleQsTdpReapply();
            }
            UpdateQuickPowerBoxes();
        }

        /// <summary>Keeps the boxes in step with the controls they drive (wired once).</summary>
        private void SubscribeQuickPowerBoxes()
        {
            if (TDPModeComboBox != null) TDPModeComboBox.SelectionChanged += (s, e) => UpdateQuickPowerBoxes();
            if (CustomTDPSlowSlider != null) CustomTDPSlowSlider.ValueChanged += (s, e) => UpdateQuickPowerBoxes();
            if (AutoTDPToggle != null) AutoTDPToggle.Toggled += (s, e) => UpdateQuickPowerBoxes();
            if (AutoTDPTargetFPSSlider != null) AutoTDPTargetFPSSlider.ValueChanged += (s, e) => UpdateQuickPowerBoxes();
            UpdateQuickPowerBoxes();
        }

        private void QuickTdpModeHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleQuickPowerPanel(QuickTdpModePanel, QuickTdpModeChevron);
        }

        private void QuickAutoTdpHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleQuickPowerPanel(QuickAutoTdpPanel, QuickAutoTdpChevron);
        }

        private void ToggleQuickPowerPanel(FrameworkElement panel, FontIcon chevron)
        {
            if (panel == null) return;
            bool expand = panel.Visibility != Visibility.Visible;
            panel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            if (chevron != null) chevron.Glyph = expand ? "" : ""; // ChevronUp / ChevronDown
            UpdateQuickPowerBoxes();
        }

        private void QuickTdpModeOption_Click(object sender, RoutedEventArgs e)
        {
            if (isUpdatingQuickPowerBoxes) return;
            if (!(sender is FrameworkElement fe) || !int.TryParse(fe.Tag as string, out int index)) return;
            if (index < 0 || index >= QuickTdpModeValues.Length) return;
            ApplyTdpModeFromQuickSettings(QuickTdpModeValues[index]);
        }

        private void QuickTdpSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            int watts = (int)Math.Round(e.NewValue);
            if (QuickTdpValueText != null) QuickTdpValueText.Text = $"{watts} W";
            if (isUpdatingQuickPowerBoxes || CustomTDPSlowSlider == null) return;
            // Only usable in Custom, and not while AutoTDP picks the wattage.
            if (!IsCustomTdpModeSelected() || AutoTDPToggle?.IsOn == true) return;
            if ((int)Math.Round(CustomTDPSlowSlider.Value) != watts)
            {
                CustomTDPSlowSlider.Value = watts; // its handler applies the limits live
            }
        }

        private void QuickAutoTdpToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isUpdatingQuickPowerBoxes || AutoTDPToggle == null || QuickAutoTdpToggle == null) return;
            if (AutoTDPToggle.IsOn != QuickAutoTdpToggle.IsOn)
            {
                AutoTDPToggle.IsOn = QuickAutoTdpToggle.IsOn; // AutoTDPToggle_Toggled does the rest
            }
        }

        private void QuickAutoTdpTargetSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            int fps = (int)Math.Round(e.NewValue);
            if (QuickAutoTdpTargetText != null) QuickAutoTdpTargetText.Text = $"{fps} FPS";
            if (isUpdatingQuickPowerBoxes || AutoTDPTargetFPSSlider == null) return;
            if ((int)Math.Round(AutoTDPTargetFPSSlider.Value) != fps)
            {
                AutoTDPTargetFPSSlider.Value = fps; // its debounced handler sends and saves it
            }
        }

        /// <summary>Shows the current TDP mode / AutoTDP state in both boxes without sending anything.</summary>
        private void UpdateQuickPowerBoxes()
        {
            if (QuickTdpModeValueText == null || isUpdatingQuickPowerBoxes) return;
            isUpdatingQuickPowerBoxes = true;
            try
            {
                bool autoOn = AutoTDPToggle?.IsOn == true;
                int index = TDPModeComboBox?.SelectedIndex ?? -1;
                bool isCustom = IsCustomTdpModeSelected();
                int spl = (int)Math.Round(CustomTDPSlowSlider?.Value ?? 15);
                int target = (int)Math.Round(AutoTDPTargetFPSSlider?.Value ?? 60);

                // TDP Mode box
                string modeName = index >= 0 && index < QuickTdpModeValues.Length
                    ? GetLegionModeShortName(QuickTdpModeValues[index]) : "--";
                QuickTdpModeValueText.Text = isCustom ? (autoOn ? "Custom · Auto" : $"Custom · {spl} W") : modeName;

                var accent = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColorLight2"]);
                var idle = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2A, 0x2E, 0x33));
                var buttons = new[] { QuickTdpModeQuiet, QuickTdpModeBalanced, QuickTdpModePerformance, QuickTdpModeCustom };
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i] == null) continue;
                    buttons[i].Background = i == index ? accent : idle;
                    buttons[i].IsEnabled = !autoOn; // AutoTDP holds the mode (Custom)
                }
                if (QuickTdpSlider != null)
                {
                    QuickTdpSlider.Value = spl;
                    QuickTdpSlider.IsEnabled = isCustom && !autoOn;
                    QuickTdpSlider.Opacity = QuickTdpSlider.IsEnabled ? 1.0 : 0.4;
                }
                if (QuickTdpValueText != null) QuickTdpValueText.Text = autoOn && isCustom ? "AutoTDP" : $"{spl} W";

                // AutoTDP box
                if (QuickAutoTdpValueText != null) QuickAutoTdpValueText.Text = autoOn ? $"On · {target} FPS" : "Off";
                if (QuickAutoTdpToggle != null) QuickAutoTdpToggle.IsOn = autoOn;
                if (QuickAutoTdpTargetSlider != null) QuickAutoTdpTargetSlider.Value = target;
                if (QuickAutoTdpTargetText != null) QuickAutoTdpTargetText.Text = $"{target} FPS";
            }
            catch (Exception ex)
            {
                Logger.Debug($"UpdateQuickPowerBoxes failed: {ex.Message}");
            }
            finally
            {
                isUpdatingQuickPowerBoxes = false;
            }
        }
    }
}
