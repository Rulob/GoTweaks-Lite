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

        // After AutoTDP is turned off the helper writes its own restore value; re-assert the
        // Custom card's limits once that has landed.
        private const int CustomTDPRestoreAfterAutoTDPDelayMs = 1500;

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

            if (isOn)
            {
                // Boosts first, so AutoTDP's very first write already carries them.
                SendAutoTDPBoostsToHelper();
            }
            autoTDPEnabled?.SetValue(isOn);

            if (!isOn)
            {
                ScheduleCustomTDPRestoreAfterAutoTDP();
            }
            else
            {
                KeepAutoTDPCardInView();
            }

            SaveCurrentSettingsToProfile(currentProfileName);
        }

        /// <summary>
        /// Turning AutoTDP on switches to Custom mode, which reveals the Custom power-limit card
        /// above the AutoTDP card. That pushes
        /// AutoTDP's own settings (which appear at the same moment) down out of view, so it
        /// looked as if the tab jumped. Once that layout has settled, scroll the AutoTDP card back
        /// to the top of the view so its settings are visible.
        /// </summary>
        private void KeepAutoTDPCardInView()
        {
            if (AutoTDPCard == null || PerformanceScrollViewer == null
                || PerformanceScrollViewer.Visibility != Visibility.Visible) return;

            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                try
                {
                    AutoTDPCard.UpdateLayout();
                    AutoTDPCard.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = false,
                        VerticalAlignmentRatio = 0.0 // card top at the top of the view
                    });
                }
                catch (Exception ex)
                {
                    Logger.Debug($"KeepAutoTDPCardInView failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// While AutoTDP runs it owns SPL/SPPT/FPPT; when it stops the helper writes the profile's
        /// flat TDP. If the device is still in Custom mode, put the Custom card's own limits back
        /// once that write has landed, so the sliders and the hardware agree again.
        /// </summary>
        private async void ScheduleCustomTDPRestoreAfterAutoTDP()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(CustomTDPRestoreAfterAutoTDPDelayMs);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (AutoTDPToggle?.IsOn == true) return;
                    if (legionGoDetected?.Value != true || !IsCustomTdpModeSelected()) return;
                    Logger.Info("AutoTDP off: re-applying the Custom card's power limits");
                    ApplyCustomTDPSlidersToHelper(force: true);
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Custom TDP restore after AutoTDP failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends the Custom card's SPPT / FPPT Boost sliders to the helper, which adds them on top
        /// of every wattage AutoTDP writes (so SPPT/FPPT keep their offsets instead of being
        /// flattened to the AutoTDP value).
        /// </summary>
        private void SendAutoTDPBoostsToHelper(bool force = false)
        {
            if (CustomTDPFastSlider == null || CustomTDPPeakSlider == null) return;
            int fppt = (int)CustomTDPPeakSlider.Value;
            int sppt = Math.Min((int)CustomTDPFastSlider.Value, fppt);
            if (force)
            {
                autoTDPSpptBoost?.ForceSetValue(sppt);
                autoTDPFpptBoost?.ForceSetValue(fppt);
            }
            else
            {
                autoTDPSpptBoost?.SetValue(sppt);
                autoTDPFpptBoost?.SetValue(fppt);
            }
        }

        /// <summary>
        /// Applies a profile's saved AutoTDP setup to the controls, and to the helper for profiles
        /// the widget switches itself (game profiles are applied by the helper from its own copy;
        /// the post-switch reconcile pushes ours). Profiles saved before AutoTDP was stored leave
        /// AutoTDP alone.
        /// </summary>
        private void ApplyAutoTDPFromProfile(PerformanceProfile profile, string profileName)
        {
            if (!profile.AutoTDPEnabled.HasValue || legionGoDetected?.Value != true || isInitialSync) return;

            bool on = profile.AutoTDPEnabled.Value;
            bool helperDrivenSwitch = isApplyingHelperUpdate;

            isApplyingHelperUpdate = true;
            try
            {
                SetAutoTDPSlidersSilent(profile.AutoTDPTargetFPS, profile.AutoTDPMinTDP, profile.AutoTDPMaxTDP);
                if (AutoTDPToggle != null && AutoTDPToggle.IsOn != on)
                {
                    AutoTDPToggle.IsOn = on;
                }
            }
            finally
            {
                isApplyingHelperUpdate = helperDrivenSwitch;
            }

            // The profile now decides the mode; there is nothing to restore when AutoTDP goes off.
            tdpModeIndexBeforeAutoTDP = null;
            UpdateAutoTDPToggleUI(on);
            Logger.Info($"Applied profile AutoTDP for {profileName}: {(on ? "On" : "Off")}, target={profile.AutoTDPTargetFPS} FPS, {profile.AutoTDPMinTDP}-{profile.AutoTDPMaxTDP}W");

            if (!profileName.StartsWith("Game_") && !helperDrivenSwitch)
            {
                PushAutoTDPSettingsToHelper(profile);
            }
        }

        /// <summary>Sends a profile's AutoTDP setup to the helper (settings first, then on/off).</summary>
        private void PushAutoTDPSettingsToHelper(PerformanceProfile profile)
        {
            if (!profile.AutoTDPEnabled.HasValue) return;
            SendAutoTDPBoostsToHelper();
            autoTDPTargetFPS?.SetValue(profile.AutoTDPTargetFPS);
            autoTDPMinTDP?.SetValue(profile.AutoTDPMinTDP);
            autoTDPMaxTDP?.SetValue(profile.AutoTDPMaxTDP);
            autoTDPEnabled?.SetValue(profile.AutoTDPEnabled.Value);
        }

        /// <summary>Sets the AutoTDP sliders and labels without sending anything.</summary>
        private void SetAutoTDPSlidersSilent(int targetFps, int minTdp, int maxTdp)
        {
            if (AutoTDPMinSlider != null) AutoTDPMinSlider.Value = minTdp;
            if (AutoTDPMaxSlider != null) AutoTDPMaxSlider.Value = maxTdp;
            if (AutoTDPTargetFPSSlider != null) AutoTDPTargetFPSSlider.Value = targetFps;
            if (AutoTDPTargetFPSValue != null) AutoTDPTargetFPSValue.Text = $"{targetFps} FPS";
            // Labels show the values the sliders actually took (clamped to the device range).
            if (AutoTDPMinValue != null) AutoTDPMinValue.Text = $"{(int)(AutoTDPMinSlider?.Value ?? minTdp)}W";
            if (AutoTDPMaxValue != null) AutoTDPMaxValue.Text = $"{(int)(AutoTDPMaxSlider?.Value ?? maxTdp)}W";
        }

        /// <summary>
        /// Gives the Min / Max TDP sliders the device's TDP range (the same limits the rest of the
        /// widget uses) instead of a fixed 4-85 W. Both sliders always share the same range, so
        /// moving one never shifts the other's handle.
        /// </summary>
        private void ApplyAutoTDPSliderRange()
        {
            if (AutoTDPMinSlider == null || AutoTDPMaxSlider == null) return;
            int min = Math.Max(4, deviceTDPMin);
            int max = Math.Max(min + 1, deviceTDPMax);

            bool prev = isApplyingHelperUpdate;
            isApplyingHelperUpdate = true; // clamping a value here must not send or save anything
            try
            {
                AutoTDPMinSlider.Minimum = min;
                AutoTDPMinSlider.Maximum = max;
                AutoTDPMaxSlider.Minimum = min;
                AutoTDPMaxSlider.Maximum = max;
            }
            finally
            {
                isApplyingHelperUpdate = prev;
            }
            if (AutoTDPMinValue != null) AutoTDPMinValue.Text = $"{(int)AutoTDPMinSlider.Value}W";
            if (AutoTDPMaxValue != null) AutoTDPMaxValue.Text = $"{(int)AutoTDPMaxSlider.Value}W";
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
        /// Disables the manual TDP (SPL) slider while AutoTDP is managing TDP; re-enables it when
        /// it's off. The SPPT / FPPT Boost sliders stay usable: AutoTDP adds those boosts on top of
        /// every wattage it picks. Call whenever AutoTDPToggle's state changes or the Custom card
        /// is shown.
        /// </summary>
        private void UpdateCustomTDPSlidersEnabledForAutoTDP()
        {
            bool autoTDPOwnsTDP = AutoTDPToggle?.IsOn == true;
            if (CustomTDPSlowSlider != null) CustomTDPSlowSlider.IsEnabled = !autoTDPOwnsTDP;
            if (CustomTDPFastSlider != null) CustomTDPFastSlider.IsEnabled = true;
            if (CustomTDPPeakSlider != null) CustomTDPPeakSlider.IsEnabled = true;
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
            SaveCurrentSettingsToProfile(currentProfileName);
        }

        private void AutoTDPMinSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (AutoTDPMinSlider == null) return;
            if (isApplyingHelperUpdate) return;

            int minTDP = (int)Math.Round(e.NewValue);

            // Min dragged above Max: carry Max up with it (a real value change, shown and sent).
            // Raising Max's range floor instead moved Max's handle without changing its value.
            if (AutoTDPMaxSlider != null && AutoTDPMaxSlider.Value < minTDP)
            {
                AutoTDPMaxSlider.Value = minTDP; // fires AutoTDPMaxSlider_ValueChanged, which sends it
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
            SaveCurrentSettingsToProfile(currentProfileName);
        }

        private void AutoTDPMaxSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (AutoTDPMaxSlider == null) return;
            if (isApplyingHelperUpdate) return;

            int maxTDP = (int)Math.Round(e.NewValue);

            // Mirror of the Min handler above: Max dragged below Min carries Min down with it.
            if (AutoTDPMinSlider != null && AutoTDPMinSlider.Value > maxTDP)
            {
                AutoTDPMinSlider.Value = maxTDP; // fires AutoTDPMinSlider_ValueChanged, which sends it
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
            SaveCurrentSettingsToProfile(currentProfileName);
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
