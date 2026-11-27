using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;

namespace XboxGamingBar
{
    // The Quick tab power card (1.9.9a; it replaced the 1.9.7c TDP Mode / AutoTDP boxes), the Power
    // tab's TDP mode / Windows power mode buttons and FPS presets, and the Profiles tab quick
    // buttons. All of them drive the Power tab's own controls (TDPModeComboBox, CustomTDPSlowSlider,
    // AutoTDPToggle, AutoTDPTargetFPSSlider, OSPowerModeComboBox, FPSLimitSlider, or a tile action),
    // so the sending, locking and saving logic stays in one place, and they follow those controls back.
    public sealed partial class GamingWidget
    {
        private bool isUpdatingQuickPowerBoxes;
        private static readonly int[] QuickTdpModeValues = { 1, 2, 3, 255 }; // Quiet, Balanced, Performance, Custom

        /// <summary>
        /// Switches the TDP mode the way the Performance tab dropdown does (used by the TDP Mode
        /// box and the TDP Mode tile / tile hotkey). Ignored while AutoTDP holds the mode.
        /// </summary>
        private void ApplyTdpModeFromQuickSettings(int nextMode)
        {
            if (AutoTDPToggle?.IsOn == true) return;
            bool isLegion = legionGoDetected?.Value == true;

            if (isLegion && legionPerformanceMode != null)
            {
                legionPerformanceMode.SetValue(nextMode);
            }

            int nextIndex = Array.IndexOf(QuickTdpModeValues, nextMode);
            if (nextIndex >= 0 && TDPModeComboBox != null)
            {
                isUserInitiatedTDPModeChange = true;
                TDPModeComboBox.SelectedIndex = nextIndex;
                isUserInitiatedTDPModeChange = false;
            }

            // Entering Custom on Legion: re-assert the Custom limits once the mode has landed.
            if (isLegion && nextMode == 255)
            {
                ScheduleQsTdpReapply();
            }
            UpdateQuickPowerBoxes();
        }

