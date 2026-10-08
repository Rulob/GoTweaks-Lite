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

        private async void GamingWidget_RequestedThemeChanged(XboxGameBarWidget sender, object args)
        {
            Logger.Info("Game Bar event: RequestedThemeChanged");
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                SetBackgroundColor();
            });
        }

        private async void GamingWidget_SettingsClicked(XboxGameBarWidget sender, object args)
        {
            Logger.Info("Game Bar event: SettingsClicked");
            await widget.ActivateSettingsAsync();
        }

        // Diagnostics for the "widget opens but nothing responds" report: logs the FIRST pointer or
        // key input that reaches the widget after each Game Bar visibility change. A window that is
        // open but cut off from Game Bar shows VisibleChanged lines (or none at all) and then never
        // this line however much is tapped; a healthy one logs it on the first tap or button press.
        private bool firstInputLoggedSinceVisibleChange = false;

        private void GamingWidget_InputProbe(string kind)
        {
            if (firstInputLoggedSinceVisibleChange) return;
            firstInputLoggedSinceVisibleChange = true;
            Logger.Info($"Input reached the widget: first {kind} since Game Bar visibility last changed.");
        }

        private void GamingWidget_PointerProbe(object sender, PointerRoutedEventArgs e) => GamingWidget_InputProbe("pointer press");

        private void GamingWidget_KeyProbe(object sender, KeyRoutedEventArgs e) => GamingWidget_InputProbe("key/gamepad press");

        private bool hasAppliedInitialSize = false;

        private async void GamingWidget_VisibleChanged(XboxGameBarWidget sender, object args)
        {
            try
            {
                bool isVisible = sender?.Visible ?? false;
                Logger.Info($"GamingWidget_VisibleChanged: Visible={isVisible}, DisplayMode={sender?.GameBarDisplayMode.ToString() ?? "Unknown"}");
                firstInputLoggedSinceVisibleChange = false; // re-arm the input probe (see GamingWidget_InputProbe)
                UpdateGameBarForegroundSignal("VisibleChanged");

                // Re-read the built-in panel brightness on every open so the optional brightness
                // slider always shows the true current value (it may have changed from another
                // source) and grays out when the internal panel is off.
                if (isVisible)
                {
                    RefreshPanelBrightness();

                    // Re-paint the Legion controller battery/connection/VID:PID display on every
                    // open. The underlying property values are usually already correct by now (the
                    // real battery reading typically arrives 180ms-1s after the widget's first
                    // BatchGet on a cold helper start), but the very first live-push repaint attempt
                    // can lose the race against the widget's own startup/dispatcher churn and throw
                    // RPC_E_WRONG_THREAD, which UpdateLegionControllerBatteryDisplay's catch block
                    // silently swallows - leaving the UI stuck on stale/blank values even though the
                    // data itself is fine. This gives the paint a guaranteed second chance on the
                    // next open instead of requiring the user to notice and manually reopen.
                    //
                    // VisibleChanged is raised by Game Bar OFF the XAML UI thread, so the repaint has
                    // to be marshalled onto it: called directly it failed with RPC_E_WRONG_THREAD
                    // (0x8001010E) on every single open and the repaint never actually happened.
                    var ignoreRepaint = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        UpdateLegionControllerBatteryDisplay();
                        UpdateLegionControllerVidPidDisplay();
                    });

                    // Put gamepad focus on the active tab so the pad can navigate immediately on
                    // open — but only if nothing in the widget is focused yet (don't steal focus
                    // if the user is already interacting). Deferred so layout has settled.
                    var ignoreFocus = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                    {
                        if (FocusManager.GetFocusedElement() == null)
                        {
                            FocusActiveNavItem();
                        }
                    });
                }

                // Resize to full height on first activation.
                // Delay to let Game Bar finish restoring its cached layout first.
                if (isVisible && !hasAppliedInitialSize && sender != null)
                {
                    hasAppliedInitialSize = true;
                    await Task.Delay(500);
                    var success = await sender.TryResizeWindowAsync(new Windows.Foundation.Size(464, 1080));
                    Logger.Info($"Initial TryResizeWindowAsync(464x1080): success={success}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"GamingWidget_VisibleChanged failed: {ex.Message}");
            }
        }

        private void GamingWidget_GameBarDisplayModeChanged(XboxGameBarWidget sender, object args)
        {
            try
            {
                Logger.Info($"GamingWidget_GameBarDisplayModeChanged: DisplayMode={sender?.GameBarDisplayMode.ToString() ?? "Unknown"}, Visible={sender?.Visible ?? false}");
                UpdateGameBarForegroundSignal("GameBarDisplayModeChanged");
            }
            catch (Exception ex)
            {
                Logger.Error($"GamingWidget_GameBarDisplayModeChanged failed: {ex.Message}");
            }
        }

        private void UpdateGameBarForegroundSignal(string source)
        {
            try
            {
                bool widgetVisible = widget?.Visible ?? false;
                XboxGameBarDisplayMode displayMode = widget?.GameBarDisplayMode ?? XboxGameBarDisplayMode.Foreground;

                // Full Game Bar visibility signal:
                // - true when the overlay is foreground (even if this specific widget tab is not selected)
                // - false when app is backgrounded or Game Bar is not in foreground mode
                bool gameBarForeground = !appIsInBackground && displayMode == XboxGameBarDisplayMode.Foreground;
                isForeground.ForceSetValue(gameBarForeground);

                Logger.Info($"GameBar foreground signal update ({source}): value={gameBarForeground}, displayMode={displayMode}, widgetVisible={widgetVisible}, appBackground={appIsInBackground}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"UpdateGameBarForegroundSignal failed ({source}): {ex.Message}");
            }
        }

    }
}
