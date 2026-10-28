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

        private void ClearNintendoLayoutMappings()
        {
            var nintendoButtons = new[] { "A", "B", "X", "Y" };

            // Set each button to reset state (Type=0, GamepadAction=0) to trigger HID reset
            foreach (var button in nintendoButtons)
            {
                gamepadButtonMappings[button] = new ButtonMapping { Type = 0, GamepadAction = 0 };
            }
            SaveAndSendGamepadMappings();

            // Remove from dictionary after sending reset
            foreach (var button in nintendoButtons)
            {
                gamepadButtonMappings.Remove(button);
            }

            Logger.Info("Cleared Nintendo layout mappings for A, B, X, Y");
        }

    }
}
