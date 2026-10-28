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
        /// Serializes gamepad button mappings dictionary to JSON string.
        /// Format: {"ButtonName":{Type:0,GamepadAction:5,...},...}
        /// </summary>
        private string SerializeGamepadButtonMappings(Dictionary<string, ButtonMapping> mappings)
        {
            if (mappings == null || mappings.Count == 0)
                return "{}";

            // Output nested JSON objects (not escaped strings)
            var entries = mappings.Select(kvp =>
                $"\"{kvp.Key}\":{kvp.Value.ToJson()}");
            return "{" + string.Join(",", entries) + "}";
        }

        /// <summary>
        /// Deserializes JSON string to gamepad button mappings dictionary.
        /// Format: {"ButtonName":{Type:0,...},...}
        /// </summary>
        private Dictionary<string, ButtonMapping> DeserializeGamepadButtonMappings(string json)
        {
            var result = new Dictionary<string, ButtonMapping>();
            if (string.IsNullOrEmpty(json) || json == "{}")
                return result;

            // Match patterns like "ButtonName":{...}
            var regex = new System.Text.RegularExpressions.Regex("\"(\\w+)\"\\s*:\\s*(\\{[^}]+\\})");
            var matches = regex.Matches(json);

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count >= 3)
                {
                    var buttonName = match.Groups[1].Value;
                    var mappingJson = match.Groups[2].Value;
                    result[buttonName] = ButtonMapping.FromJson(mappingJson);
                }
            }

            return result;
        }

        private void SaveAndSendGamepadMappings()
        {
            // Don't save during profile loading - we're just applying the profile, not modifying it
            // The profile will be fully applied and any saves will happen after isLoadingControllerProfile is cleared
            if (isLoadingControllerProfile)
            {
                // Skip sending if this is a duplicate call during profile loading
                // (the main send will happen via SendButtonMappingsToHelper at the end of ApplyControllerProfile)
                return;
            }

            // Skip if a profile was just applied (prevents duplicate sends from queued UI events)
            // HID commands take ~1.5s to complete, so use 2 second window
            if ((DateTime.Now - lastProfileApplyTime).TotalMilliseconds < 2000)
            {
                Logger.Info("SaveAndSendGamepadMappings skipped - profile was just applied");
                return;
            }

            // Get current profile
            ControllerProfile currentProfile;
            globalControllerProfile = GetCurrentControllerProfileFromUI();
            SaveControllerProfileToStorage("Global", globalControllerProfile);
            currentProfile = globalControllerProfile;

            // Send to helper
            SendButtonMappingsToHelper(currentProfile);
        }

        private string GetGamepadActionName(int action)
        {
            string[] names = { "Disabled", "LS Click", "LS Up", "LS Down", "LS Left", "LS Right",
                              "RS Click", "RS Up", "RS Down", "RS Left", "RS Right",
                              "D-Up", "D-Down", "D-Left", "D-Right",
                              "A", "B", "X", "Y", "LB", "LT", "RB", "RT", "View", "Menu" };
            return action >= 0 && action < names.Length ? names[action] : $"Action{action}";
        }

    }
}
