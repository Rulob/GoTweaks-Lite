using System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace XboxGamingBar
{
    // AutoTDP: continuously adjusts TDP to hit a target FPS. Available regardless of the current
    // TDP Mode - turning it on forces Custom mode (the only mode it can actually manage TDP in)
    // and locks the mode selector; turning it off restores whatever mode was active before.
    public sealed partial class GamingWidget
    {
        private DispatcherTimer autoTDPTargetFPSDebounceTimer;
        private int pendingAutoTDPTargetFPS;
        private DispatcherTimer autoTDPMinDebounceTimer;
        private int pendingAutoTDPMinTDP;
        private DispatcherTimer autoTDPMaxDebounceTimer;
        private int pendingAutoTDPMaxTDP;

        // Remembers which TDP Mode was active before AutoTDP force-switched to Custom, so
        // turning AutoTDP off can put it back. Null when AutoTDP was enabled while already in
        // Custom mode (nothing to restore) or while AutoTDP is off.
        private int? tdpModeIndexBeforeAutoTDP = null;

        private void AutoTDPToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AutoTDPToggle == null) return;
            if (isApplyingHelperUpdate) return;

            ApplyAutoTDPToggle(AutoTDPToggle.IsOn);
        }

        /// <summary>
        /// Handles a user-initiated AutoTDP on/off request, from either the Performance tab
        /// toggle or the Quick Settings tile. Forces/restores TDP Mode and locks/unlocks the mode
        /// selector around it, then pushes the enabled state to the helper.
        /// </summary>
        private void ApplyAutoTDPToggle(bool isOn)
        {
            Logger.Info($"AutoTDP toggled to: {isOn}");

            if (isOn)
            {
                if (TDPModeComboBox != null && !IsCustomTdpModeSelected())
                {
                    tdpModeIndexBeforeAutoTDP = TDPModeComboBox.SelectedIndex;
                    Logger.Info($"AutoTDP: switching TDP Mode to Custom (was index {tdpModeIndexBeforeAutoTDP})");
                    TDPModeComboBox.SelectedIndex = GetCustomTdpModeIndex();
                }
            }
            else if (tdpModeIndexBeforeAutoTDP.HasValue)
            {
                Logger.Info($"AutoTDP: restoring TDP Mode to index {tdpModeIndexBeforeAutoTDP.Value}");
                if (TDPModeComboBox != null)
                {
                    TDPModeComboBox.SelectedIndex = tdpModeIndexBeforeAutoTDP.Value;
                }
                tdpModeIndexBeforeAutoTDP = null;
            }

            UpdateAutoTDPToggleUI(isOn);

            autoTDPEnabled?.SetValue(isOn);
        }

        /// <summary>
        /// Reflects AutoTDP's on/off state in the rest of the Performance tab: locks the TDP Mode
        /// selector while active (so it can't be changed out from under AutoTDP - including via
        /// the Quick Settings "TDP Mode" tile, guarded separately in CycleTDPMode) and disables
        /// the manual Custom sliders, which AutoTDP would otherwise fight.
        /// </summary>
        private void UpdateAutoTDPToggleUI(bool isOn)
        {
            if (TDPModeComboBox != null) TDPModeComboBox.IsEnabled = !isOn;
            UpdateCustomTDPSlidersEnabledForAutoTDP();
            UpdateQuickSettingsTileStates();
        }

        /// <summary>
        /// Disables the manual Custom TDP sliders while AutoTDP is managing TDP; re-enables them
        /// when it's off. Call whenever AutoTDPToggle's state changes or the Custom card is shown.
        /// </summary>
        private void UpdateCustomTDPSlidersEnabledForAutoTDP()
        {
            bool autoTDPOwnsTDP = AutoTDPToggle?.IsOn == true;
            if (CustomTDPSlowSlider != null) CustomTDPSlowSlider.IsEnabled = !autoTDPOwnsTDP;
            if (CustomTDPFastSlider != null) CustomTDPFastSlider.IsEnabled = !autoTDPOwnsTDP;
            if (CustomTDPPeakSlider != null) CustomTDPPeakSlider.IsEnabled = !autoTDPOwnsTDP;
        }

        private void AutoTDPTargetFPSSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (AutoTDPTargetFPSSlider == null) return;
            if (isApplyingHelperUpdate) return;

            int targetFPS = (int)Math.Round(e.NewValue);

            if (AutoTDPTargetFPSValue != null)
            {
                AutoTDPTargetFPSValue.Text = $"{targetFPS} FPS";
            }

            pendingAutoTDPTargetFPS = targetFPS;

            if (autoTDPTargetFPSDebounceTimer == null)
            {
                autoTDPTargetFPSDebounceTimer = new DispatcherTimer();
                autoTDPTargetFPSDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                autoTDPTargetFPSDebounceTimer.Tick += AutoTDPTargetFPSDebounceTimer_Tick;
            }
            autoTDPTargetFPSDebounceTimer.Stop();
            autoTDPTargetFPSDebounceTimer.Start();
        }

        private void AutoTDPTargetFPSDebounceTimer_Tick(object sender, object e)
        {
            autoTDPTargetFPSDebounceTimer.Stop();
            Logger.Info($"AutoTDP target FPS changed to: {pendingAutoTDPTargetFPS} (debounced)");
            autoTDPTargetFPS?.SetValue(pendingAutoTDPTargetFPS);
        }

        private void AutoTDPMinSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (AutoTDPMinSlider == null) return;
            if (isApplyingHelperUpdate) return;

            int minTDP = (int)Math.Round(e.NewValue);

            // Keep Max from being dragged below Min by raising its floor, instead of snapping
            // Min back with a `.Value =` write-back - reassigning Value mid-drag re-fires this
            // same event and can retrigger the ScrollViewer's "bring focused element into view"
            // on every correction, which is what made the whole card jump while dragging.
            if (AutoTDPMaxSlider != null && AutoTDPMaxSlider.Minimum != minTDP)
            {
                AutoTDPMaxSlider.Minimum = minTDP;
            }

            if (AutoTDPMinValue != null)
            {
                AutoTDPMinValue.Text = $"{minTDP}W";
            }

            pendingAutoTDPMinTDP = minTDP;

            if (autoTDPMinDebounceTimer == null)
            {
                autoTDPMinDebounceTimer = new DispatcherTimer();
                autoTDPMinDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                autoTDPMinDebounceTimer.Tick += AutoTDPMinDebounceTimer_Tick;
            }
            autoTDPMinDebounceTimer.Stop();
            autoTDPMinDebounceTimer.Start();
        }

        private void AutoTDPMinDebounceTimer_Tick(object sender, object e)
        {
            autoTDPMinDebounceTimer.Stop();
            Logger.Info($"AutoTDP min TDP changed to: {pendingAutoTDPMinTDP}W (debounced)");
            autoTDPMinTDP?.SetValue(pendingAutoTDPMinTDP);
        }

        private void AutoTDPMaxSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (AutoTDPMaxSlider == null) return;
            if (isApplyingHelperUpdate) return;

            int maxTDP = (int)Math.Round(e.NewValue);

            // Mirror of the Min handler above: lower Min's ceiling instead of snapping Max back.
            if (AutoTDPMinSlider != null && AutoTDPMinSlider.Maximum != maxTDP)
            {
                AutoTDPMinSlider.Maximum = maxTDP;
            }

            if (AutoTDPMaxValue != null)
            {
                AutoTDPMaxValue.Text = $"{maxTDP}W";
            }

            pendingAutoTDPMaxTDP = maxTDP;

            if (autoTDPMaxDebounceTimer == null)
            {
                autoTDPMaxDebounceTimer = new DispatcherTimer();
                autoTDPMaxDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                autoTDPMaxDebounceTimer.Tick += AutoTDPMaxDebounceTimer_Tick;
            }
            autoTDPMaxDebounceTimer.Stop();
            autoTDPMaxDebounceTimer.Start();
        }

        private void AutoTDPMaxDebounceTimer_Tick(object sender, object e)
        {
            autoTDPMaxDebounceTimer.Stop();
            Logger.Info($"AutoTDP max TDP changed to: {pendingAutoTDPMaxTDP}W (debounced)");
            autoTDPMaxTDP?.SetValue(pendingAutoTDPMaxTDP);
        }

        private void AutoTDPPauseWhenUnfocusedToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AutoTDPPauseWhenUnfocusedToggle == null) return;
            if (isApplyingHelperUpdate) return;

            bool pauseWhenUnfocused = AutoTDPPauseWhenUnfocusedToggle.IsOn;
            Logger.Info($"AutoTDP pause when unfocused changed to: {pauseWhenUnfocused}");
            autoTDPPauseWhenUnfocused?.SetValue(pauseWhenUnfocused);
        }

        private void AutoTDPEnabled_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (AutoTDPToggle != null && autoTDPEnabled != null && AutoTDPToggle.IsOn != autoTDPEnabled.Value)
                {
                    isApplyingHelperUpdate = true;
                    try
                    {
                        AutoTDPToggle.IsOn = autoTDPEnabled.Value;
                        // Sync-driven (e.g. profile switch) - just reflect the lock state, don't
                        // re-run the remember/force-switch/restore dance ApplyAutoTDPToggle does
                        // for a real user click. The profile system already restores whatever TDP
                        // Mode that profile wants on its own.
                        UpdateAutoTDPToggleUI(autoTDPEnabled.Value);
                    }
                    finally
                    {
                        isApplyingHelperUpdate = false;
                    }
                }
            });
        }

        private void AutoTDPTargetFPS_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (autoTDPTargetFPS == null) return;
                isApplyingHelperUpdate = true;
                try
                {
                    if (AutoTDPTargetFPSSlider != null && (int)AutoTDPTargetFPSSlider.Value != autoTDPTargetFPS.Value)
                    {
                        AutoTDPTargetFPSSlider.Value = autoTDPTargetFPS.Value;
                    }
                    if (AutoTDPTargetFPSValue != null)
                    {
                        AutoTDPTargetFPSValue.Text = $"{autoTDPTargetFPS.Value} FPS";
                    }
                }
                finally
                {
                    isApplyingHelperUpdate = false;
                }
            });
        }

        private void AutoTDPCurrentFPS_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (AutoTDPCurrentFPSValue != null && autoTDPCurrentFPS != null)
                {
                    int fps = autoTDPCurrentFPS.Value;
                    AutoTDPCurrentFPSValue.Text = fps > 0 ? $"{fps} FPS" : "-- FPS";
                }
            });
        }

        private void AutoTDPMinTDP_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (autoTDPMinTDP == null) return;
                isApplyingHelperUpdate = true;
                try
                {
                    if (AutoTDPMaxSlider != null) AutoTDPMaxSlider.Minimum = autoTDPMinTDP.Value;
                    if (AutoTDPMinSlider != null && (int)AutoTDPMinSlider.Value != autoTDPMinTDP.Value)
                    {
                        AutoTDPMinSlider.Value = autoTDPMinTDP.Value;
                    }
                    if (AutoTDPMinValue != null)
                    {
                        AutoTDPMinValue.Text = $"{autoTDPMinTDP.Value}W";
                    }
                }
                finally
                {
                    isApplyingHelperUpdate = false;
                }
            });
        }

        private void AutoTDPMaxTDP_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (autoTDPMaxTDP == null) return;
                isApplyingHelperUpdate = true;
                try
                {
                    if (AutoTDPMinSlider != null) AutoTDPMinSlider.Maximum = autoTDPMaxTDP.Value;
                    if (AutoTDPMaxSlider != null && (int)AutoTDPMaxSlider.Value != autoTDPMaxTDP.Value)
                    {
                        AutoTDPMaxSlider.Value = autoTDPMaxTDP.Value;
                    }
                    if (AutoTDPMaxValue != null)
                    {
                        AutoTDPMaxValue.Text = $"{autoTDPMaxTDP.Value}W";
                    }
                }
                finally
                {
                    isApplyingHelperUpdate = false;
                }
            });
        }

        private void AutoTDPPauseWhenUnfocused_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (AutoTDPPauseWhenUnfocusedToggle != null && autoTDPPauseWhenUnfocused != null
                    && AutoTDPPauseWhenUnfocusedToggle.IsOn != autoTDPPauseWhenUnfocused.Value)
                {
                    isApplyingHelperUpdate = true;
                    try
                    {
                        AutoTDPPauseWhenUnfocusedToggle.IsOn = autoTDPPauseWhenUnfocused.Value;
                    }
                    finally
                    {
                        isApplyingHelperUpdate = false;
                    }
                }
            });
        }
    }
}
