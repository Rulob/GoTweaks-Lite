using System;
using System.Collections.Generic;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace XboxGamingBar
{
    /// <summary>
    /// D-pad focus order for the Power (formerly Performance) tab.
    ///
    /// The tab is a stack of cards whose content changes at runtime (a game being detected, the
    /// FPS-limit slider appearing, the Custom power-limit sliders in Custom mode, AutoTDP's
    /// settings when it's on, controls being disabled while AutoTDP owns TDP). Hand-written
    /// XYFocusUp/XYFocusDown links went stale as cards were added, so D-pad Down skipped the FPS
    /// Limit and AutoTDP toggles. Instead, the whole vertical order is declared once below and the
    /// links are rebuilt from whichever controls are currently visible and enabled.
    /// </summary>
    public sealed partial class GamingWidget
    {
        private bool performanceFocusChainRebuildPending;

        /// <summary>
        /// The Power tab's controls, top to bottom, as rows. 1.9.9d: rows of buttons (FPS presets,
        /// TDP modes, Windows power mode) replaced the dropdowns, so each row is one stop: Up/Down
        /// move between rows (to the button in the matching position), Left/Right stay in the row.
        /// </summary>
        private IEnumerable<Control[]> PerformanceTabFocusRows()
        {
            yield return new Control[] { PerformanceOverlayToggle };
            yield return new Control[] { FPSLimitToggle };
            yield return ButtonsIn(FPSLimitPresetGrid);
            yield return new Control[] { FPSLimitSlider };
            yield return new Control[] { AutoTDPToggle };
            yield return new Control[] { AutoTDPTargetFPSSlider };
            yield return new Control[] { AutoTDPMinSlider };
            yield return new Control[] { AutoTDPMaxSlider };
            yield return new Control[] { AutoTDPPauseWhenUnfocusedToggle };
            yield return new Control[] { AutoTDPFastResponseToggle };
            yield return new Control[] { PerfTdpModeQuiet, PerfTdpModeBalanced, PerfTdpModePerformance, PerfTdpModeCustom };
            yield return new Control[] { CustomTDPSlowSlider };
            yield return new Control[] { CustomTDPFastSlider };
            yield return new Control[] { CustomTDPPeakSlider };
            yield return new Control[] { OSPowerModeEfficiencyButton, OSPowerModeBalancedButton, OSPowerModePerformanceButton };
            yield return new Control[] { CPUExtrasExpandToggle };
        }

        private static Control[] ButtonsIn(Panel panel)
        {
            var buttons = new List<Control>();
            if (panel != null)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Button b) buttons.Add(b);
                }
            }
            return buttons.ToArray();
        }

        /// <summary>
        /// Hooks the Power tab up so its focus order is rebuilt whenever a control gets
        /// enabled/disabled or the FPS limit / AutoTDP toggles show or hide their settings.
        /// Call once after InitializeComponent.
        /// </summary>
        private void InitPerformanceTabFocusChain()
        {
            foreach (var row in PerformanceTabFocusRows())
            {
                foreach (var control in row)
                {
                    if (control != null)
                    {
                        control.IsEnabledChanged += (s, e) => SchedulePerformanceFocusChainRebuild();
                    }
                }
            }

            // These reveal/hide their own settings (the FPS limit row, AutoTDP's panel, the Custom
            // card) through bindings or code that run just after the change.
            if (FPSLimitToggle != null) FPSLimitToggle.Toggled += (s, e) => SchedulePerformanceFocusChainRebuild();
            if (AutoTDPToggle != null) AutoTDPToggle.Toggled += (s, e) => SchedulePerformanceFocusChainRebuild();
            if (TDPModeComboBox != null) TDPModeComboBox.SelectionChanged += (s, e) => SchedulePerformanceFocusChainRebuild();

            RebuildPerformanceTabFocusChain();
        }

        /// <summary>Rebuilds the focus order once the current layout/binding pass has settled.</summary>
        private void SchedulePerformanceFocusChainRebuild()
        {
            if (performanceFocusChainRebuildPending) return;
            performanceFocusChainRebuildPending = true;

            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                performanceFocusChainRebuildPending = false;
                RebuildPerformanceTabFocusChain();
            });
        }

        /// <summary>
        /// Links every row of currently focusable Power-tab controls to the rows above and below.
        /// </summary>
        private void RebuildPerformanceTabFocusChain()
        {
            try
            {
                if (PerformanceNavItem == null || PerformanceScrollViewer == null) return;

                var rows = new List<List<Control>>();
                foreach (var row in PerformanceTabFocusRows())
                {
                    var stops = new List<Control>();
                    foreach (var control in row)
                    {
                        if (IsPerformanceFocusStop(control)) stops.Add(control);
                    }
                    if (stops.Count > 0) rows.Add(stops);
                }

                if (rows.Count == 0) return;

                PerformanceNavItem.XYFocusDown = rows[0][0];
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    for (int i = 0; i < row.Count; i++)
                    {
                        row[i].XYFocusUp = r == 0 ? (DependencyObject)PerformanceNavItem : Matching(rows[r - 1], i, row.Count);
                        // Leave the last row's "down" alone so the controls below the list (the CPU
                        // Extras content when it's expanded) keep their normal navigation.
                        if (r < rows.Count - 1)
                        {
                            row[i].XYFocusDown = Matching(rows[r + 1], i, row.Count);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"RebuildPerformanceTabFocusChain failed: {ex.Message}");
            }
        }

        /// <summary>The control in <paramref name="target"/> at the same relative position as
        /// index <paramref name="index"/> of a row of <paramref name="count"/>.</summary>
        private static Control Matching(List<Control> target, int index, int count)
        {
            int i = count <= 1 ? 0 : (int)Math.Round(index * (target.Count - 1) / (double)(count - 1));
            return target[Math.Max(0, Math.Min(target.Count - 1, i))];
        }
        /// <summary>
        /// True when the control can take focus right now: enabled, and neither it nor any card
        /// or panel above it (up to the tab itself) is collapsed. The tab's own visibility is
        /// ignored so the order stays correct while another tab is showing.
        /// </summary>
        private bool IsPerformanceFocusStop(Control control)
        {
            if (control == null || !control.IsEnabled || !control.IsTabStop) return false;

            DependencyObject current = control;
            while (current != null && !ReferenceEquals(current, PerformanceScrollViewer))
            {
                if (current is UIElement element && element.Visibility != Visibility.Visible)
                {
                    return false;
                }
                current = VisualTreeHelper.GetParent(current);
            }

            // Reaching the root without meeting the tab means the control isn't on this tab.
            return ReferenceEquals(current, PerformanceScrollViewer);
        }
    }
}