        private void OSPowerModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !int.TryParse(fe.Tag as string, out int index)) return;
            if (OSPowerModeComboBox == null || !OSPowerModeComboBox.IsEnabled) return;
            if (OSPowerModeComboBox.SelectedIndex != index)
                OSPowerModeComboBox.SelectedIndex = index; // its SelectionChanged handler applies it
        }

        private void PaintOSPowerModeButtons()
        {
            var buttons = new[] { OSPowerModeEfficiencyButton, OSPowerModeBalancedButton, OSPowerModePerformanceButton };
            int index = OSPowerModeComboBox?.SelectedIndex ?? -1;
            bool enabled = OSPowerModeComboBox?.IsEnabled ?? false;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] == null) continue;
                PaintSegmentButton(buttons[i], i == index);
                buttons[i].IsEnabled = enabled;
            }
        }

        private void FPSLimitPreset_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !int.TryParse(fe.Tag as string, out int fps)) return;
            if (FPSLimitSlider == null) return;
            if ((int)Math.Round(FPSLimitSlider.Value) != fps)
                FPSLimitSlider.Value = fps; // the slider's handler sends it
        }

        private void PaintFpsLimitPresets()
        {
            if (FPSLimitPresetGrid == null || FPSLimitSlider == null) return;
            int value = (int)Math.Round(FPSLimitSlider.Value);
            foreach (var child in FPSLimitPresetGrid.Children)
            {
                if (child is Button b && int.TryParse(b.Tag as string, out int fps))
                    PaintSegmentButton(b, fps == value);
            }
        }

        // ---- Profiles tab quick buttons (1.9.9a) ----------------------------------------------

        /// <summary>Runs the same action as the matching Quick Settings tile (tile id in Tag).</summary>
        private void ProfileQuickButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is string tileId)) return;
            SimulateTileHotkeyFired(tileId);
            // Tile actions update their controls right away or after the helper answers.
            PaintProfileQuickButtons();
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, PaintProfileQuickButtons);
        }

        /// <summary>Shows each setting's current value; values that are "on" use the accent colour.</summary>
        private void PaintProfileQuickButtons()
        {
            if (ProfileQuickButtonsGrid == null) return;
            try
            {
                var accent = new SolidColorBrush(CurrentAccentColor);
                var white = new SolidColorBrush(Windows.UI.Colors.White);
                var muted = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x9A, 0xA3, 0xAF));
                void Set(TextBlock text, string value, bool on)
                {
                    if (text == null) return;
                    text.Text = value;
                    text.Foreground = on ? accent : (value == "Off" ? muted : white);
                }

                bool autoOn = AutoTDPToggle?.IsOn == true;
                int index = TDPModeComboBox?.SelectedIndex ?? -1;
                int spl = (int)Math.Round(CustomTDPSlowSlider?.Value ?? 15);
                int target = (int)Math.Round(AutoTDPTargetFPSSlider?.Value ?? 60);
                string tdp = autoOn ? "Auto"
                    : IsCustomTdpModeSelected() ? $"Custom {spl} W"
                    : index >= 0 && index < QuickTdpModeValues.Length ? GetLegionModeShortName(QuickTdpModeValues[index]) : "--";
                Set(ProfileQuickTdpText, tdp, false);
                Set(ProfileQuickAutoTdpText, autoOn ? $"{target} FPS" : "Off", autoOn);

                bool fpsOn = FPSLimitToggle?.IsOn == true;
                Set(ProfileQuickFpsText, fpsOn ? $"{(int)Math.Round(FPSLimitSlider?.Value ?? 0)} FPS" : "Off", fpsOn);

                bool overlayOn = PerformanceOverlayToggle?.IsOn == true;
                Set(ProfileQuickOverlayText, overlayOn ? "On" : "Off", overlayOn);

                string[] powerModes = { "Efficiency", "Balanced", "Performance" };
                int pm = OSPowerModeComboBox?.SelectedIndex ?? -1;
                Set(ProfileQuickPowerModeText, pm >= 0 && pm < powerModes.Length ? powerModes[pm] : "--", false);

                bool boostOn = CPUBoostToggle?.IsOn == true;
                Set(ProfileQuickCpuBoostText, boostOn ? "On" : "Off", boostOn);
            }
            catch (Exception ex)
            {
                Logger.Debug($"PaintProfileQuickButtons failed: {ex.Message}");
            }
        }

        /// <summary>Selected segment: accent background and dark text; otherwise the default button look.</summary>
        private static void PaintSegmentButton(Button button, bool selected)
        {
            if (selected)
            {
                button.Background = new SolidColorBrush(CurrentAccentColor);
                button.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0A, 0x1A, 0x24));
            }
            else
            {
                button.ClearValue(Control.BackgroundProperty);
                button.Foreground = new SolidColorBrush(Windows.UI.Colors.White);
            }
        }

        /// <summary>Keeps the boxes in step with the controls they drive (wired once).</summary>
        private void SubscribeQuickPowerBoxes()
        {
            if (TDPModeComboBox != null) TDPModeComboBox.SelectionChanged += (s, e) => UpdateQuickPowerBoxes();
            if (CustomTDPSlowSlider != null) CustomTDPSlowSlider.ValueChanged += (s, e) => UpdateQuickPowerBoxes();
            if (AutoTDPToggle != null) AutoTDPToggle.Toggled += (s, e) => UpdateQuickPowerBoxes();
            if (AutoTDPTargetFPSSlider != null) AutoTDPTargetFPSSlider.ValueChanged += (s, e) => UpdateQuickPowerBoxes();
            // The dial shows the live wattage (presets and AutoTDP) from the helper's CurrentTDP push,
            // and AutoTDP's arc moves when its Min / Max TDP change.
            if (currentTdp != null)
            {
                currentTdp.PropertyChanged += (s, e) => OnCurrentTdpPushed(currentTdp.Value);
                OnCurrentTdpPushed(currentTdp.Value);
            }
            if (AutoTDPMinSlider != null) AutoTDPMinSlider.ValueChanged += (s, e) => UpdateQuickPowerBoxes();
            if (AutoTDPMaxSlider != null) AutoTDPMaxSlider.ValueChanged += (s, e) => UpdateQuickPowerBoxes();
            // Power tab buttons (1.9.9a) that stand in for the hidden ComboBox / slider presets.
            if (OSPowerModeComboBox != null)
            {
                OSPowerModeComboBox.SelectionChanged += (s, e) => PaintOSPowerModeButtons();
                OSPowerModeComboBox.IsEnabledChanged += (s, e) => PaintOSPowerModeButtons();
            }
            if (FPSLimitSlider != null) FPSLimitSlider.ValueChanged += (s, e) => PaintFpsLimitPresets();
            // Profiles tab quick buttons follow the controls they mirror.
            if (FPSLimitToggle != null) FPSLimitToggle.Toggled += (s, e) => PaintProfileQuickButtons();
            if (FPSLimitSlider != null) FPSLimitSlider.ValueChanged += (s, e) => PaintProfileQuickButtons();
            if (PerformanceOverlayToggle != null) PerformanceOverlayToggle.Toggled += (s, e) => PaintProfileQuickButtons();
            if (OSPowerModeComboBox != null) OSPowerModeComboBox.SelectionChanged += (s, e) => PaintProfileQuickButtons();
            if (CPUBoostToggle != null) CPUBoostToggle.Toggled += (s, e) => PaintProfileQuickButtons();
            PaintOSPowerModeButtons();
            PaintFpsLimitPresets();
            UpdateQuickPowerBoxes();
        }

        private void QuickTdpModeOption_Click(object sender, RoutedEventArgs e)
        {
            if (isUpdatingQuickPowerBoxes) return;
            if (!(sender is FrameworkElement fe) || !int.TryParse(fe.Tag as string, out int index)) return;
            if (index < 0 || index >= QuickTdpModeValues.Length) return;

            // 1.9.9c: picking a mode while AutoTDP runs turns AutoTDP off and switches to that
            // mode. Clearing the remembered pre-AutoTDP mode first stops the off path from
            // restoring it over the user's choice (Custom keeps the Custom card's limits, which
            // ScheduleCustomTDPRestoreAfterAutoTDP re-applies).
            if (AutoTDPToggle?.IsOn == true)
            {
                Logger.Info($"TDP mode button {index} pressed while AutoTDP is on: turning AutoTDP off");
                tdpModeIndexBeforeAutoTDP = null;
                AutoTDPToggle.IsOn = false; // AutoTDPToggle_Toggled -> ApplyAutoTDPToggle(false)
                if (AutoTDPToggle.IsOn) return; // the switch refused (e.g. mid helper update)
            }
            ApplyTdpModeFromQuickSettings(QuickTdpModeValues[index]);
        }

        private void QuickTdpSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            int watts = (int)Math.Round(e.NewValue);
            if (isUpdatingQuickPowerBoxes || CustomTDPSlowSlider == null) return;
            // Only usable in Custom, and not while AutoTDP picks the wattage.
            if (!IsCustomTdpModeSelected() || AutoTDPToggle?.IsOn == true) return;
            if ((int)Math.Round(CustomTDPSlowSlider.Value) != watts)
            {
                CustomTDPSlowSlider.Value = watts; // its handler applies the limits live
            }
        }

        private void QuickAutoTdpToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (isUpdatingQuickPowerBoxes || AutoTDPToggle == null || QuickAutoTdpToggle == null) return;
            if (AutoTDPToggle.IsOn != QuickAutoTdpToggle.IsOn)
            {
                AutoTDPToggle.IsOn = QuickAutoTdpToggle.IsOn; // AutoTDPToggle_Toggled does the rest
            }
        }

        /// <summary>The − / + target FPS buttons: step the Power tab's target slider by Tag (±5).</summary>
        private void QuickAutoTdpStep_Click(object sender, RoutedEventArgs e)
        {
            if (AutoTDPTargetFPSSlider == null || AutoTDPToggle?.IsOn != true) return;
            if (!(sender is FrameworkElement fe) || !int.TryParse(fe.Tag as string, out int step)) return;
            double next = Math.Max(AutoTDPTargetFPSSlider.Minimum, Math.Min(AutoTDPTargetFPSSlider.Maximum, AutoTDPTargetFPSSlider.Value + step));
            if (next != AutoTDPTargetFPSSlider.Value)
                AutoTDPTargetFPSSlider.Value = next; // its debounced handler sends and saves it
        }

        /// <summary>
        /// Paints the power card from the controls it drives, without sending anything: the dial
        /// (Custom watts, the preset's level, or AutoTDP's live FPS against its target), the
        /// selected mode button, the mini Custom slider and the AutoTDP row.
        /// </summary>
        private void UpdateQuickPowerBoxes()
        {
            if (QuickTdpDialArc == null || isUpdatingQuickPowerBoxes) return;
            isUpdatingQuickPowerBoxes = true;
            try
            {
                bool autoOn = AutoTDPToggle?.IsOn == true;
                int index = TDPModeComboBox?.SelectedIndex ?? -1;
                bool isCustom = IsCustomTdpModeSelected();
                int spl = (int)Math.Round(CustomTDPSlowSlider?.Value ?? 15);
                int target = (int)Math.Round(AutoTDPTargetFPSSlider?.Value ?? 60);
                int mode = index >= 0 && index < QuickTdpModeValues.Length ? QuickTdpModeValues[index] : 2;

                var accentColor = CurrentAccentColor;
                var accent = new SolidColorBrush(accentColor);
                var onAccent = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0A, 0x1A, 0x24));
                var white = new SolidColorBrush(Windows.UI.Colors.White);

                // Dial and title
                if (autoOn)
                {
                    // AutoTDP: the wattage it is running at, the arc placed between its Min and Max TDP.
                    double min = AutoTDPMinSlider?.Value ?? 4;
                    double max = AutoTDPMaxSlider?.Value ?? CustomTdpMaxWatts;
                    int watts = liveTdpIsPreset ? 0 : liveSplWatts;
                    SetQuickDial(watts > 0 ? watts.ToString() : "--", "WATTS", null,
                        watts > 0 && max > min ? (watts - min) / (max - min) : 0);
                }
                else if (isCustom)
                {
                    SetQuickDial(spl.ToString(), "WATTS", null, spl / (double)CustomTdpMaxWatts);
                }
                else
                {
                    // Preset: the firmware doesn't report a preset's own limits (Lenovo WMI returns the
                    // same value in every preset, checked on device), so show the SoC's live draw
                    // (helper metrics feed, 1.9.9d); the preset's icon until the first reading.
                    if (currentMetricsData.TryGetValue("socPower", out double soc) && soc > 0)
                    {
                        SetQuickDial(Math.Round(soc).ToString("0"), "W DRAW", null, soc / CustomTdpMaxWatts);
                    }
                    else
                    {
                        switch (mode)
                        {
                            case 1: SetQuickDial(null, "QUIET", "\uEC48", 1 / 3.0); break;
                            case 3: SetQuickDial(null, "PERF.", "\uEC4A", 1.0); break;
                            default: SetQuickDial(null, "BALANCED", "\uEC49", 2 / 3.0); break;
                        }
                    }
                }

                // Mode buttons: the selected one is filled with the accent colour.
                var buttons = new[]
                {
                    QuickTdpModeQuiet, QuickTdpModeBalanced, QuickTdpModePerformance, QuickTdpModeCustom,
                    PerfTdpModeQuiet, PerfTdpModeBalanced, PerfTdpModePerformance, PerfTdpModeCustom
                };
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i] == null) continue;
                    // 1.9.9c: the selected mode stays lit while AutoTDP holds it (Custom); the
                    // buttons stay usable - pressing one turns AutoTDP off (QuickTdpModeOption_Click).
                    bool selected = (i % 4) == index;
                    if (selected)
                    {
                        buttons[i].Background = accent;
                        buttons[i].Foreground = onAccent;
                    }
                    else
                    {
                        buttons[i].ClearValue(Control.BackgroundProperty);
                        buttons[i].Foreground = white;
                    }
                    buttons[i].IsEnabled = true;
                    buttons[i].Opacity = 1.0;
                }

                if (PerfTdpModeHint != null) PerfTdpModeHint.Text = autoOn ? "A mode turns AutoTDP off" : "";

                // Custom wattage: a smaller dial with the mini slider under it
                bool showSlider = isCustom && !autoOn;
                if (QuickTdpSlider != null)
                {
                    QuickTdpSlider.Visibility = showSlider ? Visibility.Visible : Visibility.Collapsed;
                    QuickTdpSlider.Value = spl;
                }
                if (QuickTdpDialBox != null) QuickTdpDialBox.Width = QuickTdpDialBox.Height = showSlider ? 70 : 94;

                // AutoTDP: the target stepper only works (and looks active) while AutoTDP is on
                if (QuickAutoTdpToggle != null) QuickAutoTdpToggle.IsOn = autoOn;
                if (QuickAutoTdpTargetText != null) QuickAutoTdpTargetText.Text = target.ToString();
                if (QuickAutoTdpTargetPanel != null) QuickAutoTdpTargetPanel.Opacity = autoOn ? 1.0 : 0.4;
                if (QuickAutoTdpMinusButton != null) QuickAutoTdpMinusButton.IsEnabled = autoOn && target > (AutoTDPTargetFPSSlider?.Minimum ?? 30);
                if (QuickAutoTdpPlusButton != null) QuickAutoTdpPlusButton.IsEnabled = autoOn && target < (AutoTDPTargetFPSSlider?.Maximum ?? 144);

                PaintProfileQuickButtons();
            }
            catch (Exception ex)
            {
                Logger.Debug($"UpdateQuickPowerBoxes failed: {ex.Message}");
            }
            finally
            {
                isUpdatingQuickPowerBoxes = false;
            }
        }

        private const int CustomTdpMaxWatts = 35;

        // Live limits from the helper's CurrentTDP push (every few seconds): "SPL:15W SPPL:.. FPPT:.."
        // in Custom (AutoTDP included), "Balanced SPL:15W ..." in a preset (1.9.9c), or a bare mode name.
        private int liveSplWatts;
        private bool liveTdpIsPreset;
        private string liveTdpModeName = "";

        private void OnCurrentTdpPushed(string value)
        {
            int watts = 0;
            bool preset = false;
            string modeName = "";
            if (!string.IsNullOrEmpty(value))
            {
                var match = System.Text.RegularExpressions.Regex.Match(value, @"\bSPL:(\d+)W");
                if (match.Success) int.TryParse(match.Groups[1].Value, out watts);
                preset = !value.StartsWith("SPL:", StringComparison.Ordinal);
                if (preset)
                {
                    int space = value.IndexOf(' ');
                    modeName = space > 0 ? value.Substring(0, space) : value;
                }
            }
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                liveSplWatts = watts;
                liveTdpIsPreset = preset;
                liveTdpModeName = modeName;
                UpdateQuickPowerBoxes();
            });
        }

        /// <summary>Sets the dial's centre (a number, or a glyph when <paramref name="text"/> is null),
        /// its unit line and the value arc (0 to 1 of the 270 degree track).</summary>
        private void SetQuickDial(string text, string unit, string glyph, double fraction)
        {
            if (QuickTdpDialText != null)
            {
                QuickTdpDialText.Text = text ?? "";
                QuickTdpDialText.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
            }
            if (QuickTdpDialIcon != null)
            {
                if (glyph != null) QuickTdpDialIcon.Glyph = glyph;
                QuickTdpDialIcon.Visibility = glyph != null ? Visibility.Visible : Visibility.Collapsed;
            }
            if (QuickTdpDialUnit != null) QuickTdpDialUnit.Text = unit;
            if (QuickTdpDialArc != null) QuickTdpDialArc.Data = BuildDialArc(fraction);
        }

        /// <summary>Arc on the dial's circle (centre 52,52, radius 46) from 135 degrees, clockwise.</summary>
        private static Geometry BuildDialArc(double fraction)
        {
            fraction = Math.Max(0, Math.Min(1, double.IsNaN(fraction) ? 0 : fraction));
            if (fraction < 0.005) return null;
            const double cx = 52, cy = 52, r = 46, startDeg = 135, spanDeg = 270;
            double endDeg = startDeg + spanDeg * Math.Min(fraction, 0.999);
            var start = new Windows.Foundation.Point(cx + r * Math.Cos(startDeg * Math.PI / 180), cy + r * Math.Sin(startDeg * Math.PI / 180));
            var end = new Windows.Foundation.Point(cx + r * Math.Cos(endDeg * Math.PI / 180), cy + r * Math.Sin(endDeg * Math.PI / 180));
            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Windows.Foundation.Size(r, r),
                IsLargeArc = spanDeg * fraction > 180,
                SweepDirection = SweepDirection.Clockwise
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }
    }
}