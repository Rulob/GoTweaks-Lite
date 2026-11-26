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

        /// <summary>
        /// Apply default hotkey settings if not already configured
        /// </summary>
        private void ApplyHotkeyDefaults()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                // Check if defaults have already been applied
                if (settings.Values.ContainsKey("Hotkey_DefaultsApplied"))
                    return;

                // Apply defaults:
                // View + A: Disabled (0) - was Ctrl+Alt+Del but that cannot be simulated
                // View + B: Open Virtual Keyboard (7)
                // View + X: Screenshot (4)
                // View + Y: Focus GoTweaks (10)
                settings.Values["Hotkey_MenuA_Action"] = (int)HotkeyAction.Disabled;
                settings.Values["Hotkey_MenuB_Action"] = (int)HotkeyAction.OpenKeyboard;
                settings.Values["Hotkey_MenuX_Action"] = (int)HotkeyAction.Screenshot;
                settings.Values["Hotkey_MenuY_Action"] = (int)HotkeyAction.FocusGoTweaks;
                settings.Values["Hotkey_DefaultsApplied"] = true;

                Logger.Info("Hotkey defaults applied: A=Disabled, B=OpenKeyboard, X=Screenshot, Y=FocusGoTweaks");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error applying hotkey defaults: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the default hotkey bindings on first run. The View+ABXY / Menu+D-Pad combos
        /// themselves are detected and run only by the helper (ControllerHotkeyMonitor), which
        /// polls the controller in games and on the desktop alike. Until 1.9.8b the widget also
        /// watched the same combos through Game Bar, so a combo could run its action twice
        /// (Toggle OSD turning the overlay on and straight back off).
        /// </summary>
        private void InitializeHotkeyWatchers()
        {
            ApplyHotkeyDefaults();
            // The defaults may have been written just now, after the connect-time send.
            SendControllerHotkeyConfigToHelper();
        }

    }
}
