using System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace XboxGamingBar
{
    // AutoTDP: continuously adjusts TDP (while in Custom TDP mode) to hit a target FPS.
    // The AutoTDPCard is only visible in Custom mode (see GamingWidget.LegionGo.cs
    // UpdateCustomTDPCardsVisibility), so there's no need to auto-switch modes here.
    public sealed partial class GamingWidget
    {
        private DispatcherTimer autoTDPTargetFPSDebounceTimer;
        private int pendingAutoTDPTargetFPS;
        private DispatcherTimer autoTDPMinDebounceTimer;
        private int pendingAutoTDPMinTDP;
        private DispatcherTimer autoTDPMaxDebounceTimer;
        private int pendingAutoTDPMaxTDP;

        private void AutoTDPToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AutoTDPToggle == null) return;
            if (isApplyingHelperUpdate) return;

            Logger.Info($"AutoTDP toggled to: {AutoTDPToggle.IsOn}");

            // While AutoTDP owns TDP, the manual Custom sliders would just fight it - disable them.
            UpdateCustomTDPSlidersEnabledForAutoTDP();

            autoTDPEnabled?.SetValue(AutoTDPToggle.IsOn);
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

            if (AutoTDPMaxSlider != null && minTDP > AutoTDPMaxSlider.Value)
            {
                minTDP = (int)AutoTDPMaxSlider.Value;
                AutoTDPMinSlider.Value = minTDP;
                return;
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

            if (AutoTDPMinSlider != null && maxTDP < AutoTDPMinSlider.Value)
            {
                maxTDP = (int)AutoTDPMinSlider.Value;
                AutoTDPMaxSlider.Value = maxTDP;
                return;
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
                        UpdateCustomTDPSlidersEnabledForAutoTDP();
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
