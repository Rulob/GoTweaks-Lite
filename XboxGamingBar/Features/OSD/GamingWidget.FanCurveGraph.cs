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
        // Local cache of every saved per-mode fan curve. Helper pushes all 4 modes on
        // connect via LegionFanCurvePerMode. Only the Custom-mode curve (255) is editable
        // and applied through Lenovo WMI, so the graph always shows that slot, decoupled
        // from the actual running power mode.
        private readonly Dictionary<int, int[]> fanCurveCache = new Dictionary<int, int[]>();
        private const int CustomFanCurveMode = 255;
        private int selectedFanCurveMode = CustomFanCurveMode;
        private bool isApplyingFanCurveCacheLoad = false; // suppress UI→helper echo while loading the selected slot

        private void InitializeFanCurveGraph()
        {
            if (FanCurveCanvas == null || fanCurveGraphInitialized)
                return;

            // Initialize with current values from property
            currentFanCurveValues = legionFanCurveGraph.GetCurveValues();

            // Create 10 control point ellipses
            for (int i = 0; i < 10; i++)
            {
                var ellipse = new Windows.UI.Xaml.Shapes.Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.ColorHelper.FromArgb(255, 0, 170, 255)),
                    Stroke = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                    StrokeThickness = 2,
                    Tag = i
                };
                fanCurvePoints[i] = ellipse;
                FanCurveCanvas.Children.Add(ellipse);
            }

            fanCurveGraphInitialized = true;

            // Load saved preset selection
            LoadFanCurvePresetSetting();

            // Draw the graph
            DrawGridLines();
            UpdateFanCurveGraph();

            // Sync the prefix label + protection-floor legend so the first render matches
            // reality (avoids flicker).
            RefreshFanCurveGraphForUnlockState();
            UpdateActiveModeLabel();
        }

        // Stub kept so per-edit code paths still compile. Manual curve edits always go to
        // the Custom-mode slot.
        private void SwitchToCustomPreset() { }

        // Called when the running power mode changes (Lenovo button, TDP card, helper push).
        // The graph always shows the Custom curve, so only the label and the live
        // indicators (which only make sense for the running curve) need refreshing.
        private void SyncFanCurvePresetComboToActiveMode()
        {
            UpdateActiveModeLabel();
            RefreshFanCurveGraphForUnlockState();
        }

        // Spell out that the graph edits the Custom-mode curve, and whether the console is
        // actually running Custom (the only mode where the edited curve takes effect).
        private void UpdateActiveModeLabel()
        {
            if (FanCurveActiveModeLabel == null) return;
            int active = legionPerformanceMode?.Value ?? selectedFanCurveMode;

            if (active == selectedFanCurveMode)
            {
                FanCurveActiveModeLabel.Text = "Custom mode curve (running)";
                FanCurveActiveModeLabel.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88));
            }
            else
            {
                FanCurveActiveModeLabel.Text = $"Custom mode curve — applies when TDP Mode is Custom (running {LegionModeShortName(active)})";
                FanCurveActiveModeLabel.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0xB0, 0xE0));
            }
            FanCurveActiveModeLabel.Visibility = Visibility.Visible;
        }

        private static string LegionModeShortName(int mode)
        {
            switch (mode)
            {
                case 1: return "Quiet";
                case 2: return "Balanced";
                case 3: return "Performance";
                case 255: return "Custom";
                default: return mode.ToString();
            }
        }

        // Loads the cached Custom-mode curve into the graph, suppressing the UI→helper
        // echo so it never triggers a phantom save. If the cache isn't populated yet
        // (helper hasn't pushed), fall back to the legacy active-mode value when Custom is
        // running so the graph isn't blank.
        private void ApplySelectedFanCurveModeFromCache()
        {
            if (!fanCurveGraphInitialized) return;
            isApplyingFanCurveCacheLoad = true;
            try
            {
                if (fanCurveCache.TryGetValue(selectedFanCurveMode, out int[] cached) && cached != null && cached.Length == 10)
                {
                    currentFanCurveValues = (int[])cached.Clone();
                }
                else if (legionFanCurveGraph != null && IsViewingActiveFanCurveMode())
                {
                    currentFanCurveValues = legionFanCurveGraph.GetCurveValues();
                }
                UpdateFanCurveGraph();
            }
            finally
            {
                isApplyingFanCurveCacheLoad = false;
            }
        }

        // Inbound from helper: per-mode fan curve push. Update the cache; if the user
        // is currently viewing this mode, repaint the graph with the new values.
        private void OnFanCurvePerModeReceived(int mode, int[] values)
        {
            if (values == null || values.Length != 10) return;
            fanCurveCache[mode] = (int[])values.Clone();
            if (mode == selectedFanCurveMode && fanCurveGraphInitialized)
            {
                isApplyingFanCurveCacheLoad = true;
                try
                {
                    currentFanCurveValues = (int[])values.Clone();
                    UpdateFanCurveGraph();
                }
                finally { isApplyingFanCurveCacheLoad = false; }
            }
        }

        private bool IsViewingActiveFanCurveMode()
            => legionPerformanceMode != null && legionPerformanceMode.Value == selectedFanCurveMode;

        // Legacy preset-name persistence is obsolete now (we don't store a "preset name"
        // separately from power mode). Kept as a no-op so older code paths don't crash.
        private void SaveFanCurvePresetSetting(string presetName) { }

        // Legacy: persisted preset name was used by the old preset dropdown
        // (Silent/Balanced/Performance/MaxCooling/Custom). The dropdown is now keyed
        // off TdpMode and the helper is the source of truth — SyncFanCurvePresetComboToActiveMode
        // drives the selection from legionPerformanceMode.Value. Kept as a no-op so we
        // don't read the now-meaningless LocalSettings value.
        private void LoadFanCurvePresetSetting() { }

        private void DrawGridLines()
        {
            if (FanCurveCanvas == null) return;

            double width = FanCurveCanvas.ActualWidth;
            double height = FanCurveCanvas.ActualHeight;

            if (width <= 0 || height <= 0) return;

            // Draw horizontal grid lines (at 25%, 50%, 75%)
            for (int i = 1; i <= 3; i++)
            {
                double y = height - (height * i * 0.25);
                var line = new Windows.UI.Xaml.Shapes.Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = width,
                    Y2 = y,
                    Stroke = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.ColorHelper.FromArgb(50, 255, 255, 255)),
                    StrokeThickness = 1
                };
                Canvas.SetZIndex(line, -1);
                FanCurveCanvas.Children.Add(line);
            }

            // Draw vertical grid lines (at 20%, 40%, 60%, 80%)
            for (int i = 1; i <= 4; i++)
            {
                double x = width * i * 0.2;
                var line = new Windows.UI.Xaml.Shapes.Line
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = height,
                    Stroke = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.ColorHelper.FromArgb(50, 255, 255, 255)),
                    StrokeThickness = 1
                };
                Canvas.SetZIndex(line, -1);
                FanCurveCanvas.Children.Add(line);
            }

            // Draw EC floor line after grid lines
            DrawECFloorLine();
        }

        private void DrawECFloorLine()
        {
            if (ECFloorPolyline == null || FanCurveCanvas == null) return;

            double width = FanCurveCanvas.ActualWidth;
            double height = FanCurveCanvas.ActualHeight;

            if (width <= 0 || height <= 0) return;

            var points = new Windows.UI.Xaml.Media.PointCollection();

            foreach (var (temp, floor) in ECFloorPoints)
            {
                // Map temperature to X position (10-100°C range)
                double x = (temp - 10.0) / 90.0 * width;
                // Map fan % to Y position (inverted)
                double y = height - (floor / 100.0 * height);
                points.Add(new Windows.Foundation.Point(x, y));
            }

            ECFloorPolyline.Points = points;
        }

        private void UpdateFanCurveGraph()
        {
            if (FanCurveCanvas == null || FanCurvePolyline == null || FanCurveFill == null)
                return;

            double width = FanCurveCanvas.ActualWidth;
            double height = FanCurveCanvas.ActualHeight;

            if (width <= 0 || height <= 0) return;

            var points = new Windows.UI.Xaml.Media.PointCollection();
            var fillPoints = new Windows.UI.Xaml.Media.PointCollection();

            // Legion Go temperature thresholds: 10, 20, 30, 40, 50, 60, 70, 80, 90, 100°C (FIXED by EC)
            // Map to 0-100% of width (10-100°C range = 90°C)
            for (int i = 0; i < 10; i++)
            {
                int temp = FanCurveTemperatures[i];
                double x = (temp - 10.0) / 90.0 * width; // Normalize 10-100 to 0-width
                double y = height - (currentFanCurveValues[i] / 100.0 * height);

                points.Add(new Windows.Foundation.Point(x, y));
                fillPoints.Add(new Windows.Foundation.Point(x, y));

                // Position control point
                if (fanCurvePoints[i] != null)
                {
                    Canvas.SetLeft(fanCurvePoints[i], x - 8); // Center the 16px ellipse
                    Canvas.SetTop(fanCurvePoints[i], y - 8);
                }
            }

            FanCurvePolyline.Points = points;

            // Add bottom corners for fill polygon
            fillPoints.Add(new Windows.Foundation.Point(width, height));
            fillPoints.Add(new Windows.Foundation.Point(0, height));
            FanCurveFill.Points = fillPoints;
        }

        private void UpdateTemperatureIndicator(int tempC)
        {
            if (TempIndicatorLine == null || FanCurveCanvas == null)
                return;

            double width = FanCurveCanvas.ActualWidth;
            double height = FanCurveCanvas.ActualHeight;

            if (width <= 0 || height <= 0) return;

            // Clamp temp to 10-100 range (Legion Go fan curve range, FIXED by EC)
            tempC = Math.Max(10, Math.Min(100, tempC));

            // Calculate X position (10-100°C range = 90°C span)
            double x = (tempC - 10.0) / 90.0 * width;

            TempIndicatorLine.X1 = x;
            TempIndicatorLine.X2 = x;
            TempIndicatorLine.Y1 = 0;
            TempIndicatorLine.Y2 = height;
            TempIndicatorLine.Visibility = Visibility.Visible;
        }

        private void OnFanCurveUpdated(int[] values)
        {
            if (values == null || values.Length != 10) return;

            // Cache is owned by the per-mode channel (LegionFanCurvePerMode), where the
            // mode is explicit in the payload — race-free. The legacy LegionFanCurveData
            // channel doesn't carry a mode tag, so reading legionPerformanceMode.Value
            // here is racy at startup / mode change. Don't write to the cache from this
            // path; just repaint the graph if the user is viewing the (presumed) active
            // mode and the per-mode push hasn't yet landed.
            if (legionPerformanceMode != null && legionPerformanceMode.Value == selectedFanCurveMode)
            {
                currentFanCurveValues = values;
                UpdateFanCurveGraph();
            }
        }

        // The whole fan-curve UI shows the true CPU temperature (Tctl/Tdie), which is what
        // users expect — NOT the EC's internal fan-control sensor (0x01), which reads a
        // chipset/board area and was being mistaken for the CPU temp.
        private void OnCPUTempUpdated(int tempC)
        {
            // Header readout is live hardware — always show it while the card is open,
            // independent of which mode's curve is being viewed below.
            UpdateHeaderTemp(tempC);
            // The on-graph temp dot maps onto the displayed curve, so only draw it when the
            // viewed curve is the one actually running.
            if (IsViewingActiveFanCurveMode()) UpdateFanCurveGraphTemp(tempC);
            else HideLiveIndicators();
        }

        // The EC fan-control sensor (0x01, chipset/board) is intentionally no longer shown —
        // it was being mistaken for the CPU temperature. The firmware still uses it internally
        // in locked mode (the Info expander notes the firmware may map temps differently).
        private void OnFanSensorTempUpdated(int tempC)
        {
            // not displayed — CPU temp drives the readout everywhere now.
        }

        // Live header temperature readout (next to the RPM). Always reflects the sensor that's
        // currently driving the fan, independent of which mode's curve the graph is showing.
        private void UpdateHeaderTemp(int tempC)
        {
            if (FanHeaderTempLabel != null) FanHeaderTempLabel.Text = $"{tempC}°C";
        }

        // Hide the ON-GRAPH live indicators (temp dot, RPM line) and the in-graph driving-temp
        // label when viewing a non-active mode — they can't honestly map onto a curve that isn't
        // running. The header Fan/temp readout is left untouched: it stays live because it
        // reflects real hardware, not the displayed curve.
        private void HideLiveIndicators()
        {
            if (TempIndicatorLine != null) TempIndicatorLine.Visibility = Visibility.Collapsed;
            if (RPMIndicatorLine != null) RPMIndicatorLine.Visibility = Visibility.Collapsed;
            if (CurrentTempLabel != null) CurrentTempLabel.Text = "--";
        }

        private void UpdateFanCurveGraphTemp(int tempC)
        {
            if (CurrentTempLabel != null)
            {
                CurrentTempLabel.Text = $"{tempC}°C";
            }
            UpdateTemperatureIndicator(tempC);
        }

        private void OnFanRPMUpdated(int rpm)
        {
            // Header RPM is a live hardware reading — always show it while the card is open
            // (the card being expanded is what gates polling; which mode's curve you're viewing
            // below is irrelevant to the fan's actual RPM). This is the fix for the readout
            // showing "-- RPM" whenever the viewed mode differed from the running mode.
            if (FanRPMLabel != null) FanRPMLabel.Text = $"{rpm} RPM";

            // The on-graph RPM line maps onto the displayed curve, so only draw it for the
            // curve that's actually running.
            if (IsViewingActiveFanCurveMode())
                UpdateRPMIndicator(rpm);
            else if (RPMIndicatorLine != null)
                RPMIndicatorLine.Visibility = Visibility.Collapsed;
        }

        private void UpdateRPMIndicator(int rpm)
        {
            if (RPMIndicatorLine == null || FanCurveCanvas == null)
                return;

            double width = FanCurveCanvas.ActualWidth;
            double height = FanCurveCanvas.ActualHeight;

            if (width <= 0 || height <= 0) return;

            // Convert RPM to percentage (max 7500 RPM for Legion Go EC scale)
            const int MAX_RPM = 7500;
            double percent = Math.Max(0, Math.Min(100, (double)rpm / MAX_RPM * 100));

            // Calculate Y position (inverted - 0% at bottom, 100% at top)
            double y = height - (percent / 100.0 * height);

            RPMIndicatorLine.X1 = 0;
            RPMIndicatorLine.X2 = width;
            RPMIndicatorLine.Y1 = y;
            RPMIndicatorLine.Y2 = y;
            RPMIndicatorLine.Visibility = Windows.UI.Xaml.Visibility.Visible;
        }

        private void FanCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (fanCurveGraphInitialized)
            {
                // Clear old grid lines
                var toRemove = new System.Collections.Generic.List<Windows.UI.Xaml.UIElement>();
                foreach (var child in FanCurveCanvas.Children)
                {
                    if (child is Windows.UI.Xaml.Shapes.Line line && line != TempIndicatorLine && line != RPMIndicatorLine)
                    {
                        toRemove.Add(child);
                    }
                }
                foreach (var item in toRemove)
                {
                    FanCurveCanvas.Children.Remove(item);
                }

                DrawGridLines();
                UpdateFanCurveGraph();

                // Re-update temp indicator from the CPU temperature (always — see OnCPUTempUpdated).
                int? activeTemp = legionCPUTemp != null && legionCPUTemp.Value > 0 ? legionCPUTemp.Value : (int?)null;
                if (activeTemp.HasValue)
                {
                    UpdateTemperatureIndicator(activeTemp.Value);
                }
            }
        }

        // Refreshes the graph's temp-display strip and the firmware protection-floor
        // visibility. Called on init and whenever the running power mode changes.
        // (Name kept from when the card also had an EC-override toggle.)
        private void RefreshFanCurveGraphForUnlockState()
        {
            // The live sensor routing and protection-floor visibility describe what's running
            // right now, so they only apply while the Custom curve is the running one.
            bool viewingActive = IsViewingActiveFanCurveMode();

            if (CurrentTempPrefixLabel != null)
                CurrentTempPrefixLabel.Text = "CPU Temp: ";

            // The protection-floor line + legend describe the running curve, so hide them
            // while the console is running a different mode.
            if (ECFloorPolyline != null)
                ECFloorPolyline.Visibility = viewingActive ? Visibility.Visible : Visibility.Collapsed;
            if (ECFloorLegendPanel != null)
                ECFloorLegendPanel.Visibility = viewingActive ? Visibility.Visible : Visibility.Collapsed;

            // Temperature axis labels describe the curve's breakpoint storage
            // (10°C…100°C). Lenovo's firmware may map sensor temp to curve point
            // differently; the Info expander notes that. Showing the labels gives the
            // user a temperature anchor while editing.
            if (FanCurveTempAxisGrid != null)
                FanCurveTempAxisGrid.Visibility = Visibility.Visible;
            if (FanCurveLockedAxisHint != null)
                FanCurveLockedAxisHint.Visibility = Visibility.Collapsed;

            // Hide live temp/RPM indicators when the console isn't running Custom — they
            // can't honestly map onto a curve that isn't currently driving the fan.
            if (!viewingActive)
            {
                HideLiveIndicators();
                return;
            }

            // Push the current CPU temperature now so the label + indicator refresh immediately
            // rather than waiting for the next sensor tick.
            int? newTemp = legionCPUTemp != null && legionCPUTemp.Value > 0 ? legionCPUTemp.Value : (int?)null;
            if (newTemp.HasValue)
            {
                UpdateFanCurveGraphTemp(newTemp.Value);
            }
        }

        private void FanCurveCanvas_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (FanCurveCanvas == null) return;

            var point = e.GetCurrentPoint(FanCurveCanvas).Position;

            // Find the closest control point
            double minDist = double.MaxValue;
            int closestIndex = -1;

            for (int i = 0; i < 10; i++)
            {
                if (fanCurvePoints[i] == null) continue;

                double px = Canvas.GetLeft(fanCurvePoints[i]) + 8;
                double py = Canvas.GetTop(fanCurvePoints[i]) + 8;

                double dist = Math.Sqrt(Math.Pow(point.X - px, 2) + Math.Pow(point.Y - py, 2));
                if (dist < minDist && dist < 30) // 30px hit area
                {
                    minDist = dist;
                    closestIndex = i;
                }
            }

            if (closestIndex >= 0)
            {
                draggedPointIndex = closestIndex;
                isDraggingPoint = true;
                FanCurveCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void FanCurveCanvas_PointerMoved(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!isDraggingPoint || draggedPointIndex < 0 || FanCurveCanvas == null)
                return;

            var point = e.GetCurrentPoint(FanCurveCanvas).Position;
            double height = FanCurveCanvas.ActualHeight;

            // Calculate new fan speed (invert Y since 0 is at top)
            double fanSpeed = (1.0 - point.Y / height) * 100.0;

            // Enforce minimum fan speed for this temperature threshold
            int minSpeed = FanCurveMinSpeeds[draggedPointIndex];
            fanSpeed = Math.Max(minSpeed, Math.Min(100, fanSpeed));

            // Update the value
            currentFanCurveValues[draggedPointIndex] = (int)Math.Round(fanSpeed);

            // Redraw the graph
            UpdateFanCurveGraph();

            e.Handled = true;
        }

        private void FanCurveCanvas_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (isDraggingPoint && FanCurveCanvas != null)
            {
                FanCurveCanvas.ReleasePointerCapture(e.Pointer);

                // Update the cache for the Custom-mode slot — the slot the edit targets
                // (NOT necessarily the active mode).
                fanCurveCache[selectedFanCurveMode] = (int[])currentFanCurveValues.Clone();

                // Push to helper via the per-mode channel so the helper persists this
                // edit in the right slot. Helper will only write to hardware if Custom
                // happens to be the running power mode.
                if (legionFanCurvePerMode != null)
                {
                    legionFanCurvePerMode.SendForMode(selectedFanCurveMode, currentFanCurveValues);
                }
            }

            draggedPointIndex = -1;
            isDraggingPoint = false;
            e.Handled = true;
        }

    }
}
