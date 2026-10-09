using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace XboxGamingBar
{
    /// <summary>
    /// Controller Mode card + Quick Settings "Input Mode" tile: a Legion Space style
    /// X-Input / D-Input switch for the Legion Go / Go 2 controllers.
    ///
    /// The helper owns the truth: it derives the current mode from the controllers' USB
    /// product ID and pushes it through <see cref="legionControllerMode"/>. Flipping the switch
    /// sends 1 (X-Input) or 2 (D-Input) to the helper, which issues the HID command; the
    /// controllers then re-enumerate and the helper reports the mode they actually ended up
    /// in, so a switch that didn't take snaps back by itself.
    /// </summary>
    public sealed partial class GamingWidget
    {
        // Values come from Shared.Data.LegionControllerModes (the same numbers the helper sends).
        private const int LegionControllerModeUnknown = Shared.Data.LegionControllerModes.Unknown;
        private const int LegionControllerModeXInput = Shared.Data.LegionControllerModes.XInput;
        private const int LegionControllerModeDInput = Shared.Data.LegionControllerModes.DInput;
        private const int LegionControllerModeDualDInput = Shared.Data.LegionControllerModes.DualDInput;
        private const int LegionControllerModeFps = Shared.Data.LegionControllerModes.Fps;

        // The pads drop off the bus for a few seconds while they switch; don't leave the
        // switch greyed out forever if the "done" signal never arrives.
        private const int ControllerModeSwitchUiTimeoutMs = 12000;

        private bool isApplyingControllerModeUI;
        private int controllerModeSwitchTarget;      // 0 = not switching
        private int controllerModeSwitchGeneration;  // invalidates stale timeouts

        private static string ControllerModeName(int mode)
        {
            switch (mode)
            {
                case LegionControllerModeXInput: return "X-Input";
                case LegionControllerModeDInput: return "D-Input";
                case LegionControllerModeDualDInput: return "Dual D-Input";
                case LegionControllerModeFps: return "FPS mode";
                default: return "unknown";
            }
        }

        private void LegionControllerMode_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                int mode = legionControllerMode?.Value ?? LegionControllerModeUnknown;

                // A mode that differs from the one we asked for means the helper answered with
                // what the controllers really are (e.g. the switch didn't take) - stop waiting.
                if (controllerModeSwitchTarget != 0 && mode != controllerModeSwitchTarget)
                {
                    EndControllerModeSwitching($"helper reported {ControllerModeName(mode)}");
                    return;
                }

                UpdateLegionControllerModeUI();
            });
        }

        /// <summary>
        /// The controllers re-enumerate under a new USB product ID when the mode changes, so a
        /// VID:PID change while a switch is pending means it went through.
        /// </summary>
        private void OnControllerVidPidChangedForModeSwitch()
        {
            if (controllerModeSwitchTarget != 0)
            {
                EndControllerModeSwitching("controllers re-enumerated");
            }
        }

        private void UpdateLegionControllerModeUI()
        {
            try
            {
                if (LegionControllerModeCard == null || LegionControllerModeToggle == null) return;

                int mode = legionControllerMode?.Value ?? LegionControllerModeUnknown;
                bool switching = controllerModeSwitchTarget != 0;

                // Only show the card for controllers that actually support the switch.
                LegionControllerModeCard.Visibility = (mode != LegionControllerModeUnknown || switching)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                bool switchable = mode == LegionControllerModeXInput || mode == LegionControllerModeDInput;

                isApplyingControllerModeUI = true;
                try
                {
                    LegionControllerModeToggle.IsOn = (switching ? controllerModeSwitchTarget : mode) == LegionControllerModeDInput;
                }
                finally
                {
                    isApplyingControllerModeUI = false;
                }
                LegionControllerModeToggle.IsEnabled = switchable && !switching;

                // Keep D-pad focus navigation valid: the Touchpad card's header sits below this
                // switch, so only route "up" through it while the switch can actually take focus.
                if (TouchpadVibrationExpandToggle != null && LegionNavItem != null)
                {
                    bool switchFocusable = LegionControllerModeCard.Visibility == Visibility.Visible
                                           && LegionControllerModeToggle.IsEnabled;
                    TouchpadVibrationExpandToggle.XYFocusUp = switchFocusable
                        ? (DependencyObject)LegionControllerModeToggle
                        : LegionNavItem;
                }

                string status;
                if (switching)
                {
                    status = $"Switching to {ControllerModeName(controllerModeSwitchTarget)}… the controllers reconnect for a moment";
                }
                else
                {
                    switch (mode)
                    {
                        case LegionControllerModeXInput:
                            status = "X-Input — standard Xbox controller (switch on for D-Input)";
                            break;
                        case LegionControllerModeDInput:
                            status = "D-Input — generic gamepad, for games that need it";
                            break;
                        case LegionControllerModeDualDInput:
                            status = "Controllers are detached — they run in Dual D-Input, set by the hardware";
                            break;
                        case LegionControllerModeFps:
                            status = "FPS mode switch is on (right controller) — mode can't be changed";
                            break;
                        default:
                            status = "Controllers not detected";
                            break;
                    }
                }

                if (LegionControllerModeStatusText != null)
                {
                    LegionControllerModeStatusText.Text = status;
                }

                if (quickSettingsInitialized)
                {
                    UpdateQuickSettingsTileStates();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error updating Legion controller mode UI: {ex.Message}");
            }
        }

        private void LegionControllerModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isApplyingControllerModeUI) return;

            SetLegionControllerMode(LegionControllerModeToggle.IsOn
                ? LegionControllerModeDInput
                : LegionControllerModeXInput);

            // If the request was refused, put the switch back to what the hardware reports.
            UpdateLegionControllerModeUI();
        }

        /// <summary>
        /// Asks the helper to switch the controllers to X-Input (1) or D-Input (2). Used by
        /// both the Controller Mode card and the Quick Settings "Input Mode" tile.
        /// </summary>
        internal void SetLegionControllerMode(int mode)
        {
            if (legionControllerMode == null) return;
            if (mode != LegionControllerModeXInput && mode != LegionControllerModeDInput) return;
            if (controllerModeSwitchTarget != 0) return; // a switch is already in flight

            int current = legionControllerMode.Value;
            if (current == mode) return;
            if (current != LegionControllerModeXInput && current != LegionControllerModeDInput)
            {
                Logger.Info($"Controller mode change to {ControllerModeName(mode)} ignored - controllers are in {ControllerModeName(current)}.");
                return;
            }

            Logger.Info($"Controller mode: requesting {ControllerModeName(mode)} (was {ControllerModeName(current)})");
            BeginControllerModeSwitching(mode);
            legionControllerMode.SetValue(mode); // sent to the helper, which issues the HID command
        }

        private async void BeginControllerModeSwitching(int target)
        {
            controllerModeSwitchTarget = target;
            int generation = ++controllerModeSwitchGeneration;
            UpdateLegionControllerModeUI();

            await Task.Delay(ControllerModeSwitchUiTimeoutMs);

            if (generation == controllerModeSwitchGeneration && controllerModeSwitchTarget != 0)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (generation == controllerModeSwitchGeneration)
                    {
                        EndControllerModeSwitching("timeout");
                    }
                });
            }
        }

        private void EndControllerModeSwitching(string reason)
        {
            if (controllerModeSwitchTarget == 0) return;

            Logger.Info($"Controller mode switch finished ({reason}); mode is now {ControllerModeName(legionControllerMode?.Value ?? 0)}");
            controllerModeSwitchTarget = 0;
            controllerModeSwitchGeneration++;
            UpdateLegionControllerModeUI();
        }
    }
}
