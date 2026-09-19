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
        // OSD configuration for the single overlay layout - which items are enabled.
        // Default: FPS, CPU, GPU, RAM, VRAM, Battery, Time (24h). Everything else stays
        // available below but off until the user turns it on.
        private Dictionary<string, bool> osdItemConfig = new Dictionary<string, bool>
        {
            { "FPS", true }, { "CPU", true }, { "GPU", true }, { "Memory", true }, { "VRAM", true },
            { "Battery", true }, { "Time", true },
            { "ControllerBattery", false }, { "CPUClock", false }, { "GPUClock", false },
            { "FrameBudget", false }, { "Fan", false }, { "TDPLimits", false }, { "AutoTDP", false },
            { "FrametimeGraph", false }
        };

        private string osdCustomTags = "";

        // Overlay column count (items per row; 1 = vertical list)
        private int osdColumns = 3;

        // Overlay item order (list of item IDs in display order)
        private List<string> osdItemOrder = new List<string>
        {
            "FPS", "CPU", "GPU", "Memory", "VRAM", "Battery", "Time",
            "ControllerBattery", "CPUClock", "GPUClock", "FrameBudget", "Fan", "TDPLimits", "AutoTDP", "FrametimeGraph"
        };

        // Per-item label colors (DEFAULT = use global text color)
        private Dictionary<string, string> osdItemLabelColors = new Dictionary<string, string>();

        // Item display names for UI
        private static readonly Dictionary<string, string> osdItemDisplayNames = new Dictionary<string, string>
        {
            { "Time", "Time (24-hour)" },
            { "FPS", "FPS & Frametime" },
            { "Battery", "Battery" },
            { "ControllerBattery", "Controller Battery (L/R)" },
            { "Memory", "Memory (RAM)" },
            { "VRAM", "VRAM (GPU Memory)" },
            { "CPU", "CPU (Usage, Wattage, Temp)" },
            { "CPUClock", "CPU Clock Speed" },
            { "GPU", "GPU (Usage, Wattage, Temp)" },
            { "GPUClock", "GPU Clock Speed" },
            { "FrameBudget", "Frame Budget (CPU/GPU bound %)" },
            { "Fan", "Fan Speed" },
            { "TDPLimits", "TDP Limits (SPL/SPPT/FPPT)" },
            { "AutoTDP", "AutoTDP Status" },
            { "FrametimeGraph", "Frametime Graph" }
        };

        // Observable collection for OSD items UI
        private ObservableCollection<OSDItemViewModel> osdItemViewModels = new ObservableCollection<OSDItemViewModel>();

        // Global OSD layout settings
        private int osdTextSize = 100;    // Percentage: 50=Small, 100=Medium, 150=Large, 200=X-Large, 250=XX-Large, 300=XXX-Large
        private string osdTextColor = "DYNAMIC";  // DYNAMIC = value-based colors, or hex color code
        private string osdLabelColor = "DEFAULT";  // DEFAULT = use item-specific colors, or hex color code
        private int osdProvider = 0;  // 0=RTSS, 1=AMD
        private int amdOverlayLevel = 0;  // Track AMD overlay level: 0=Off, 1-4=Level 1-4 (can't query from AMD)
        private bool isOSDCustomizeExpanded = false;
        private bool isProfileDetectionExpanded = false;
        private bool isProfileSettingsExpanded = false;
        private bool isTDPSettingsExpanded = false;
        private bool isColorSettingsExpanded = false;
        private bool isButtonRemappingExpanded = false;
        private bool isGyroSettingsExpanded = false;
        private bool isSavedProfilesExpanded = false;
        private bool isProfileSaveCategoriesExpanded = false;
        private bool isSpecialRemappingExpanded = false;
        private bool isStickDeadzonesExpanded = false;
        private bool isTouchpadVibrationExpanded = false;
        private bool isLightingExpanded = false;
        private bool isFanCurveExpanded = false;
        private bool isControllerEmulationExpanded = false;
        private bool fanCurveGraphInitialized = false;

        // Display and OSD settings
        private bool adaptiveBrightnessEnabled = false;
        private bool osdPositionShiftEnabled = false;
        private bool frametimeGraphPinned = false;
        private int osdOpacity = 100; // percentage 10-100
        private bool isLoadingOLEDSettings = false;
        private bool isLoadingPerformanceOverlaySetting = false;
        private readonly Windows.UI.Xaml.Shapes.Ellipse[] fanCurvePoints = new Windows.UI.Xaml.Shapes.Ellipse[10];
        private int[] currentFanCurveValues = new int[10];
        private int draggedPointIndex = -1;
        private bool isDraggingPoint = false;

        // Legion Go fan curve temperature thresholds (°C) - FIXED by EC at 10°C increments
        private static readonly int[] FanCurveTemperatures = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        // Minimum fan speeds - set to 0 since EC enforces its own thermal protection floor
        // EC override floor: 0-44°C=0%, 45°C=27%, 50°C=40%, 55°C=55%, 60°C=65%, 70°C=85%, 80+°C=100%
        private static readonly int[] FanCurveMinSpeeds = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        // EC Override Floor points: (temperature, minimum fan %) - what the EC enforces regardless of user curve
        private static readonly (int temp, int floor)[] ECFloorPoints = new[]
        {
            (10, 0), (20, 0), (30, 0), (44, 0),
            (45, 27), (50, 40), (55, 55), (60, 65), (70, 85),
            (80, 100), (90, 100), (100, 100)
        };
        // Fan curve preset definitions (values are fan % for temps 10,20,30,40,50,60,70,80,90,100°C)
        private static readonly Dictionary<string, int[]> FanCurvePresets = new Dictionary<string, int[]>
        {
            { "Silent", new int[] { 0, 0, 0, 27, 30, 40, 55, 65, 80, 100 } },       // Silent (Safe)
            { "Balanced", new int[] { 0, 0, 25, 30, 35, 45, 55, 70, 85, 100 } },    // Balanced
            { "Performance", new int[] { 30, 35, 40, 45, 50, 60, 70, 80, 90, 100 } }, // Performance
            { "MaxCooling", new int[] { 40, 45, 50, 55, 60, 70, 80, 90, 100, 100 } }  // Max Cooling
        };
        private bool isFanCurvePresetLoading = false;
        private bool isCPUExtrasExpanded = false;
        private bool isDebugExpanded = false;
        private int deviceTDPMin = 4;
        private int deviceTDPMax = 35;


        private bool isLoadingOSDConfig = false;

        private void OSDProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingOSDConfig) return;

            if (OSDProviderComboBox?.SelectedItem is ComboBoxItem selected && selected.Tag is string tagStr)
            {
                if (int.TryParse(tagStr, out int provider))
                {
                    int previousProvider = osdProvider;
                    osdProvider = provider;

                    // Save to storage
                    try
                    {
                        ApplicationData.Current.LocalSettings.Values["OSD_Provider"] = osdProvider;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error saving OSD provider: {ex.Message}");
                    }

                    // Update UI visibility
                    UpdateOSDProviderUI();

                    // When switching providers, disable the other one
                    if (provider == 0) // RTSS
                    {
                        // Disable AMD overlay if it was enabled (send Ctrl+Shift+O to toggle off)
                        if (previousProvider == 1 && amdOverlayLevel > 0)
                        {
                            SendAMDOverlayToggle();
                            amdOverlayLevel = 0;
                            SaveAMDOverlayLevel();
                        }
                        // Enable RTSS OSD by sending config
                        SendOSDConfigToHelper();
                    }
                    else if (provider == 1) // AMD
                    {
                        // Disable RTSS OSD by setting level to 0
                        if (osd != null)
                        {
                            osd.SetValue(0);
                        }
                        // Don't auto-toggle AMD overlay - we can't know its actual state
                        // User should manually enable via Quick Settings tile if needed
                    }

                    // Update Quick Settings tiles
                    UpdateQuickSettingsTileStates();

                    Logger.Info($"OSD Provider changed to: {(provider == 0 ? "RTSS" : "AMD")}");
                }
            }
        }

        private void UpdateOSDProviderUI()
        {
            if (RTSSOptionsPanel != null)
            {
                RTSSOptionsPanel.Visibility = osdProvider == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (AMDOptionsPanel != null)
            {
                AMDOptionsPanel.Visibility = osdProvider == 1 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void SendAMDOverlayToggle()
        {
            // Send Ctrl+Shift+O to toggle AMD Adrenaline's metrics overlay on/off
            // Use helper's InputInjector since UWP widget can't use SendInput directly
            try
            {
                if (App.IsConnected)
                {
                    var request = new Windows.Foundation.Collections.ValueSet();
                    request.Add("SendKeyboardShortcut", "Ctrl+Shift+O");
                    await App.SendMessageAsync(request);
                    Logger.Info("Sent AMD overlay toggle hotkey (Ctrl+Shift+O) via helper");
                }
                else
                {
                    Logger.Warn("Cannot send AMD overlay toggle - not connected to helper");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending AMD overlay toggle: {ex.Message}");
            }
        }

        private async void CycleAMDOverlayLevel()
        {
            // Send Ctrl+Shift+X to cycle AMD Adrenaline's metrics overlay levels
            // Use helper's InputInjector since UWP widget can't use SendInput directly
            try
            {
                if (App.IsConnected)
                {
                    var request = new Windows.Foundation.Collections.ValueSet();
                    request.Add("SendKeyboardShortcut", "Ctrl+Shift+X");
                    await App.SendMessageAsync(request);
                    Logger.Info("Sent AMD overlay cycle hotkey (Ctrl+Shift+X) via helper");
                }
                else
                {
                    Logger.Warn("Cannot cycle AMD overlay level - not connected to helper");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error cycling AMD overlay level: {ex.Message}");
            }
        }

        private void LoadOSDOptions()
        {
            isLoadingOSDConfig = true;
            try
            {
                // Refresh the OSD items control with the current order and enabled states
                RefreshOSDItemsControl();

                if (OSDCustomTagsTextBox != null) OSDCustomTagsTextBox.Text = osdCustomTags;

                if (OSDColumnsComboBox != null)
                {
                    foreach (ComboBoxItem item in OSDColumnsComboBox.Items)
                    {
                        if (item.Tag is string tag && int.TryParse(tag, out int val) && val == osdColumns)
                        {
                            OSDColumnsComboBox.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
            finally
            {
                isLoadingOSDConfig = false;
            }
        }

        /// <summary>
        /// Refreshes the OSD items control with the current order and enabled states
        /// </summary>
        private void RefreshOSDItemsControl()
        {
            if (OSDItemsControl == null) return;

            var order = osdItemOrder;
            var config = osdItemConfig;

            osdItemViewModels.Clear();
            var labelColors = osdItemLabelColors;
            for (int i = 0; i < order.Count; i++)
            {
                var id = order[i];
                osdItemViewModels.Add(new OSDItemViewModel
                {
                    Id = id,
                    DisplayName = osdItemDisplayNames.ContainsKey(id) ? osdItemDisplayNames[id] : id,
                    IsEnabled = config.ContainsKey(id) && config[id],
                    CanMoveUp = i > 0,
                    CanMoveDown = i < order.Count - 1,
                    LabelColor = labelColors.ContainsKey(id) ? labelColors[id] : "DEFAULT"
                });
            }

            OSDItemsControl.ItemsSource = osdItemViewModels;
        }

        private void OSDItemCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (isLoadingOSDConfig) return;

            if (sender is CheckBox cb && cb.Tag is string itemId)
            {
                osdItemConfig[itemId] = cb.IsChecked == true;

                SaveOSDConfigToStorage();
                SendOSDConfigToHelper();
            }
        }

        private void OSDItemMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string itemId)
            {
                var order = osdItemOrder;
                int index = order.IndexOf(itemId);
                if (index > 0)
                {
                    order.RemoveAt(index);
                    order.Insert(index - 1, itemId);
                    RefreshOSDItemsControl();
                    SaveOSDConfigToStorage();
                    SendOSDConfigToHelper();
                }
            }
        }

        private void OSDItemMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string itemId)
            {
                var order = osdItemOrder;
                int index = order.IndexOf(itemId);
                if (index >= 0 && index < order.Count - 1)
                {
                    order.RemoveAt(index);
                    order.Insert(index + 1, itemId);
                    RefreshOSDItemsControl();
                    SaveOSDConfigToStorage();
                    SendOSDConfigToHelper();
                }
            }
        }

        private void OSDCustomTagsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isLoadingOSDConfig) return;

            SaveCurrentOSDConfig();
        }

        private void SaveCurrentOSDConfig()
        {
            // Item enabled states are already in osdItemConfig (updated by OSDItemCheckBox_Changed)
            // Just save custom tags and columns here

            osdCustomTags = OSDCustomTagsTextBox?.Text ?? "";

            if (OSDColumnsComboBox?.SelectedItem is ComboBoxItem colItem && colItem.Tag is string colTag)
            {
                if (int.TryParse(colTag, out int cols))
                {
                    osdColumns = cols;
                }
            }

            SaveOSDConfigToStorage();
            SendOSDConfigToHelper();
        }

        private void SaveOSDConfigToStorage()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                foreach (var item in osdItemConfig)
                {
                    settings.Values[$"OSD_{item.Key}"] = item.Value;
                }
                settings.Values["OSD_CustomTags"] = osdCustomTags;
                settings.Values["OSD_Columns"] = osdColumns;
                settings.Values["OSD_Order"] = string.Join(",", osdItemOrder);

                foreach (var colorItem in osdItemLabelColors)
                {
                    settings.Values[$"OSD_{colorItem.Key}_Color"] = colorItem.Value;
                }

                // Save global layout settings (text size is per-resolution)
                string currentRes = resolution?.Value ?? "default";
                settings.Values[$"OSD_TextSize_{currentRes}"] = osdTextSize;
                settings.Values["OSD_TextColor"] = osdTextColor;
                settings.Values["OSD_LabelColor"] = osdLabelColor;
                settings.Values["OSD_Opacity"] = osdOpacity;
                settings.Values["OSD_FrametimeGraphPinned"] = frametimeGraphPinned;

                Logger.Info($"OSD configuration saved to storage (resolution: {currentRes}, text size: {osdTextSize}, opacity: {osdOpacity})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving OSD config: {ex.Message}");
            }
        }

        private void LoadOSDConfigFromStorage()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                var itemKeys = new[] { "FPS", "CPU", "GPU", "Memory", "VRAM", "Battery", "Time", "ControllerBattery", "CPUClock", "GPUClock", "FrameBudget", "Fan", "TDPLimits", "AutoTDP", "FrametimeGraph" };

                foreach (var key in itemKeys)
                {
                    string settingKey = $"OSD_{key}";
                    if (settings.Values.TryGetValue(settingKey, out object val) && val is bool enabled)
                    {
                        osdItemConfig[key] = enabled;
                    }
                }

                if (settings.Values.TryGetValue("OSD_CustomTags", out object tagsVal) && tagsVal is string tags)
                {
                    osdCustomTags = tags;
                }

                if (settings.Values.TryGetValue("OSD_Columns", out object colsVal) && colsVal is int cols)
                {
                    osdColumns = cols;
                }

                if (settings.Values.TryGetValue("OSD_Order", out object orderVal) && orderVal is string orderStr)
                {
                    var orderList = orderStr.Split(',').Where(s => !string.IsNullOrWhiteSpace(s) && itemKeys.Contains(s)).ToList();
                    // Only trust a saved order if it accounts for every current item - an order
                    // saved before an item was added/removed would silently drop it otherwise.
                    if (orderList.Count == itemKeys.Length)
                    {
                        osdItemOrder = orderList;
                    }
                }

                foreach (var key in itemKeys)
                {
                    string colorKey = $"OSD_{key}_Color";
                    if (settings.Values.TryGetValue(colorKey, out object colorVal) && colorVal is string color)
                    {
                        osdItemLabelColors[key] = color;
                    }
                }

                // Load global layout settings (text size is per-resolution)
                string currentRes = resolution?.Value ?? "default";
                string textSizeKey = $"OSD_TextSize_{currentRes}";
                if (settings.Values.TryGetValue(textSizeKey, out object sizeVal) && sizeVal is int size)
                {
                    osdTextSize = size;
                    Logger.Info($"Loaded OSD text size {osdTextSize} for resolution {currentRes}");
                }
                else
                {
                    // Default to 100 if no per-resolution setting exists
                    osdTextSize = 100;
                    Logger.Info($"No OSD text size saved for resolution {currentRes}, using default 100");
                }
                if (settings.Values.TryGetValue("OSD_TextColor", out object textColorVal) && textColorVal is string textColor)
                {
                    osdTextColor = textColor;
                }
                if (settings.Values.TryGetValue("OSD_LabelColor", out object labelColorVal) && labelColorVal is string labelColor)
                {
                    osdLabelColor = labelColor;
                }
                if (settings.Values.TryGetValue("OSD_Opacity", out object opacityVal) && opacityVal is int opacity)
                {
                    osdOpacity = opacity;
                }
                if (settings.Values.TryGetValue("OSD_FrametimeGraphPinned", out object pinnedVal) && pinnedVal is bool pinned)
                {
                    frametimeGraphPinned = pinned;
                    if (FrametimeGraphPinnedToggle != null)
                        FrametimeGraphPinnedToggle.IsOn = frametimeGraphPinned;
                }
                if (settings.Values.TryGetValue("OSD_Provider", out object providerVal) && providerVal is int provider)
                {
                    osdProvider = provider;
                }
                if (settings.Values.TryGetValue("AMD_OverlayLevel", out object amdLevelVal) && amdLevelVal is int amdLevel)
                {
                    amdOverlayLevel = amdLevel;
                }

                // Update layout UI
                UpdateOSDLayoutUI();
                LoadOSDOptions();

                Logger.Info("OSD configuration loaded from storage");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading OSD config: {ex.Message}");
            }
        }

        private async void SendOSDConfigToHelper()
        {
            try
            {
                if (!App.IsConnected) return;

                // Build config string to send to helper
                var configParts = new List<string>();

                // Add global layout settings
                configParts.Add($"TextSize:{osdTextSize}");
                configParts.Add($"TextColor:{osdTextColor}");
                configParts.Add($"LabelColor:{osdLabelColor}");
                configParts.Add($"Opacity:{osdOpacity}");
                configParts.Add($"FrametimeGraphPinned:{(frametimeGraphPinned ? "1" : "0")}");

                // Add item configuration
                var enabledItems = new List<string>();
                foreach (var item in osdItemConfig)
                {
                    if (item.Value)
                    {
                        enabledItems.Add(item.Key);
                    }
                }
                configParts.Add($"Items:{string.Join(",", enabledItems)}");

                if (!string.IsNullOrWhiteSpace(osdCustomTags))
                {
                    configParts.Add($"Custom:{osdCustomTags}");
                }

                configParts.Add($"Columns:{osdColumns}");
                configParts.Add($"Order:{string.Join(",", osdItemOrder)}");

                foreach (var colorItem in osdItemLabelColors)
                {
                    if (!string.IsNullOrEmpty(colorItem.Value) && colorItem.Value != "DEFAULT")
                    {
                        configParts.Add($"{colorItem.Key}_Color:{colorItem.Value}");
                    }
                }

                var configString = string.Join(";", configParts);
                var request = new Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.OSDConfig },
                    { "Content", configString },
                    { "UpdatedTime", DateTimeOffset.Now.Ticks }
                };
                await App.SendMessageAsync(request);

                Logger.Info($"OSD config sent to helper: {configString}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending OSD config to helper: {ex.Message}");
            }
        }

        private void OSDCustomizeExpandButton_Click(object sender, RoutedEventArgs e)
        {
            isOSDCustomizeExpanded = !isOSDCustomizeExpanded;

            if (OSDCustomizeContent != null)
            {
                OSDCustomizeContent.Visibility = isOSDCustomizeExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (OSDCustomizeExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                OSDCustomizeExpandIcon.Glyph = isOSDCustomizeExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void OSDOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (isLoadingOSDConfig) return;
            osdOpacity = (int)Math.Round(e.NewValue);
            if (OSDOpacityValue != null)
                OSDOpacityValue.Text = $"{osdOpacity}%";
            SaveOSDConfigToStorage();
            SendOSDConfigToHelper();
        }
        private void ProfileSettingsExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            isProfileSettingsExpanded = !isProfileSettingsExpanded;

            if (ProfileSettingsContent != null)
            {
                ProfileSettingsContent.Visibility = isProfileSettingsExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ProfileSettingsExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                ProfileSettingsExpandIcon.Glyph = isProfileSettingsExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void ProfileDetectionExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            isProfileDetectionExpanded = !isProfileDetectionExpanded;

            if (ProfileDetectionContent != null)
            {
                ProfileDetectionContent.Visibility = isProfileDetectionExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ProfileDetectionExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                ProfileDetectionExpandIcon.Glyph = isProfileDetectionExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void ButtonRemappingExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            isButtonRemappingExpanded = !isButtonRemappingExpanded;

            if (ButtonRemappingContent != null)
            {
                ButtonRemappingContent.Visibility = isButtonRemappingExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ButtonRemappingExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                ButtonRemappingExpandIcon.Glyph = isButtonRemappingExpanded ? "\uE70E" : "\uE70D";
            }

            if (isButtonRemappingExpanded)
            {
                RefreshLegionEnhancedRemapUi();
            }
        }

        private void GyroSettingsExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            isGyroSettingsExpanded = !isGyroSettingsExpanded;

            if (GyroSettingsContent != null)
            {
                GyroSettingsContent.Visibility = isGyroSettingsExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (GyroSettingsExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                GyroSettingsExpandIcon.Glyph = isGyroSettingsExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void StickDeadzonesExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            isStickDeadzonesExpanded = !isStickDeadzonesExpanded;

            if (StickDeadzonesContent != null)
            {
                StickDeadzonesContent.Visibility = isStickDeadzonesExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (StickDeadzonesExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                StickDeadzonesExpandIcon.Glyph = isStickDeadzonesExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void TouchpadVibrationExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            isTouchpadVibrationExpanded = !isTouchpadVibrationExpanded;

            if (TouchpadVibrationContent != null)
            {
                TouchpadVibrationContent.Visibility = isTouchpadVibrationExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (TouchpadVibrationExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                TouchpadVibrationExpandIcon.Glyph = isTouchpadVibrationExpanded ? "\uE70E" : "\uE70D";
            }
        }
        private void LightingExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            isLightingExpanded = !isLightingExpanded;

            if (LightingContent != null)
            {
                LightingContent.Visibility = isLightingExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (LightingExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                LightingExpandIcon.Glyph = isLightingExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void FanCurveExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            isFanCurveExpanded = !isFanCurveExpanded;

            if (FanCurveContent != null)
            {
                FanCurveContent.Visibility = isFanCurveExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (FanCurveExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                FanCurveExpandIcon.Glyph = isFanCurveExpanded ? "\uE70E" : "\uE70D";
            }

            // Initialize graph on first expand
            if (isFanCurveExpanded && !fanCurveGraphInitialized)
            {
                InitializeFanCurveGraph();
            }

            // Tell helper whether to push the fan-control sensor temp (0x01) used by the
            // fan-curve graph's own info panel, and to speed up its refresh cadence. RPM and
            // the header CPU temp stream continuously regardless of this flag (see
            // LegionManager.RefreshFanSpeed), so the header never needs clearing here.
            legionFanCurveVisible?.SetVisible(isFanCurveExpanded);

            if (isFanCurveExpanded)
            {
                UpdateActiveModeLabel();
            }
        }
        private void CPUExtrasExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            isCPUExtrasExpanded = !isCPUExtrasExpanded;

            if (CPUExtrasContent != null)
            {
                CPUExtrasContent.Visibility = isCPUExtrasExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (CPUExtrasExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                CPUExtrasExpandIcon.Glyph = isCPUExtrasExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void ControllerEmulationExpandButton_Click(object sender, RoutedEventArgs e)
        {
            isControllerEmulationExpanded = !isControllerEmulationExpanded;
            LastCardExpandedBeforeHide = isControllerEmulationExpanded;

            // Show whichever backend's body the user has selected (legacy vs VIIPER).
            bool viiperActive = emulationBackend != null && emulationBackend.Value;

            if (ControllerEmulationContent != null)
            {
                ControllerEmulationContent.Visibility = (isControllerEmulationExpanded && !viiperActive)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (ViiperEmulationContent != null)
            {
                ViiperEmulationContent.Visibility = (isControllerEmulationExpanded && viiperActive)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (ControllerEmulationExpandIcon != null)
            {
                ControllerEmulationExpandIcon.Glyph = isControllerEmulationExpanded ? "\uE70E" : "\uE70D";
            }

            UpdateSystemControllerEmulationNavigation();
        }

        private void TDPSettingsExpandButton_Click(object sender, RoutedEventArgs e)
        {
            isTDPSettingsExpanded = !isTDPSettingsExpanded;

            if (TDPSettingsContent != null)
            {
                TDPSettingsContent.Visibility = isTDPSettingsExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (TDPSettingsExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                TDPSettingsExpandIcon.Glyph = isTDPSettingsExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void SpecialRemappingExpandButton_Click(object sender, RoutedEventArgs e)
        {
            isSpecialRemappingExpanded = !isSpecialRemappingExpanded;

            if (SpecialRemappingContent != null)
            {
                SpecialRemappingContent.Visibility = isSpecialRemappingExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SpecialRemappingExpandIcon != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                SpecialRemappingExpandIcon.Glyph = isSpecialRemappingExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void StickSensitivityV2Slider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (StickSensitivityV2ValueText != null)
                StickSensitivityV2ValueText.Text = $"{(e.NewValue / 100.0):0.00}x";
        }

        // Min/Max gyro speed, Min/Max output, Power curve, Deadzone, Precision speed,
        // Output mix slider value-change handlers all removed in #79 round 5
        // along with the underlying sliders.


        private void ColorSettingsExpandButton_Click(object sender, RoutedEventArgs e)
        {
            isColorSettingsExpanded = !isColorSettingsExpanded;

            if (ColorSettingsContent != null)
            {
                ColorSettingsContent.Visibility = isColorSettingsExpanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ColorSettingsExpandButton != null)
            {
                // E70D = ChevronDown, E70E = ChevronUp
                ColorSettingsExpandButton.Content = isColorSettingsExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private void ApplyTDPLimits()
        {
            // Master TDP slider removed — the Custom power-limit sliders use fixed ranges
            // (TDP 5-35W, boosts +0..+10 / +0..+15), so device min/max no longer reshapes a slider.
            // Still inform the helper of the device limits (used for OSD / clamping).
            SendTDPLimitsToHelper();
        }

        private void SendTDPLimitsToHelper()
        {
            try
            {
                string limitsString = $"{deviceTDPMin},{deviceTDPMax}";
                tdpLimits?.SetValue(limitsString);
                Logger.Info($"Sent TDP limits to helper: Min={deviceTDPMin}W, Max={deviceTDPMax}W");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to send TDP limits to helper: {ex.Message}");
            }
        }

        private void SaveTDPLimitsToStorage()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values["DeviceTDPMin"] = deviceTDPMin;
                settings.Values["DeviceTDPMax"] = deviceTDPMax;
                Logger.Info($"Saved TDP limits: Min={deviceTDPMin}W, Max={deviceTDPMax}W");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save TDP limits: {ex.Message}");
            }
        }

        private void LoadTDPLimitsFromStorage()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                if (settings.Values.TryGetValue("DeviceTDPMin", out object minObj) && minObj is int min)
                {
                    deviceTDPMin = min;
                }

                if (settings.Values.TryGetValue("DeviceTDPMax", out object maxObj) && maxObj is int max)
                {
                    deviceTDPMax = max;
                }

                // Apply to TDP slider bounds
                ApplyTDPLimits();

                Logger.Info($"Loaded TDP limits: Min={deviceTDPMin}W, Max={deviceTDPMax}W");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load TDP limits: {ex.Message}");
            }
        }
        private void OSDLayoutOption_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingOSDConfig) return;

            // Get text size (global setting)
            if (OSDTextSizeComboBox?.SelectedItem is ComboBoxItem sizeItem && sizeItem.Tag is string sizeTag)
            {
                if (int.TryParse(sizeTag, out int size))
                {
                    osdTextSize = size;
                }
            }

            // Columns are handled by SaveCurrentOSDConfig
            SaveCurrentOSDConfig();
        }

        private void OSDTextColorDynamic_Changed(object sender, RoutedEventArgs e)
        {
            if (isLoadingOSDConfig) return;

            bool isDynamic = OSDTextColorDynamicCheckBox?.IsChecked == true;
            if (isDynamic)
            {
                osdTextColor = "DYNAMIC";
                UpdateOSDTextColorPreview();
            }
            else
            {
                // Use current color picker color
                if (OSDTextColorPicker != null)
                {
                    var color = OSDTextColorPicker.Color;
                    osdTextColor = $"{color.R:X2}{color.G:X2}{color.B:X2}";
                }
                else
                {
                    osdTextColor = "FFFFFF";
                }
                UpdateOSDTextColorPreview();
            }

            SaveOSDConfigToStorage();
            SendOSDConfigToHelper();
        }

        private void OSDTextColorExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (OSDTextColorPicker != null)
                {
                    bool isExpanded = OSDTextColorPicker.Visibility == Visibility.Visible;
                    OSDTextColorPicker.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;

                    if (OSDTextColorExpandButton != null)
                    {
                        OSDTextColorExpandButton.Content = isExpanded ? "\uE70D" : "\uE70E";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in OSDTextColorExpandButton_Click: {ex.Message}");
            }
        }

        private void OSDTextColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
        {
            if (isLoadingOSDConfig) return;

            try
            {
                // Update preview
                if (OSDTextColorPreview != null)
                {
                    OSDTextColorPreview.Background = new SolidColorBrush(args.NewColor);
                }

                // Only update color if not in Dynamic mode
                if (OSDTextColorDynamicCheckBox?.IsChecked != true)
                {
                    osdTextColor = $"{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
                    SaveOSDConfigToStorage();
                    SendOSDConfigToHelper();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in OSDTextColorPicker_ColorChanged: {ex.Message}");
            }
        }

        private void OSDLabelColorDefault_Changed(object sender, RoutedEventArgs e)
        {
            if (isLoadingOSDConfig) return;

            bool isDefault = OSDLabelColorDefaultCheckBox?.IsChecked == true;
            if (isDefault)
            {
                osdLabelColor = "DEFAULT";
                UpdateOSDLabelColorPreview();
            }
            else
            {
                // Use current color picker color
                if (OSDLabelColorPicker != null)
                {
                    var color = OSDLabelColorPicker.Color;
                    osdLabelColor = $"{color.R:X2}{color.G:X2}{color.B:X2}";
                }
                else
                {
                    osdLabelColor = "00FFFF";  // Cyan default
                }
                UpdateOSDLabelColorPreview();
            }

            SaveOSDConfigToStorage();
            SendOSDConfigToHelper();
        }

        private void OSDLabelColorExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (OSDLabelColorPicker != null)
                {
                    bool isExpanded = OSDLabelColorPicker.Visibility == Visibility.Visible;
                    OSDLabelColorPicker.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;

                    if (OSDLabelColorExpandButton != null)
                    {
                        OSDLabelColorExpandButton.Content = isExpanded ? "\uE70D" : "\uE70E";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in OSDLabelColorExpandButton_Click: {ex.Message}");
            }
        }

        private void OSDLabelColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
        {
            if (isLoadingOSDConfig) return;

            try
            {
                // Update preview
                if (OSDLabelColorPreview != null)
                {
                    OSDLabelColorPreview.Background = new SolidColorBrush(args.NewColor);
                }

                // Only update color if not in Default mode
                if (OSDLabelColorDefaultCheckBox?.IsChecked != true)
                {
                    osdLabelColor = $"{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
                    SaveOSDConfigToStorage();
                    SendOSDConfigToHelper();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in OSDLabelColorPicker_ColorChanged: {ex.Message}");
            }
        }

        private void UpdateOSDTextColorPreview()
        {
            if (OSDTextColorPreview == null) return;

            try
            {
                if (osdTextColor == "DYNAMIC")
                {
                    // Show gradient for dynamic color preview (blue to green to yellow to red)
                    var gradient = new LinearGradientBrush();
                    gradient.StartPoint = new Windows.Foundation.Point(0, 0);
                    gradient.EndPoint = new Windows.Foundation.Point(1, 0);
                    gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0, 128, 255), Offset = 0 });    // Blue (cold)
                    gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0, 255, 0), Offset = 0.33 });   // Green (good)
                    gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 255, 255, 0), Offset = 0.66 }); // Yellow (warm)
                    gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 255, 0, 0), Offset = 1 });      // Red (hot)
                    OSDTextColorPreview.Background = gradient;
                }
                else if (osdTextColor.Length == 6)
                {
                    var color = Windows.UI.Color.FromArgb(255,
                        Convert.ToByte(osdTextColor.Substring(0, 2), 16),
                        Convert.ToByte(osdTextColor.Substring(2, 2), 16),
                        Convert.ToByte(osdTextColor.Substring(4, 2), 16));
                    OSDTextColorPreview.Background = new SolidColorBrush(color);
                }
            }
            catch { }
        }

        private void UpdateOSDLabelColorPreview()
        {
            if (OSDLabelColorPreview == null) return;

            try
            {
                if (osdLabelColor == "DEFAULT")
                {
                    // Show gradient to indicate default (each item has its own color)
                    var gradient = new LinearGradientBrush();
                    gradient.StartPoint = new Windows.Foundation.Point(0, 0);
                    gradient.EndPoint = new Windows.Foundation.Point(1, 0);
                    gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0, 255, 255), Offset = 0 });    // Cyan
                    gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 255, 165, 0), Offset = 0.5 });  // Orange
                    gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0, 255, 0), Offset = 1 });      // Green
                    OSDLabelColorPreview.Background = gradient;
                }
                else if (osdLabelColor.Length == 6)
                {
                    var color = Windows.UI.Color.FromArgb(255,
                        Convert.ToByte(osdLabelColor.Substring(0, 2), 16),
                        Convert.ToByte(osdLabelColor.Substring(2, 2), 16),
                        Convert.ToByte(osdLabelColor.Substring(4, 2), 16));
                    OSDLabelColorPreview.Background = new SolidColorBrush(color);
                }
            }
            catch { }
        }

        private void UpdateOSDLayoutUI()
        {
            isLoadingOSDConfig = true;
            try
            {
                // Set OSD provider combobox
                if (OSDProviderComboBox != null)
                {
                    foreach (ComboBoxItem item in OSDProviderComboBox.Items)
                    {
                        if (item.Tag is string tag && int.TryParse(tag, out int val) && val == osdProvider)
                        {
                            OSDProviderComboBox.SelectedItem = item;
                            break;
                        }
                    }
                }

                // Update provider-specific UI visibility
                UpdateOSDProviderUI();

                // Columns are loaded in LoadOSDOptions

                // Set text size combobox
                if (OSDTextSizeComboBox != null)
                {
                    foreach (ComboBoxItem item in OSDTextSizeComboBox.Items)
                    {
                        if (item.Tag is string tag && int.TryParse(tag, out int val) && val == osdTextSize)
                        {
                            OSDTextSizeComboBox.SelectedItem = item;
                            break;
                        }
                    }
                }

                // Set text color checkbox and color picker
                if (OSDTextColorDynamicCheckBox != null)
                {
                    OSDTextColorDynamicCheckBox.IsChecked = (osdTextColor == "DYNAMIC");
                }
                if (OSDTextColorPicker != null && osdTextColor != "DYNAMIC" && osdTextColor.Length == 6)
                {
                    try
                    {
                        var color = Windows.UI.Color.FromArgb(255,
                            Convert.ToByte(osdTextColor.Substring(0, 2), 16),
                            Convert.ToByte(osdTextColor.Substring(2, 2), 16),
                            Convert.ToByte(osdTextColor.Substring(4, 2), 16));
                        OSDTextColorPicker.Color = color;
                    }
                    catch { }
                }
                UpdateOSDTextColorPreview();

                // Set label color checkbox and color picker
                if (OSDLabelColorDefaultCheckBox != null)
                {
                    OSDLabelColorDefaultCheckBox.IsChecked = (osdLabelColor == "DEFAULT");
                }
                if (OSDLabelColorPicker != null && osdLabelColor != "DEFAULT" && osdLabelColor.Length == 6)
                {
                    try
                    {
                        var color = Windows.UI.Color.FromArgb(255,
                            Convert.ToByte(osdLabelColor.Substring(0, 2), 16),
                            Convert.ToByte(osdLabelColor.Substring(2, 2), 16),
                            Convert.ToByte(osdLabelColor.Substring(4, 2), 16));
                        OSDLabelColorPicker.Color = color;
                    }
                    catch { }
                }
                UpdateOSDLabelColorPreview();

            }
            finally
            {
                isLoadingOSDConfig = false;
            }
        }

    }
}
