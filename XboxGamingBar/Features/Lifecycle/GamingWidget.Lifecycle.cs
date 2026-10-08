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

        private bool tornDown = false;

        private void GamingWidget_Unloaded(object sender, RoutedEventArgs e)
        {
            TearDown("page unloaded");
        }

        /// <summary>
        /// Called by App when Game Bar closes this instance's window. Closing a widget window does
        /// NOT raise the page's Unloaded event, so without this the dead instance stayed registered
        /// as the active widget and subscribed to the pipe: the next helper pushes were handed to
        /// its closed window's dispatcher and failed with "COM object ... separated from its
        /// underlying RCW" until the relaunched window registered itself.
        /// </summary>
        public void OnWindowClosed()
        {
            TearDown("widget window closed");
        }

        private void TearDownStep(string name, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                // A closed window's XAML/WinRT objects throw once they are detached; one failing
                // step must not stop the remaining cleanup (above all the unregister + pipe detach).
                Logger.Warn($"GamingWidget teardown step '{name}' failed: {ex.Message}");
            }
        }

        private void TearDown(string reason)
        {
            // Runs from both the page's Unloaded event and the window-closed notification.
            if (tornDown) return;
            tornDown = true;

            // Set flag immediately to prevent any pending async operations from updating UI
            isUnloading = true;

            Logger.Info($"GamingWidget teardown ({reason}). Widget is null: {widget == null}, WidgetActivity is null: {widgetActivity == null}, Pipe connected: {App.IsConnected}, Instance hash: {GetHashCode()}");

            // Unregister this instance as the active widget and detach its pipe handlers FIRST so
            // helper pushes stop reaching a dead window even if a later step throws. The
            // active-instance guards inside those handlers already make a stale instance's
            // callbacks a no-op, but leaving them subscribed across repeated Game-Bar-driven
            // instance recreations lets dead GamingWidget instances pile up on the static
            // App.PipeMessageReceived event.
            TearDownStep("unregister active widget", () =>
            {
                App.UnregisterActiveGamingWidget(this);
                App.PipeMessageReceived -= PipeClient_MessageReceived;
                App.PipeDisconnected -= PipeClient_Disconnected;
                Logger.Info("GamingWidget instance unregistered and pipe handlers detached.");
            });

            // Unsubscribe from power source changes
            TearDownStep("power source events", () =>
            {
                PowerManager.PowerSupplyStatusChanged -= PowerManager_PowerSourceChanged;
                if (PowerSourceProfileToggle != null)
                {
                    PowerSourceProfileToggle.Toggled -= PowerSourceProfileToggle_Toggled;
                }
            });

            TearDownStep("Game Bar widget events", () =>
            {
                if (widget != null)
                {
                    widget.RequestedThemeChanged -= GamingWidget_RequestedThemeChanged;
                    widget.SettingsClicked -= GamingWidget_SettingsClicked;
                    widget.VisibleChanged -= GamingWidget_VisibleChanged;
                    widget.GameBarDisplayModeChanged -= GamingWidget_GameBarDisplayModeChanged;
                }
            });

            // Stop power source TDP reapply timer
            TearDownStep("power source timer", () =>
            {
                if (powerSourceTdpReapplyTimer != null)
                {
                    powerSourceTdpReapplyTimer.Stop();
                    powerSourceTdpReapplyTimer = null;
                }
            });

            // Stop reconnection timeout timer
            TearDownStep("reconnection timer", StopReconnectionTimeoutTimer);

            // Unsubscribe from Lossless Scaling property changes
            TearDownStep("Lossless Scaling events", () =>
            {
                if (losslessScalingInstalled != null)
                {
                    losslessScalingInstalled.PropertyChanged -= LosslessScalingStatus_PropertyChanged;
                }
                if (losslessScalingRunning != null)
                {
                    losslessScalingRunning.PropertyChanged -= LosslessScalingStatus_PropertyChanged;
                }
            });
            Logger.Info("Event handlers unregistered.");

            // Clean up properties (stop debounce timers, unregister slider events)
            Logger.Info("Cleaning up properties...");
            TearDownStep("properties cleanup", () => properties.Cleanup());
            Logger.Info("Properties cleaned up.");

            // Clean up widget activity - capture to local var to avoid race condition
            var activity = widgetActivity;
            if (activity != null)
            {
                Logger.Info("Completing widget activity during teardown.");
                try
                {
                    activity.Complete();
                    Logger.Info("Widget activity completed and disposed.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error completing widget activity during teardown: {ex.Message}");
                }
                finally
                {
                    widgetActivity = null;
                }
            }
            else
            {
                Logger.Info("No widget activity to clean up during teardown.");
            }

            // Reset Quick Settings initialized flag so next instance starts fresh
            quickSettingsInitialized = false;

            Logger.Info($"GamingWidget teardown completed ({reason}).");
        }

        // ---- System resume handling -------------------------------------------------------

        private readonly DateTime instanceCreatedUtc = DateTime.UtcNow;

        // Give Game Bar a moment after the wake before recycling, and ignore a resume signal
        // that is older than this (it sat in the pipe while the process was suspended).
        private const int ResumeRecycleDelayMs = 4000;
        private const double ResumeSignalMaxAgeSeconds = 60;

        /// <summary>
        /// The helper reported that the PC woke from sleep/hibernate. After a long sleep Game Bar
        /// can drop its link to an already-open widget window: the window still draws and still
        /// receives helper pushes but never hears a Game Bar event or input again (the widget opens
        /// but nothing in it responds). Ask Game Bar to close this window so the next open is a
        /// clean launch. Skipped when the window is visible, was created after the wake, or the
        /// signal is stale. Everything is logged so a failure shows up in the widget log.
        /// </summary>
        private async Task HandleSystemResumedAsync(long resumedUtcTicks)
        {
            try
            {
                DateTime resumedUtc;
                try
                {
                    resumedUtc = new DateTime(resumedUtcTicks, DateTimeKind.Utc);
                }
                catch (ArgumentOutOfRangeException)
                {
                    Logger.Warn($"Resume signal ignored: invalid timestamp {resumedUtcTicks}");
                    return;
                }

                double ageSeconds = (DateTime.UtcNow - resumedUtc).TotalSeconds;
                double instanceAgeSeconds = (DateTime.UtcNow - instanceCreatedUtc).TotalSeconds;
                Logger.Info($"Resume signal from helper: age={ageSeconds:F1}s, this window is {instanceAgeSeconds:F0}s old.");

                if (resumedUtc < instanceCreatedUtc)
                {
                    Logger.Info("Resume signal ignored: this widget window was opened after the wake, so it is fresh.");
                    return;
                }
                if (ageSeconds > ResumeSignalMaxAgeSeconds)
                {
                    Logger.Info("Resume signal ignored: too old (it was queued while the app was suspended).");
                    return;
                }

                await Task.Delay(ResumeRecycleDelayMs);

                if (isUnloading || App.GetActiveGamingWidget() != this || Dispatcher == null)
                {
                    Logger.Info("Resume recycle skipped: this window is no longer the active widget.");
                    return;
                }

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        if (widget == null)
                        {
                            Logger.Info("Resume recycle skipped: no Game Bar widget object.");
                            return;
                        }
                        if (widget.Visible)
                        {
                            Logger.Info("Resume recycle skipped: the widget is on screen right now.");
                            return;
                        }

                        Logger.Info("Resume recycle: asking Game Bar to close this widget window so the next open starts fresh.");
                        var widgetControl = new XboxGameBarWidgetControl(widget);
                        var closeTask = widgetControl.CloseAsync("GamingWidget").AsTask();
                        var finished = await Task.WhenAny(closeTask, Task.Delay(5000));
                        if (finished == closeTask)
                        {
                            await closeTask; // surface any exception to the catch below
                            Logger.Info("Resume recycle: Game Bar accepted the close request.");
                        }
                        else
                        {
                            Logger.Warn("Resume recycle: Game Bar did not answer CloseAsync within 5s (window left open).");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Resume recycle failed: {ex.GetType().Name}: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"HandleSystemResumedAsync failed: {ex.Message}");
            }
        }

        public void OnDeactivated()
        {
            Logger.Info($"=== GamingWidget.OnDeactivated START === Instance hash: {this.GetHashCode()}");
            try
            {
                // Must run on UI thread since DispatcherTimer is UI-bound
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        properties.StopPendingUpdates();
                        Logger.Info("Pending updates stopped.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error stopping pending updates: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                // Expected when Game Bar has already torn down the old instance — accessing
                // Dispatcher on a detached Page throws RCW separated. Nothing to clean up
                // anyway; the new active instance owns the timers now.
                Logger.Debug($"Deactivation cleanup skipped (instance already detached): {ex.Message}");
            }
        }

        private bool chillFPSHandlersRegistered = false;

    }
}
