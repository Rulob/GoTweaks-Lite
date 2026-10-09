using System;
using System.Collections.Generic;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace XboxGamingBar
{
    /// <summary>
    /// D-pad focus order for the Performance tab.
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

        /// <summary>The Performance tab's controls, top to bottom.</summary>
        private IEnumerable<Control> PerformanceTabFocusOrder()
        {
            yield return PerGameProfileToggle;
            yield return PowerSourceProfileToggle;
            yield return PerformanceOverlayToggle;
            yield return FPSLimitToggle;
            yield return FPSLimitSlider;
            yield return TDPModeComboBox;
            yield return CustomTDPSlowSlider;
            yield return CustomTDPFastSlider;
            yield return CustomTDPPeakSlider;
            yield return AutoTDPToggle;
            yield return AutoTDPTargetFPSSlider;
            yield return AutoTDPMinSlider;
            yield return AutoTDPMaxSlider;
            yield return AutoTDPPauseWhenUnfocusedToggle;
            yield return OSPowerModeComboBox;
            yield return CPUExtrasExpandToggle;
        }

        /// <summary>
        /// Hooks the Performance tab up so its focus order is rebuilt whenever a control gets
        /// enabled/disabled or the FPS limit / AutoTDP toggles show or hide their settings.
        /// Call once after InitializeComponent.
        /// </summary>
        private void InitPerformanceTabFocusChain()
        {
            foreach (var control in PerformanceTabFocusOrder())
            {
                if (control != null)
                {
                    control.IsEnabledChanged += (s, e) => SchedulePerformanceFocusChainRebuild();
                }
            }

            // These two reveal/hide their own settings (the FPS limit slider, AutoTDP's panel)
            // through bindings, which update just after Toggled fires.
            if (FPSLimitToggle != null) FPSLimitToggle.Toggled += (s, e) => SchedulePerformanceFocusChainRebuild();
            if (AutoTDPToggle != null) AutoTDPToggle.Toggled += (s, e) => SchedulePerformanceFocusChainRebuild();

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
        /// Links every currently focusable Performance-tab control to the one above and below it.
        /// </summary>
        private void RebuildPerformanceTabFocusChain()
        {
            try
            {
                if (PerformanceNavItem == null || PerformanceScrollViewer == null) return;

                var stops = new List<Control>();
                foreach (var control in PerformanceTabFocusOrder())
                {
                    if (IsPerformanceFocusStop(control))
                    {
                        stops.Add(control);
                    }
                }

                if (stops.Count == 0) return;

                PerformanceNavItem.XYFocusDown = stops[0];
                for (int i = 0; i < stops.Count; i++)
                {
                    stops[i].XYFocusUp = i == 0 ? (DependencyObject)PerformanceNavItem : stops[i - 1];
                    // Leave the last stop's "down" alone so the controls below the list (the CPU
                    // Extras content when it's expanded) keep their normal navigation.
                    if (i < stops.Count - 1)
                    {
                        stops[i].XYFocusDown = stops[i + 1];
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"RebuildPerformanceTabFocusChain failed: {ex.Message}");
            }
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
