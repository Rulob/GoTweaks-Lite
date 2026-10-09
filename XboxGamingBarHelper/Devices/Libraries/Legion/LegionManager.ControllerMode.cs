using HidSharp;
using Shared.Data;
using NLog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    /// <summary>
    /// Controller input mode: the Legion Space X-Input / D-Input switch.
    ///
    /// The controllers re-enumerate on USB under a different product ID whenever the mode
    /// changes, so the PID (see <see cref="Shared.Data.LegionControllerModes"/>) is the source of truth for
    /// the current mode; the widget's property mirrors it. A widget request for 1 (X-Input) or
    /// 2 (D-Input) sends the HID command, waits for the pads to come back, and then publishes
    /// whatever mode they actually ended up in - so a failed switch visibly snaps back.
    /// </summary>
    internal partial class LegionManager
    {
        private const int ControllerModeSwitchTimeoutMs = 10000;
        private const int ControllerModePollIntervalMs = 400;

        // Mode the controllers are really in right now (LegionControllerModes.*).
        private int detectedControllerMode = LegionControllerModes.Unknown;
        private int controllerModeSwitchBusy;

        /// <summary>
        /// Reads the current mode straight from the USB device list. Returns
        /// <see cref="Shared.Data.LegionControllerModes.Unknown"/> when no Legion controller is present
        /// (for example in the second or two while the pads re-enumerate).
        /// </summary>
        private static int DetectControllerModeFromHid()
        {
            try
            {
                foreach (var device in DeviceList.Local.GetHidDevices(0x17EF))
                {
                    int mode = LegionControllerModes.FromProductId(device.ProductID);
                    if (mode != LegionControllerModes.Unknown)
                        return mode;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"DetectControllerModeFromHid failed: {ex.Message}");
            }
            return LegionControllerModes.Unknown;
        }

        /// <summary>
        /// Called when the controller VID:PID is (re)detected - including after the pads
        /// re-enumerate following a mode switch - so the mode follows the hardware.
        /// </summary>
        private void UpdateControllerModeFromVidPid(string vidPid)
        {
            int mode = LegionControllerModes.FromVidPid(vidPid);
            if (mode == LegionControllerModes.Unknown) return;
            // Mid-switch the pads can briefly report the old PID; the switch task publishes
            // the final result itself.
            if (Volatile.Read(ref controllerModeSwitchBusy) == 1) return;
            PublishControllerMode(mode);
        }

        private void PublishControllerMode(int mode)
        {
            detectedControllerMode = mode;
            if (LegionControllerMode != null && LegionControllerMode.Value != mode)
            {
                Logger.Info($"Controller mode is now {DescribeControllerMode(mode)}");
                LegionControllerMode.SetValueAndSync(mode);
            }
        }

        private static string DescribeControllerMode(int mode)
        {
            switch (mode)
            {
                case LegionControllerModes.XInput: return "X-Input";
                case LegionControllerModes.DInput: return "D-Input";
                case LegionControllerModes.DualDInput: return "Dual D-Input (detached)";
                case LegionControllerModes.Fps: return "FPS mode";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Called by the LegionControllerMode property whenever its value changes - from the
        /// widget (a switch request) or from this manager (a hardware report, which already
        /// matches <see cref="detectedControllerMode"/> and is ignored here).
        /// </summary>
        public void OnControllerModeRequested(int requested)
        {
            if (!isLegionGoDetected) return;
            if (requested != LegionControllerModes.XInput && requested != LegionControllerModes.DInput) return;
            if (requested == detectedControllerMode) return;

            // Only the two switchable states can be changed: with the controllers detached
            // (dual D-Input) or the FPS switch on, the firmware picks the mode itself.
            if (detectedControllerMode != LegionControllerModes.XInput && detectedControllerMode != LegionControllerModes.DInput)
            {
                Logger.Warn($"Controller mode change to {DescribeControllerMode(requested)} refused: controllers are in {DescribeControllerMode(detectedControllerMode)}");
                PublishControllerModeSoon(detectedControllerMode);
                return;
            }

            _ = Task.Run(() => SwitchControllerModeAsync(requested));
        }

        // Re-publish from a different call stack than the property's own change handler.
        private void PublishControllerModeSoon(int mode)
        {
            _ = Task.Run(() =>
            {
                try { LegionControllerMode?.SetValueAndSync(mode); }
                catch (Exception ex) { Logger.Debug($"PublishControllerModeSoon failed: {ex.Message}"); }
            });
        }

        private async Task SwitchControllerModeAsync(int requested)
        {
            if (Interlocked.CompareExchange(ref controllerModeSwitchBusy, 1, 0) != 0)
            {
                Logger.Warn("Controller mode switch already in progress - ignoring request");
                return;
            }

            int previous = detectedControllerMode;
            int result = previous;
            try
            {
                Logger.Info($"Switching controller mode: {DescribeControllerMode(previous)} -> {DescribeControllerMode(requested)}");

                bool sent;
                using (var controller = new LegionGoController())
                {
                    if (!controller.Connect())
                    {
                        Logger.Warn("Cannot switch controller mode: controller not connected");
                        return;
                    }

                    sent = controller.SetGamepadMode(requested == LegionControllerModes.DInput
                        ? GamepadInputMode.DInput
                        : GamepadInputMode.XInput);
                }

                if (!sent)
                {
                    Logger.Warn("Controller mode command could not be sent");
                    return;
                }

                // The pads drop off the bus and come back under the new product ID.
                int waited = 0;
                while (waited < ControllerModeSwitchTimeoutMs)
                {
                    await Task.Delay(ControllerModePollIntervalMs).ConfigureAwait(false);
                    waited += ControllerModePollIntervalMs;

                    int seen = DetectControllerModeFromHid();
                    if (seen == requested)
                    {
                        result = seen;
                        Logger.Info($"Controller mode switch complete: now {DescribeControllerMode(result)} (after ~{waited} ms)");
                        break;
                    }
                }

                if (result != requested)
                {
                    int last = DetectControllerModeFromHid();
                    result = last != LegionControllerModes.Unknown ? last : previous;
                    Logger.Warn($"Controller mode did not change to {DescribeControllerMode(requested)} within {ControllerModeSwitchTimeoutMs} ms (still {DescribeControllerMode(result)})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Controller mode switch failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref controllerModeSwitchBusy, 0);
                // Publish what the hardware is really in (this is also what un-flips the
                // widget's switch when the change didn't take).
                detectedControllerMode = result;
                try { LegionControllerMode?.SetValueAndSync(result); }
                catch (Exception ex) { Logger.Debug($"Publishing controller mode failed: {ex.Message}"); }
            }
        }
    }
}
