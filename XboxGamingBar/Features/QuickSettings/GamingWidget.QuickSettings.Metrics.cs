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
        /// Ask the helper to re-read the built-in panel brightness + availability from WMI and push
        /// them back, so the slider always reflects the true current value (in case brightness was
        /// changed from another source) and grays out when the panel is off. Fired on each panel open.
        /// Fire-and-forget: the helper replies via the normal property push (PanelBrightness /
        /// PanelBrightnessSupported), which updates the slider value + enabled state.
        /// </summary>
        private async void RefreshPanelBrightness()
        {
            try
            {
                if (!App.IsConnected) return;
                var request = new Windows.Foundation.Collections.ValueSet();
                request.Add("RefreshPanelBrightness", true);
                await App.SendMessageAsync(request);
            }
            catch (Exception ex)
            {
                Logger.Error($"RefreshPanelBrightness request failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle Brightness Slider visibility toggle (Customize panel). Widget-only: the
        /// PanelBrightness property is always synced, this just shows/hides the slider row.
        /// </summary>
        private void PanelBrightnessToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                brightnessSliderEnabled = PanelBrightnessToggle.IsOn;

                var settings = ApplicationData.Current.LocalSettings;
                settings.Values[BrightnessSliderEnabledKey] = brightnessSliderEnabled;

                if (PanelBrightnessRow != null)
                    PanelBrightnessRow.Visibility = brightnessSliderEnabled ? Visibility.Visible : Visibility.Collapsed;

                Logger.Info($"Brightness slider toggle set to: {brightnessSliderEnabled}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling Brightness slider toggle: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle Quick Metrics toggle change
        /// </summary>
        private async void QuickMetricsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                quickMetricsEnabled = QuickMetricsToggle.IsOn;

                // Save setting to local storage
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values[QuickMetricsEnabledKey] = quickMetricsEnabled;

                // Update visibility of metrics row and selection panel
                if (QuickMetricsRow != null)
                    QuickMetricsRow.Visibility = quickMetricsEnabled ? Visibility.Visible : Visibility.Collapsed;
                if (MetricsSelectionPanel != null)
                    MetricsSelectionPanel.Visibility = quickMetricsEnabled ? Visibility.Visible : Visibility.Collapsed;

                // Rebuild the metrics grid if enabling
                if (quickMetricsEnabled)
                {
                    RebuildMetricsGrid();
                }

                // Notify helper to start/stop pushing metrics
                if (App.IsConnected)
                {
                    var request = new Windows.Foundation.Collections.ValueSet
                    {
                        { "Command", (int)Shared.Enums.Command.Set },
                        { "Function", (int)Shared.Enums.Function.QuickMetricsEnabled },
                        { "Content", quickMetricsEnabled }
                    };
                    await App.SendMessageAsync(request);
                    Logger.Info($"Quick Metrics toggle set to: {quickMetricsEnabled}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling Quick Metrics toggle: {ex.Message}");
            }
        }

        /// <summary>
        /// Update Quick Metrics display from helper push data
        /// </summary>
        private void UpdateQuickMetrics(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return;

                // Skip if Game Bar has replaced this widget instance. Otherwise the
                // dispatched UpdateMetricsDisplay below will hit detached XAML elements
                // and throw "COM object that has been separated from its underlying RCW".
                if (App.GetActiveGamingWidget() != this) return;

                // Parse all metrics from JSON
                var matches = System.Text.RegularExpressions.Regex.Matches(json,
                    @"""(\w+)""\s*:\s*(-?\d+\.?\d*|true|false)");

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var key = match.Groups[1].Value;
                    var value = match.Groups[2].Value;

                    if (key == "isCharging")
                    {
                        currentMetricsIsCharging = value == "true";
                    }
                    else if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double numValue))
                    {
                        currentMetricsData[key] = numValue;
                    }
                }

                // Update UI elements on dispatcher thread
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        UpdateMetricsDisplay();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error updating Quick Metrics UI: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Error parsing Quick Metrics JSON: {ex.Message}");
            }
        }

        /// <summary>
        /// Update the metrics display based on current data and selected metrics
        /// </summary>
        private void UpdateMetricsDisplay()
        {
            foreach (var metricType in selectedMetrics)
            {
                if (!metricDefinitions.TryGetValue(metricType, out var info) || info.ValueTextBlock == null)
                    continue;

                string displayValue = "--";
                string label = info.Label;

                switch (metricType)
                {
                    case MetricType.BatteryDrain:
                        if (currentMetricsData.TryGetValue("batteryDrain", out var drain))
                        {
                            if (drain > 0)
                                displayValue = $"{drain:F1}W";
                            else if (drain < 0)
                                displayValue = $"+{-drain:F1}W";
                            else
                                displayValue = "--W";
                        }
                        break;

                    case MetricType.BatteryLevel:
                        if (currentMetricsData.TryGetValue("batteryLevel", out var level) && level >= 0)
                            displayValue = $"{level:F0}%";
                        break;

                    case MetricType.CPUUsage:
                        if (currentMetricsData.TryGetValue("cpuUsage", out var cpuUse) && cpuUse >= 0)
                            displayValue = $"{cpuUse:F0}%";
                        break;

                    case MetricType.GPUUsage:
                        if (currentMetricsData.TryGetValue("gpuUsage", out var gpuUse) && gpuUse >= 0)
                            displayValue = $"{gpuUse:F0}%";
                        break;

                    case MetricType.SoCTemp:
                        if (currentMetricsData.TryGetValue("socTemp", out var socTemp) && socTemp > 0)
                            displayValue = $"{socTemp:F0}°";
                        break;

                    case MetricType.TDP:
                        // As on the overlay: the firmware mode name in Quiet / Balanced /
                        // Performance, the SPL in Custom (255) or on non-Legion devices.
                        currentMetricsData.TryGetValue("tdpMode", out var tdpMode);
                        if (tdpMode == 1) displayValue = "Quiet";
                        else if (tdpMode == 2) displayValue = "Balanced";
                        else if (tdpMode == 3) displayValue = "Perf";
                        else if (currentMetricsData.TryGetValue("tdp", out var tdp) && tdp > 0)
                            displayValue = $"{tdp:F0}W";
                        break;

                    case MetricType.MemoryUsage:
                        if (currentMetricsData.TryGetValue("memoryUsage", out var memUse) && memUse >= 0)
                            displayValue = $"{memUse:F0}%";
                        break;

                    case MetricType.TimeRemaining:
                        currentMetricsData.TryGetValue("timeRemaining", out var timeRem);
                        currentMetricsData.TryGetValue("timeToFull", out var timeFull);
                        if (currentMetricsIsCharging && timeFull > 0)
                        {
                            var hours = (int)(timeFull / 3600);
                            var mins = (int)((timeFull % 3600) / 60);
                            displayValue = $"{hours}:{mins:D2}";
                            label = "To Full";
                        }
                        else if (!currentMetricsIsCharging && timeRem > 0)
                        {
                            var hours = (int)(timeRem / 3600);
                            var mins = (int)((timeRem % 3600) / 60);
                            displayValue = $"{hours}:{mins:D2}";
                            label = "Remaining";
                        }
                        else
                        {
                            displayValue = "--:--";
                            label = currentMetricsIsCharging ? "Charging" : "Time";
                        }
                        break;
                }

                info.ValueTextBlock.Text = displayValue;
                if (info.LabelTextBlock != null)
                    info.LabelTextBlock.Text = label;
            }
        }

        /// <summary>
        /// Update checkbox states based on selected metrics
        /// </summary>
        private void UpdateMetricCheckboxes()
        {
            // Guard to prevent MetricCheckBox_Changed from firing during programmatic updates.
            // Without this, setting IsChecked=true on checkboxes fires the handler, which sees
            // selectedMetrics.Count >= MaxSelectedMetrics and reverts the checkbox to false,
            // triggering Unchecked which removes the metric from the list.
            isUpdatingMetricCheckboxes = true;
            try
            {
                // Map checkboxes to metric types
                var checkboxMap = new Dictionary<CheckBox, MetricType>
                {
                    { MetricCheck_BatteryDrain, MetricType.BatteryDrain },
                    { MetricCheck_BatteryLevel, MetricType.BatteryLevel },
                    { MetricCheck_CPUUsage, MetricType.CPUUsage },
                    { MetricCheck_GPUUsage, MetricType.GPUUsage },
                    { MetricCheck_SoCTemp, MetricType.SoCTemp },
                    { MetricCheck_TDP, MetricType.TDP },
                    { MetricCheck_MemoryUsage, MetricType.MemoryUsage },
                    { MetricCheck_TimeRemaining, MetricType.TimeRemaining }
                };

                foreach (var kvp in checkboxMap)
                {
                    if (kvp.Key != null)
                        kvp.Key.IsChecked = selectedMetrics.Contains(kvp.Value);
                }

                UpdateMetricsSelectionCount();
            }
            finally
            {
                isUpdatingMetricCheckboxes = false;
            }
        }

        /// <summary>
        /// Update the metrics selection count display
        /// </summary>
        private void UpdateMetricsSelectionCount()
        {
            if (MetricsSelectionCount != null)
            {
                MetricsSelectionCount.Text = $"{selectedMetrics.Count}/{MaxSelectedMetrics} selected";
                MetricsSelectionCount.Foreground = new SolidColorBrush(
                    selectedMetrics.Count >= MaxSelectedMetrics
                        ? Windows.UI.Color.FromArgb(255, 255, 150, 100)  // Orange when at max
                        : Windows.UI.Color.FromArgb(255, 102, 102, 102)); // Gray otherwise
            }
        }

        /// <summary>
        /// Handle metric checkbox changes
        /// </summary>
        private void MetricCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (isUpdatingMetricCheckboxes) return;
            if (!(sender is CheckBox checkbox)) return;

            // Map checkbox to metric type
            var checkboxMap = new Dictionary<CheckBox, MetricType>
            {
                { MetricCheck_BatteryDrain, MetricType.BatteryDrain },
                { MetricCheck_BatteryLevel, MetricType.BatteryLevel },
                { MetricCheck_CPUUsage, MetricType.CPUUsage },
                { MetricCheck_GPUUsage, MetricType.GPUUsage },
                { MetricCheck_SoCTemp, MetricType.SoCTemp },
                { MetricCheck_TDP, MetricType.TDP },
                { MetricCheck_MemoryUsage, MetricType.MemoryUsage },
                { MetricCheck_TimeRemaining, MetricType.TimeRemaining }
            };

            if (!checkboxMap.TryGetValue(checkbox, out var metricType))
                return;

            bool isChecked = checkbox.IsChecked == true;

            if (isChecked)
            {
                // Trying to add - check if at max
                if (selectedMetrics.Count >= MaxSelectedMetrics)
                {
                    checkbox.IsChecked = false;
                    return;
                }
                if (!selectedMetrics.Contains(metricType))
                    selectedMetrics.Add(metricType);
            }
            else
            {
                selectedMetrics.Remove(metricType);
            }

            // Save selection
            SaveMetricsSelection();

            // Update count display
            UpdateMetricsSelectionCount();

            // Rebuild the metrics grid
            RebuildMetricsGridPreserveScroll(null, preferUp: true);
        }

        /// <summary>
        /// Save metrics selection to local settings
        /// </summary>
        private void SaveMetricsSelection()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                var selectionStr = string.Join(",", selectedMetrics.Select(m => m.ToString()));
                settings.Values[QuickMetricsSelectionKey] = selectionStr;
                Logger.Info($"Saved metrics selection: {selectionStr}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving metrics selection: {ex.Message}");
            }
        }

        /// <summary>
        /// RebuildMetricsGrid for changes made in the Customize panel, keeping the scroll
        /// position (same approach as BuildSortableGridPreserveScroll). Rebuilding the reorder
        /// list removes the focused up/down button; focus then falls back to the top of the page
        /// and the view jumps there. When <paramref name="focusMetric"/> is set, focus goes to
        /// that metric's up (or down) button in its new row, or the other one if it is disabled.
        /// </summary>
        private void RebuildMetricsGridPreserveScroll(MetricType? focusMetric, bool preferUp)
        {
            var scrollViewer = QuickSettingsScrollViewer;
            double scrollOffset = scrollViewer?.VerticalOffset ?? 0;

            if (scrollViewer != null) scrollViewer.BringIntoViewOnFocusChange = false;
            try
            {
                RebuildMetricsGrid();
                scrollViewer?.UpdateLayout();
                if (focusMetric.HasValue)
                {
                    FindMetricMoveButton(focusMetric.Value, preferUp)?.Focus(FocusState.Programmatic);
                }
                scrollViewer?.ChangeView(null, scrollOffset, null, true);
            }
            finally
            {
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                {
                    if (scrollViewer == null) return;
                    if (Math.Abs(scrollViewer.VerticalOffset - scrollOffset) > 1)
                    {
                        scrollViewer.ChangeView(null, scrollOffset, null, true);
                    }
                    scrollViewer.BringIntoViewOnFocusChange = true;
                });
            }
        }

        /// <summary>The enabled up (column 2) or down (column 3) button of a metric's reorder row.</summary>
        private Button FindMetricMoveButton(MetricType metric, bool preferUp)
        {
            if (MetricsReorderList == null) return null;
            foreach (var child in MetricsReorderList.Children)
            {
                if (!(child is Grid row)) continue;
                Button up = null, down = null;
                foreach (var cell in row.Children)
                {
                    if (cell is Button b && b.Tag is MetricType m && m == metric)
                    {
                        if (Grid.GetColumn(b) == 2) up = b; else down = b;
                    }
                }
                if (up == null && down == null) continue;
                var first = preferUp ? up : down;
                var second = preferUp ? down : up;
                if (first != null && first.IsEnabled) return first;
                if (second != null && second.IsEnabled) return second;
                return first ?? second;
            }
            return null;
        }

        /// <summary>
        /// Rebuild the metrics grid based on selected metrics
        /// </summary>
        private void RebuildMetricsGrid()
        {
            if (QuickMetricsGrid == null) return;

            QuickMetricsGrid.Children.Clear();
            QuickMetricsGrid.ColumnDefinitions.Clear();

            if (selectedMetrics.Count == 0)
            {
                QuickMetricsRow.Visibility = Visibility.Collapsed;
                return;
            }

            // Create columns: one star column per metric, plus a thin Auto divider
            // column between each pair of metrics.
            for (int c = 0; c < selectedMetrics.Count; c++)
            {
                if (c > 0) QuickMetricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                QuickMetricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            // Icon color: the user's live Windows accent color (unlike the Quick tab
            // tile subtitle/state text, which stay pinned to the default Windows
            // blue - this one deliberately follows the user's actual accent).
            var metricIconBrush = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColorLight2"]);

            // Create UI for each selected metric: icon centered on top, value
            // centered below the icon, gray label centered below the value.
            int gridCol = 0;
            for (int i = 0; i < selectedMetrics.Count; i++)
            {
                var metricType = selectedMetrics[i];
                if (!metricDefinitions.TryGetValue(metricType, out var info))
                    continue;

                if (i > 0)
                {
                    // Thin gray divider between metrics.
                    var divider = new Border
                    {
                        Width = 1,
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    Grid.SetColumn(divider, gridCol);
                    QuickMetricsGrid.Children.Add(divider);
                    gridCol++;
                }

                // Create metric panel
                var panel = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var icon = new FontIcon
                {
                    Glyph = info.Glyph,
                    // Matches the Quick Settings tile icon font size (see
                    // CreateTileButton in GamingWidget.QuickSettings.TileStates.cs).
                    FontSize = 21,
                    Foreground = metricIconBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 3)
                };

                var valueText = new TextBlock
                {
                    Text = "--",
                    // Matches the Quick Settings tile state/subtitle font size (see
                    // CreateTileButton in GamingWidget.QuickSettings.TileStates.cs).
                    FontSize = 12,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                info.ValueTextBlock = valueText;

                // Label
                var labelText = new TextBlock
                {
                    Text = info.Label,
                    // Matches the Quick Settings tile name/title font size (see
                    // CreateTileButton in GamingWidget.QuickSettings.TileStates.cs).
                    FontSize = qsColumnCount == 4 ? 13 : 14,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136)), // #888888
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                info.LabelTextBlock = labelText;

                panel.Children.Add(icon);
                panel.Children.Add(valueText);
                panel.Children.Add(labelText);

                Grid.SetColumn(panel, gridCol);
                QuickMetricsGrid.Children.Add(panel);

                gridCol++;
            }

            // Show the row if we have metrics
            if (quickMetricsEnabled && selectedMetrics.Count > 0)
            {
                QuickMetricsRow.Visibility = Visibility.Visible;
            }

            // The TextBlocks just created above are seeded with "--" and only get repainted
            // by the next helper push (UpdateQuickMetrics). If a push isn't imminent (e.g. we
            // just came back from an instance recreation, or the user is mid-reorder), the row
            // would sit on "--" for a while. Repaint immediately from already-cached data so
            // there's no visible gap when we have it.
            UpdateMetricsDisplay();

            // Also rebuild the reorder list
            RebuildMetricsReorderList();
        }

        /// <summary>
        /// Rebuild the metrics reorder list UI
        /// </summary>
        private void RebuildMetricsReorderList()
        {
            if (MetricsReorderList == null || MetricsReorderSection == null) return;

            MetricsReorderList.Children.Clear();

            // Hide reorder section if no metrics selected
            if (selectedMetrics.Count == 0)
            {
                MetricsReorderSection.Visibility = Visibility.Collapsed;
                return;
            }

            MetricsReorderSection.Visibility = Visibility.Visible;

            // Create a row for each selected metric
            for (int i = 0; i < selectedMetrics.Count; i++)
            {
                var metricType = selectedMetrics[i];
                if (!metricDefinitions.TryGetValue(metricType, out var info))
                    continue;

                var row = new Grid
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 4, 4),
                    Margin = new Thickness(0, 0, 0, 0)
                };

                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // Index
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Name
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); // Up button
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); // Down button

                // Index number
                var indexText = new TextBlock
                {
                    Text = $"{i + 1}",
                    Foreground = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColorLight2"]),
                    FontSize = 11,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(indexText, 0);
                row.Children.Add(indexText);

                // Metric name
                var nameText = new TextBlock
                {
                    Text = info.Label,
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameText, 1);
                row.Children.Add(nameText);

                // Up button
                var upButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70E", FontSize = 10 }, // ChevronUp
                    Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                    Padding = new Thickness(4),
                    MinWidth = 24,
                    MinHeight = 24,
                    IsEnabled = i > 0,
                    Opacity = i > 0 ? 1.0 : 0.3,
                    Tag = metricType
                };
                upButton.Click += MetricMoveUp_Click;
                Grid.SetColumn(upButton, 2);
                row.Children.Add(upButton);

                // Down button
                var downButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70D", FontSize = 10 }, // ChevronDown
                    Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                    Padding = new Thickness(4),
                    MinWidth = 24,
                    MinHeight = 24,
                    IsEnabled = i < selectedMetrics.Count - 1,
                    Opacity = i < selectedMetrics.Count - 1 ? 1.0 : 0.3,
                    Tag = metricType
                };
                downButton.Click += MetricMoveDown_Click;
                Grid.SetColumn(downButton, 3);
                row.Children.Add(downButton);

                MetricsReorderList.Children.Add(row);
            }
        }

        /// <summary>
        /// Handle move up button click for metric reordering
        /// </summary>
        private void MetricMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is MetricType metricType))
                return;

            int index = selectedMetrics.IndexOf(metricType);
            if (index <= 0) return;

            // Swap with previous item
            selectedMetrics.RemoveAt(index);
            selectedMetrics.Insert(index - 1, metricType);

            // Save and rebuild
            SaveMetricsSelection();
            RebuildMetricsGridPreserveScroll(metricType, preferUp: true);
        }

        /// <summary>
        /// Handle move down button click for metric reordering
        /// </summary>
        private void MetricMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is MetricType metricType))
                return;

            int index = selectedMetrics.IndexOf(metricType);
            if (index < 0 || index >= selectedMetrics.Count - 1) return;

            // Swap with next item
            selectedMetrics.RemoveAt(index);
            selectedMetrics.Insert(index + 1, metricType);

            // Save and rebuild
            SaveMetricsSelection();
            RebuildMetricsGridPreserveScroll(metricType, preferUp: false);
        }

    }
}
