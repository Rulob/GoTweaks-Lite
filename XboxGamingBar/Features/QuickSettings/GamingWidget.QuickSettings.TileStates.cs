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
        /// Rebuild tile grid with only visible tiles, in 3-column layout
        /// </summary>
        private void RebuildQuickSettingsTiles()
        {
            if (QuickSettingsTilesContainer == null) return;

            QuickSettingsTilesContainer.Children.Clear();

            // Get tiles to display - in edit mode show all (including hidden), otherwise only visible
            var tilesToShow = qsTileDefinitions
                .Where(t => !ShouldSkipTile(t) && (qsEditMode || t.IsVisible))
                .OrderBy(t => t.Order)
                .ToList();

            // Build rows of tiles (3 or 4 columns based on setting)
            Grid currentRow = null;
            int colIndex = 0;

            for (int i = 0; i < tilesToShow.Count; i++)
            {
                if (colIndex == 0)
                {
                    currentRow = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                    // Add column definitions dynamically based on qsColumnCount
                    for (int c = 0; c < qsColumnCount; c++)
                    {
                        if (c > 0) currentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });  // Spacer
                        currentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    }
                    QuickSettingsTilesContainer.Children.Add(currentRow);
                }

                var tile = tilesToShow[i];
                var tileButton = CreateTileButton(tile);

                // In edit mode, stack a controller-combo bind button under each tile so a
                // combo can be assigned to activate it (helper fires it via ControllerHotkeyMonitor).
                FrameworkElement cellContent = tileButton;
                if (qsEditMode)
                {
                    var cell = new Grid();
                    cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(tileButton, 0);
                    cell.Children.Add(tileButton);

                    var bindButton = CreateTileComboBindButton(tile);
                    Grid.SetRow(bindButton, 1);
                    cell.Children.Add(bindButton);
                    cellContent = cell;
                }

                Grid.SetColumn(cellContent, colIndex * 2);
                currentRow.Children.Add(cellContent);

                colIndex++;
                if (colIndex >= qsColumnCount)
                {
                    colIndex = 0;
                }
            }
        }

        /// <summary>
        /// Create a tile button for the given definition
        /// </summary>
        private Button CreateTileButton(TileDefinition tile)
        {
            var button = new Button
            {
                Tag = tile.Id,
                Style = Resources["QuickSettingsTileStyle"] as Style,
                // Action tiles share the same neutral background/border as every
                // other tile - only their subtitle + bottom bar color is distinct
                // (see tileActionBrush below).
                Background = tileOffBrush,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                // Override QuickSettingsTileStyle's Center default so the ContentPresenter
                // actually hands the inner StackPanel the full tile width (minus the style's
                // 12,16 padding). Without this, Center alignment sizes the ContentPresenter to
                // the StackPanel's DesiredSize — which is the widest child (the Center-aligned
                // label), leaving the state-text Canvas squeezed and the marquee scrolling in
                // a narrow strip while the tile itself looks mostly empty.
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                // Slightly taller at 4 columns (narrower tiles read as too squat
                // otherwise) - overrides the Style's Padding="10,7" default.
                Padding = qsColumnCount == 4 ? new Thickness(10, 9, 10, 9) : new Thickness(10, 7, 10, 7)
            };

            // Stretch so the state-text Canvas below gets the full tile width to scroll in
            // (otherwise the StackPanel sizes to its widest child — usually the centered label —
            // and marquees scroll in a narrow strip). Icon and label stay Center-aligned per-child
            // so the tile still looks centered.
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

            // Icon and tile name stay white always. The active-tile accent signal
            // lives in the state text color and the bottom accent bar (see
            // SetTileAccentBar below) - the tile Background stays neutral.
            // FontFamily is Segoe Fluent Icons (Windows 11's icon font) rather than
            // the FontIcon default of Segoe MDL2 Assets.
            var icon = new FontIcon
            {
                Glyph = tile.Glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 21,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            content.Children.Add(icon);
            tile.IconElement = icon;

            content.Children.Add(new TextBlock
            {
                Text = tile.Name,
                // Slightly smaller at 4 columns, where tiles are narrowest.
                FontSize = qsColumnCount == 4 ? 13 : 14,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });

            // Action tiles show "Action" instead of state, in tileActionBrush - they
            // never get revisited by UpdateQuickSettingsTileStates (no on/off state),
            // so this creation-time value is final.
            var stateText = new TextBlock
            {
                Text = tile.IsAction ? "Action" : "Off",
                FontSize = 12,
                Foreground = tile.IsAction ? tileActionBrush : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0),
                TextWrapping = TextWrapping.NoWrap
            };

            // Wrap every tile's state text in a Canvas that clips to the tile's
            // available width and supports marquee scrolling when the text overflows.
            // Width binds to the parent Grid column via HorizontalAlignment=Stretch +
            // SizeChanged so the clip geometry tracks column resizes (3/4/5 cols).
            var transform = new TranslateTransform { X = 0 };
            stateText.RenderTransform = transform;

            var canvas = new Canvas
            {
                Height = 15,
                Margin = new Thickness(0, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 0, 15) };
            canvas.Children.Add(stateText);

            // Keep the clip rect in sync with the actual laid-out width so scroll
            // calculations reflect the real tile width at 3/4/5 column settings.
            canvas.SizeChanged += (s, e) =>
            {
                if (e.NewSize.Width > 0)
                {
                    canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, 15) };
                    // Re-evaluate scroll state now that the available width changed
                    UpdateTileScrollAnimation(tile);
                }
            };

            content.Children.Add(canvas);

            tile.StateTextCanvas = canvas;
            tile.StateTextTransform = transform;

            // Active-tile accent bar: a small rounded bar centered along the bottom
            // edge, ~1/4 of the tile's own width (not just the content area) - width
            // tracks the button's actual laid-out size via SizeChanged. Muted gray
            // when the tile is off - see SetTileAccentBar. Action tiles get
            // tileActionBrush immediately since they're never revisited afterward.
            var accentBar = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, -3),
                Background = tile.IsAction ? tileActionBrush : tileBarOffBrush
            };
            content.Children.Add(accentBar);
            tile.AccentBar = accentBar;

            button.SizeChanged += (s, e) =>
            {
                if (e.NewSize.Width > 0)
                {
                    accentBar.Width = e.NewSize.Width * 0.25;
                }
            };

            button.Content = content;
            button.Click += QuickSettingsTile_Click;

            tile.TileButton = button;
            tile.StateText = stateText;

            return button;
        }

        /// <summary>
        /// Sets a tile's bottom accent bar to the live Windows accent color when on,
        /// or a muted gray (same tone as the off state text) when off - the bar is
        /// always present on every tile. Call with the same boolean condition
        /// used for that tile's StateText.Foreground.
        /// </summary>
        private void SetTileAccentBar(TileDefinition tile, bool isOn)
        {
            if (tile?.AccentBar == null) return;
            tile.AccentBar.Background = isOn ? tileAccentBrush : tileBarOffBrush;
        }

        /// <summary>
        /// Dims and disables an entire tile (not just its state text) when it's blocked by the
        /// built-in-panel-only gate (only an external monitor is active). Used by Resolution,
        /// Refresh Rate and Rotation - a dimmed-but-clickable tile with just an "N/A" label reads
        /// as a bug to the user, so this makes the whole tile visibly non-interactive instead.
        /// </summary>
        private void ApplyPanelGateVisual(TileDefinition tile, bool internalActive)
        {
            if (tile?.TileButton == null) return;
            tile.TileButton.IsEnabled = internalActive;
            tile.TileButton.Opacity = internalActive ? 1.0 : 0.4;
        }

        /// <summary>
        /// Updates the scroll animation for a tile's state text.
        /// If the rendered text is wider than the tile's column, marquees it
        /// left-right-left on a loop so the full value is readable. Otherwise
        /// centers it. Safe to call repeatedly — it stops any existing storyboard
        /// before starting a new one. Replaces the old Profile-only variant now
        /// that every tile gets the same scrolling treatment.
        /// </summary>
        private void UpdateTileScrollAnimation(TileDefinition tile)
        {
            if (tile?.StateText == null || tile.StateTextCanvas == null || tile.StateTextTransform == null)
                return;

            // Stop any existing animation
            if (tile.ScrollStoryboard != null)
            {
                tile.ScrollStoryboard.Stop();
                tile.ScrollStoryboard = null;
            }

            // Reset transform
            tile.StateTextTransform.X = 0;

            // Measure text width at its natural size
            tile.StateText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double textWidth = tile.StateText.DesiredSize.Width;

            // Prefer the actual laid-out width; fall back to the declared Width
            // when the canvas hasn't had its first layout pass yet. The declared
            // Width defaults to NaN in XAML, and `NaN <= 0` is false, so guard
            // against NaN/Infinity explicitly — otherwise it propagates into
            // TimeSpan.FromSeconds below and throws.
            double canvasWidth = tile.StateTextCanvas.ActualWidth;
            if (!(canvasWidth > 0)) canvasWidth = tile.StateTextCanvas.Width;
            if (!(canvasWidth > 0)) return; // Not yet laid out — SizeChanged will retry
            if (!(textWidth >= 0) || double.IsInfinity(textWidth)) return;

            // If text fits, no animation needed — just center it
            if (textWidth <= canvasWidth)
            {
                Canvas.SetLeft(tile.StateText, (canvasWidth - textWidth) / 2);
                return;
            }

            // Text is too wide - set up scrolling animation
            Canvas.SetLeft(tile.StateText, 0);

            // Calculate scroll distance and duration
            double scrollDistance = textWidth - canvasWidth + 10; // Extra padding
            double scrollSpeed = 30; // pixels per second
            double scrollDuration = scrollDistance / scrollSpeed;

            var storyboard = new Storyboard();
            var animation = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever
            };

            // Pause at start
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                Value = 0
            });
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5)),
                Value = 0
            });

            // Scroll left
            animation.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5 + scrollDuration)),
                Value = -scrollDistance
            });

            // Pause at end
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3 + scrollDuration)),
                Value = -scrollDistance
            });

            // Scroll back right
            animation.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3 + scrollDuration * 2)),
                Value = 0
            });

            // Pause before repeat
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.5 + scrollDuration * 2)),
                Value = 0
            });

            Storyboard.SetTarget(animation, tile.StateTextTransform);
            Storyboard.SetTargetProperty(animation, "X");
            storyboard.Children.Add(animation);

            tile.ScrollStoryboard = storyboard;
            storyboard.Begin();
        }

        /// <summary>
        /// Update all Quick Settings tile states based on current property values
        /// </summary>
        private void UpdateQuickSettingsTileStates()
        {
            if (!quickSettingsInitialized)
            {
                InitializeQuickSettings();
            }

            try
            {
                // Tile subtitle ("state") text picks up the live Windows accent color
                // when a tile is on - the same brush used for the bottom accent bar
                // (see SetTileAccentBar + tileAccentBrush in GamingWidget.QuickSettings.cs).
                var accentForeground = tileAccentBrush;
                var offForeground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));

                // TDP Mode tile - background/border stay neutral like every other tile;
                // the state text + bottom bar carry a FIXED severity color per mode
                // (not the Win11 accent, not the old background tint) so the meaning
                // stays readable regardless of the user's accent color.
                if (qsTileMap.TryGetValue("TDPMode", out var tdpTile) && tdpTile.TileButton != null)
                {
                    bool isLegion = legionGoDetected?.Value == true;
                    int selectedIndex = TDPModeComboBox?.SelectedIndex ?? 0;
                    string modeText;
                    SolidColorBrush tdpModeColor;

                    // Mode display (Quiet/Balanced/Performance/Custom)
                    {
                        int mode;
                        if (isLegion && legionPerformanceMode != null)
                        {
                            mode = legionPerformanceMode.Value;
                        }
                        else
                        {
                            int[] modeValues = { 1, 2, 3, 255 };
                            mode = (selectedIndex >= 0 && selectedIndex < modeValues.Length) ? modeValues[selectedIndex] : 2;
                        }

                        int[] genericTDPValues = { 8, 15, 25 }; // Quiet, Balanced, Performance TDP values
                        switch (mode)
                        {
                            case 1: // Quiet - green
                                modeText = isLegion ? "Quiet" : $"Quiet ({genericTDPValues[0]}W)";
                                tdpModeColor = tileSeverityGreenBrush;
                                break;
                            case 2: // Balanced - blue
                                modeText = isLegion ? "Balanced" : $"Balanced ({genericTDPValues[1]}W)";
                                tdpModeColor = tileSeverityBlueBrush;
                                break;
                            case 3: // Performance - red
                                modeText = isLegion ? "Performance" : $"Perf ({genericTDPValues[2]}W)";
                                tdpModeColor = tileSeverityRedBrush;
                                break;
                            case 255: // Custom - purple
                                // The tile shows the sustained limit (SPL) = the Custom TDP slider.
                                // (The master TDP slider was removed.)
                                int currentTdp = (int)(CustomTDPSlowSlider?.Value ?? 15);
                                modeText = $"Custom ({currentTdp}W)";
                                tdpModeColor = tileSeverityPurpleBrush;
                                break;
                            default:
                                modeText = isLegion ? "Balanced" : $"Balanced ({genericTDPValues[1]}W)";
                                tdpModeColor = tileSeverityBlueBrush;
                                break;
                        }
                    }

                    tdpTile.StateText.Text = modeText;
                    tdpTile.StateText.Foreground = offForeground;
                    if (tdpTile.AccentBar != null) tdpTile.AccentBar.Background = tdpModeColor;
                    tdpTile.TileButton.Background = tileOffBrush;
                }

                // AutoTDP tile
                if (qsTileMap.TryGetValue("AutoTDP", out var autoTdpTile) && autoTdpTile.TileButton != null)
                {
                    bool autoTdpOn = AutoTDPToggle?.IsOn == true;
                    autoTdpTile.StateText.Text = autoTdpOn ? "On" : "Off";
                    autoTdpTile.StateText.Foreground = autoTdpOn ? accentForeground : offForeground;
                    SetTileAccentBar(autoTdpTile, autoTdpOn);
                    autoTdpTile.TileButton.Background = autoTdpOn ? tileOnBrush : tileOffBrush;
                }

                // Profile tile
                if (qsTileMap.TryGetValue("Profile", out var profileTile) && profileTile.TileButton != null)
                {
                    bool perGame = perGameProfile?.Value ?? false;
                    string gameName = (runningGame != null && runningGame.Value.IsValid()) ? runningGame.Value.GameId.Name : "Per-Game";

                    string profileName = perGame ? gameName : "Global";
                    profileTile.StateText.Text = profileName;
                    profileTile.StateText.Foreground = perGame ? accentForeground : offForeground;
                    SetTileAccentBar(profileTile, perGame);
                    profileTile.TileButton.Background = perGame ? tileOnBrush : tileOffBrush;

                    // Update scroll animation for long profile names (per-tile loop
                    // below also runs this for every tile, but re-running it here
                    // keeps the profile tile responsive to name changes without
                    // waiting for the next tile-state refresh pass).
                    UpdateTileScrollAnimation(profileTile);
                }

                // Performance Overlay tile
                if (qsTileMap.TryGetValue("Overlay", out var overlayTile) && overlayTile.TileButton != null)
                {
                    if (osdProvider == 1) // AMD
                    {
                        string amdLevelText = amdOverlayLevel > 0 ? $"AMD {amdOverlayLevel}" : "Off";
                        overlayTile.StateText.Text = amdLevelText;
                        overlayTile.StateText.Foreground = amdOverlayLevel > 0 ? accentForeground : offForeground;
                        SetTileAccentBar(overlayTile, amdOverlayLevel > 0);
                        overlayTile.TileButton.Background = amdOverlayLevel > 0 ? tileOnBrush : tileOffBrush;
                    }
                    else // RTSS
                    {
                        int level = (int)(osd?.Value ?? 0);
                        bool isOn = level > 0;
                        overlayTile.StateText.Text = isOn ? "On" : "Off";
                        overlayTile.StateText.Foreground = isOn ? accentForeground : offForeground;
                        SetTileAccentBar(overlayTile, isOn);
                        overlayTile.TileButton.Background = isOn ? tileOnBrush : tileOffBrush;
                    }
                }

                // Power Mode tile - background/border stay neutral; state text +
                // bottom bar carry a fixed severity color per mode.
                if (qsTileMap.TryGetValue("PowerMode", out var powerModeTile) && powerModeTile.TileButton != null)
                {
                    int mode = osPowerMode?.Value ?? 1;
                    string modeText;
                    string modeGlyph;
                    SolidColorBrush powerModeColor;
                    switch (mode)
                    {
                        case 0: modeText = "Efficiency"; modeGlyph = ((char)0xEC48).ToString(); powerModeColor = tileSeverityGreenBrush; break;  // SpeedOff
                        case 1: modeText = "Balanced"; modeGlyph = ((char)0xEC49).ToString(); powerModeColor = tileSeverityBlueBrush; break;    // SpeedMedium
                        case 2: modeText = "Performance"; modeGlyph = ((char)0xEC4A).ToString(); powerModeColor = tileSeverityRedBrush; break;  // SpeedHigh
                        default: modeText = "Balanced"; modeGlyph = ((char)0xEC49).ToString(); powerModeColor = tileSeverityBlueBrush; break;   // SpeedMedium
                    }
                    powerModeTile.StateText.Text = modeText;
                    powerModeTile.StateText.Foreground = offForeground;
                    if (powerModeTile.IconElement != null) powerModeTile.IconElement.Glyph = modeGlyph;
                    if (powerModeTile.AccentBar != null) powerModeTile.AccentBar.Background = powerModeColor;
                    powerModeTile.TileButton.Background = tileOffBrush;
                }

                // FPS Limit tile
                if (qsTileMap.TryGetValue("FPSLimit", out var fpsLimitTile) && fpsLimitTile.TileButton != null)
                {
                    int limit = fpsLimit?.Value ?? 0;
                    string limitText = limit == 0 ? "Off" : $"{limit}";
                    fpsLimitTile.StateText.Text = limitText;
                    fpsLimitTile.StateText.Foreground = limit > 0 ? accentForeground : offForeground;
                    SetTileAccentBar(fpsLimitTile, limit > 0);
                    fpsLimitTile.TileButton.Background = limit > 0 ? tileOnBrush : tileOffBrush;
                }

                // Resolution tile - blocked (whole tile dimmed + disabled) when only an external
                // monitor is active, since the resolution override only applies to the built-in panel.
                if (qsTileMap.TryGetValue("Resolution", out var resTile) && resTile.TileButton != null)
                {
                    string currentRes = resolution?.Value ?? "1920x1080";
                    resTile.StateText.Text = _internalPanelActive ? currentRes : "Built-in Only";
                    resTile.StateText.Foreground = _internalPanelActive ? accentForeground : offForeground;
                    SetTileAccentBar(resTile, _internalPanelActive);
                    resTile.TileButton.Background = tileOffBrush;
                    ApplyPanelGateVisual(resTile, _internalPanelActive);
                }

                // Refresh Rate tile - blocked (whole tile dimmed + disabled) when only an external
                // monitor is active, same reasoning as the Resolution tile above.
                if (qsTileMap.TryGetValue("RefreshRate", out var refreshRateTile) && refreshRateTile.TileButton != null)
                {
                    int currentRate = refreshRate?.Value ?? 60;
                    refreshRateTile.StateText.Text = _internalPanelActive ? $"{currentRate} Hz" : "Built-in Only";
                    refreshRateTile.StateText.Foreground = _internalPanelActive ? accentForeground : offForeground;
                    SetTileAccentBar(refreshRateTile, _internalPanelActive);
                    refreshRateTile.TileButton.Background = tileOffBrush;
                    ApplyPanelGateVisual(refreshRateTile, _internalPanelActive);
                }

                // Rotation tile - blocked (whole tile dimmed + disabled) when only an external
                // monitor is active, since display rotation only applies to the built-in panel.
                if (qsTileMap.TryGetValue("Rotation", out var rotationTile) && rotationTile.TileButton != null)
                {
                    if (_internalPanelActive)
                    {
                        string orientationText = displayOrientation?.GetOrientationText() ?? "Landscape";
                        bool isPortrait = (displayOrientation?.Value ?? 0) == 1 || (displayOrientation?.Value ?? 0) == 3;
                        rotationTile.StateText.Text = orientationText;
                        rotationTile.StateText.Foreground = isPortrait ? accentForeground : offForeground;
                        SetTileAccentBar(rotationTile, isPortrait);
                        rotationTile.TileButton.Background = isPortrait ? tileOnBrush : tileOffBrush;
                    }
                    else
                    {
                        rotationTile.StateText.Text = "Built-in Only";
                        rotationTile.StateText.Foreground = offForeground;
                        SetTileAccentBar(rotationTile, false);
                        rotationTile.TileButton.Background = tileOffBrush;
                    }
                    ApplyPanelGateVisual(rotationTile, _internalPanelActive);
                }

                // HDR tile
                if (qsTileMap.TryGetValue("HDR", out var hdrTile) && hdrTile.TileButton != null)
                {
                    bool supported = hdrSupported?.Value ?? false;
                    bool enabled = hdrEnabled?.Value ?? false;
                    hdrTile.StateText.Text = !supported ? "N/A" : (enabled ? "On" : "Off");
                    hdrTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                    SetTileAccentBar(hdrTile, enabled);
                    hdrTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // Lossless Scaling tile
                if (qsTileMap.TryGetValue("LosslessScaling", out var lsTile) && lsTile.TileButton != null)
                {
                    bool enabled = losslessScalingEnabled?.Value ?? false;
                    lsTile.StateText.Text = enabled ? "On" : "Off";
                    lsTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                    SetTileAccentBar(lsTile, enabled);
                    lsTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // RIS (Radeon Image Sharpening) tile - AMD features use a fixed red accent
                // (tileSeverityRedBrush), not the live Windows accent, so they read as a
                // distinct "AMD driver feature" group regardless of the user's chosen accent.
                if (qsTileMap.TryGetValue("RIS", out var risTile) && risTile.TileButton != null)
                {
                    bool supported = amdImageSharpeningSupported?.Value ?? false;
                    bool enabled = amdImageSharpeningEnabled?.Value ?? false;
                    risTile.StateText.Text = !supported ? "N/A" : (enabled ? "On" : "Off");
                    risTile.StateText.Foreground = enabled ? tileSeverityRedBrush : offForeground;
                    if (risTile.AccentBar != null) risTile.AccentBar.Background = enabled ? tileSeverityRedBrush : tileBarOffBrush;
                    risTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // AFMF tile
                if (qsTileMap.TryGetValue("AFMF", out var afmfTile) && afmfTile.TileButton != null)
                {
                    bool supported = amdFluidMotionFrameSupported?.Value ?? false;
                    bool enabled = amdFluidMotionFrameEnabled?.Value ?? false;
                    afmfTile.StateText.Text = !supported ? "N/A" : (enabled ? "On" : "Off");
                    afmfTile.StateText.Foreground = enabled ? tileSeverityRedBrush : offForeground;
                    if (afmfTile.AccentBar != null) afmfTile.AccentBar.Background = enabled ? tileSeverityRedBrush : tileBarOffBrush;
                    afmfTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // RSR tile
                if (qsTileMap.TryGetValue("RSR", out var rsrTile) && rsrTile.TileButton != null)
                {
                    bool supported = amdRadeonSuperResolutionSupported?.Value ?? false;
                    bool enabled = amdRadeonSuperResolutionEnabled?.Value ?? false;
                    rsrTile.StateText.Text = !supported ? "N/A" : (enabled ? "On" : "Off");
                    rsrTile.StateText.Foreground = enabled ? tileSeverityRedBrush : offForeground;
                    if (rsrTile.AccentBar != null) rsrTile.AccentBar.Background = enabled ? tileSeverityRedBrush : tileBarOffBrush;
                    rsrTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // Anti-Lag tile
                if (qsTileMap.TryGetValue("AntiLag", out var antiLagTile) && antiLagTile.TileButton != null)
                {
                    bool supported = amdRadeonAntiLagSupported?.Value ?? false;
                    bool enabled = amdRadeonAntiLagEnabled?.Value ?? false;
                    antiLagTile.StateText.Text = !supported ? "N/A" : (enabled ? "On" : "Off");
                    antiLagTile.StateText.Foreground = enabled ? tileSeverityRedBrush : offForeground;
                    if (antiLagTile.AccentBar != null) antiLagTile.AccentBar.Background = enabled ? tileSeverityRedBrush : tileBarOffBrush;
                    antiLagTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // Radeon Chill tile
                if (qsTileMap.TryGetValue("RadeonChill", out var chillTile) && chillTile.TileButton != null)
                {
                    bool supported = amdRadeonChillSupported?.Value ?? false;
                    bool enabled = amdRadeonChillEnabled?.Value ?? false;
                    chillTile.StateText.Text = !supported ? "N/A" : (enabled ? "On" : "Off");
                    chillTile.StateText.Foreground = enabled ? tileSeverityRedBrush : offForeground;
                    if (chillTile.AccentBar != null) chillTile.AccentBar.Background = enabled ? tileSeverityRedBrush : tileBarOffBrush;
                    chillTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // CPU Boost tile
                if (qsTileMap.TryGetValue("CPUBoost", out var boostTile) && boostTile.TileButton != null)
                {
                    bool enabled = cpuBoost?.Value ?? false;
                    boostTile.StateText.Text = enabled ? "On" : "Off";
                    boostTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                    SetTileAccentBar(boostTile, enabled);
                    boostTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // EPP tile - background/border stay neutral; state text + bottom bar
                // carry a fixed severity color banded around the 0/30/80/100 anchors:
                // 0 = gray (matches "off"), ~1-55 = green, ~56-90 = blue, ~91-100 = red.
                if (qsTileMap.TryGetValue("EPP", out var eppTile) && eppTile.TileButton != null)
                {
                    int eppValue = (int)(cpuEPP?.Value ?? 0);
                    eppTile.StateText.Text = $"{eppValue}%";
                    SolidColorBrush eppColor;
                    if (eppValue <= 0) eppColor = offForeground;
                    else if (eppValue <= 55) eppColor = tileSeverityGreenBrush;
                    else if (eppValue <= 90) eppColor = tileSeverityBlueBrush;
                    else eppColor = tileSeverityRedBrush;
                    eppTile.StateText.Foreground = offForeground;
                    if (eppTile.AccentBar != null) eppTile.AccentBar.Background = eppColor;
                    eppTile.TileButton.Background = tileOffBrush;
                }

                // Keyboard trigger tile - action-colored, not the Win11 accent (it
                // always fires an action, it doesn't toggle). Background/border stay
                // the same neutral as every other tile.
                if (qsTileMap.TryGetValue("Keyboard", out var keyboardTile) && keyboardTile.TileButton != null)
                {
                    keyboardTile.StateText.Text = "Open";
                    keyboardTile.StateText.Foreground = tileActionBrush;
                    if (keyboardTile.AccentBar != null) keyboardTile.AccentBar.Background = tileActionBrush;
                    keyboardTile.TileButton.Background = tileOffBrush;
                }

                // Custom shortcut tiles - same action-colored treatment as Keyboard.
                foreach (var shortcutTile in qsCustomShortcuts)
                {
                    if (shortcutTile.TileButton != null && shortcutTile.StateText != null)
                    {
                        shortcutTile.StateText.Text = shortcutTile.CustomShortcut ?? "Run";
                        shortcutTile.StateText.Foreground = tileActionBrush;
                        if (shortcutTile.AccentBar != null) shortcutTile.AccentBar.Background = tileActionBrush;
                        shortcutTile.TileButton.Background = tileOffBrush;
                    }
                }

                // Legion Touchpad tile
                if (qsTileMap.TryGetValue("LegionTouchpad", out var touchpadTile) && touchpadTile.TileButton != null)
                {
                    if (legionGoDetected?.Value == true)
                    {
                        bool enabled = legionTouchpadEnabled?.Value ?? false;
                        touchpadTile.StateText.Text = enabled ? "On" : "Off";
                        touchpadTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                        SetTileAccentBar(touchpadTile, enabled);
                        touchpadTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                    }
                }

                // Touchscreen tile
                if (qsTileMap.TryGetValue("Touchscreen", out var touchscreenTile) && touchscreenTile.TileButton != null)
                {
                    if (legionGoDetected?.Value == true)
                    {
                        bool enabled = touchscreenEnabled?.Value ?? true;
                        touchscreenTile.StateText.Text = enabled ? "On" : "Off";
                        touchscreenTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                        SetTileAccentBar(touchscreenTile, enabled);
                        touchscreenTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                    }
                }

                // Legion Light Mode tile
                if (qsTileMap.TryGetValue("LegionLightMode", out var lightTile) && lightTile.TileButton != null)
                {
                    if (legionGoDetected?.Value == true)
                    {
                        int mode = legionLightMode?.Value ?? 0;
                        string modeText;
                        switch (mode)
                        {
                            case 0: modeText = "Off"; break;
                            case 1: modeText = "Static"; break;
                            case 2: modeText = "Breathing"; break;
                            case 3: modeText = "Rainbow"; break;
                            case 4: modeText = "Spiral"; break;
                            default: modeText = "Off"; break;
                        }
                        lightTile.StateText.Text = modeText;
                        lightTile.StateText.Foreground = mode > 0 ? accentForeground : offForeground;
                        SetTileAccentBar(lightTile, mode > 0);
                        lightTile.TileButton.Background = mode > 0 ? tileOnBrush : tileOffBrush;
                    }
                }

                // Legion Vibration intensity tile
                if (qsTileMap.TryGetValue("LegionVibration", out var vibrationTile) && vibrationTile.TileButton != null)
                {
                    if (legionGoDetected?.Value == true)
                    {
                        int level = legionVibration?.Value ?? 0;
                        string levelText;
                        switch (level)
                        {
                            case 0: levelText = "Off"; break;
                            case 1: levelText = "Weak"; break;
                            case 2: levelText = "Medium"; break;
                            case 3: levelText = "Strong"; break;
                            default: levelText = "Off"; break;
                        }
                        vibrationTile.StateText.Text = levelText;
                        vibrationTile.StateText.Foreground = level > 0 ? accentForeground : offForeground;
                        SetTileAccentBar(vibrationTile, level > 0);
                        vibrationTile.TileButton.Background = level > 0 ? tileOnBrush : tileOffBrush;
                    }
                }

                // Legion Vibration Mode tile
                if (qsTileMap.TryGetValue("LegionVibrationMode", out var vibrationModeTile) && vibrationModeTile.TileButton != null)
                {
                    if (legionGoDetected?.Value == true)
                    {
                        // legionVibrationMode.Value is 1-based (FPS=1, Racing=2, AVG=3, SPG=4,
                        // RPG=5 - see LegionVibrationModeProperty.cs / VibrationMode enum in
                        // LegionGoController.cs), NOT the 0-based index this switch used to
                        // assume - every label was off by one (e.g. the tile showed "Racing"
                        // while the controller was actually in FPS mode).
                        int vibMode = legionVibrationMode?.Value ?? 1;
                        string vibModeText;
                        switch (vibMode)
                        {
                            case 1: vibModeText = "FPS"; break;
                            case 2: vibModeText = "Racing"; break;
                            case 3: vibModeText = "AVG"; break;
                            case 4: vibModeText = "SPG"; break;
                            case 5: vibModeText = "RPG"; break;
                            default: vibModeText = "FPS"; break;
                        }
                        vibrationModeTile.StateText.Text = vibModeText;
                        vibrationModeTile.StateText.Foreground = accentForeground;
                        SetTileAccentBar(vibrationModeTile, true);
                        vibrationModeTile.TileButton.Background = tileOnBrush;
                    }
                }

                // Legion Desktop Controls tile
                if (qsTileMap.TryGetValue("LegionDesktopControls", out var desktopTile) && desktopTile.TileButton != null)
                {
                    if (legionGoDetected?.Value == true)
                    {
                        bool enabled = LegionDesktopControlsToggle?.IsOn ?? false;
                        desktopTile.StateText.Text = enabled ? "On" : "Off";
                        desktopTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                        SetTileAccentBar(desktopTile, enabled);
                        desktopTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                    }
                }

                // Legion Remap Controls tile
                if (qsTileMap.TryGetValue("LegionRemapControls", out var remapTile) && remapTile.TileButton != null)
                {
                    if (legionGoDetected?.Value == true)
                    {
                        bool isGameProfile = LegionControllerProfileToggle?.IsOn == true && HasValidGame(currentGameName);
                        string profileName = isGameProfile ? currentGameName : "Global";
                        // Truncate long names
                        if (profileName.Length > 10)
                            profileName = profileName.Substring(0, 9) + "…";
                        remapTile.StateText.Text = profileName;
                        remapTile.StateText.Foreground = isGameProfile ? accentForeground : offForeground;
                        SetTileAccentBar(remapTile, isGameProfile);
                        remapTile.TileButton.Background = isGameProfile ? tileOnBrush : tileOffBrush;
                    }
                }

                // Legion Charge Limit tile (80% battery limit)
                if (qsTileMap.TryGetValue("LegionChargeLimit", out var chargeLimitTile) && chargeLimitTile.TileButton != null)
                {
                    if (legionGoDetected?.Value == true)
                    {
                        bool enabled = legionChargeLimit?.Value ?? false;
                        chargeLimitTile.StateText.Text = enabled ? "80%" : "Off";
                        chargeLimitTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                        SetTileAccentBar(chargeLimitTile, enabled);
                        chargeLimitTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                    }
                }

                // Legion Power Light tile
                if (qsTileMap.TryGetValue("LegionPowerLight", out var powerLightTile) && powerLightTile.TileButton != null)
                {
                    if (legionGoDetected?.Value == true)
                    {
                        bool enabled = legionPowerLight?.Value ?? false;
                        powerLightTile.StateText.Text = enabled ? "On" : "Off";
                        powerLightTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                        SetTileAccentBar(powerLightTile, enabled);
                        powerLightTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                    }
                }

                // Controller Emulation tile — label is the active VIIPER virtual-device
                // tag (Xbox / DS4 / DS Edge / Elite 2 / Steam / Switch). VIIPER is the
                // only emulation backend now (CLAUDE.md SS21).
                if (qsTileMap.TryGetValue("ControllerEmulation", out var ceTile) && ceTile.TileButton != null)
                {
                    bool available = controllerEmulationAvailable?.Value == true;
                    bool enabled = available && (controllerEmulationEnabled?.Value == true);
                    string label;
                    if (!available)
                    {
                        label = "N/A";
                    }
                    else if (!enabled)
                    {
                        label = "Off";
                    }
                    else
                    {
                        string device = viiperDeviceType?.Value ?? "";
                        switch (device)
                        {
                            case "xbox360": label = "Xbox"; break;
                            case "dualshock4": label = "DS4"; break;
                            case "dualsenseedge": label = "DS Edge"; break;
                            // xboxelite2 removed from UI but kept here so legacy
                            // persisted settings still render a sane label until
                            // the helper coerces them forward on next launch.
                            case "xboxelite2": label = "Xbox"; break;
                            case "steam-generic": label = "Steam"; break;
                            case "sony": label = "Sony"; break;
                            case "nintendo": label = "Switch"; break;
                            // Helper resolves nintendo+sub → joycon-left/right/pair;
                            // keep direct labels for those when they arrive.
                            case "switchpro": label = "Switch"; break;
                            case "joycon-left": label = "JoyL"; break;
                            case "joycon-right": label = "JoyR"; break;
                            case "joycon-pair": label = "JoyPair"; break;
                            default: label = "On"; break;
                        }
                    }
                    // StateText can be null if the tile was rebuilt mid-update during a
                    // foreground-window-change cascade — null-check before assigning.
                    if (ceTile.StateText != null)
                    {
                        ceTile.StateText.Text = label;
                        ceTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                        SetTileAccentBar(ceTile, enabled);
                    }
                    ceTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // Fan Full Speed tile (Legion or GPD)
                if (qsTileMap.TryGetValue("LegionFanFullSpeed", out var fanFullSpeedTile) && fanFullSpeedTile.TileButton != null)
                {
                    bool enabled = false;
                    if (legionGoDetected?.Value == true)
                    {
                        enabled = legionFanFullSpeed?.Value ?? false;
                    }
                    else if (gpdDetected?.Value == true)
                    {
                        enabled = gpdFanMaxActive;
                    }
                    fanFullSpeedTile.StateText.Text = enabled ? "On" : "Off";
                    fanFullSpeedTile.StateText.Foreground = enabled ? accentForeground : offForeground;
                    SetTileAccentBar(fanFullSpeedTile, enabled);
                    fanFullSpeedTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                }

                // Screen Saver tile
                if (qsTileMap.TryGetValue("ScreenSaver", out var screenSaverTile) && screenSaverTile.TileButton != null)
                {
                    bool enabled = screenSaverEnabled;
                    if (enabled)
                    {
                        // Don't overwrite countdown text — let the timer handle it
                        screenSaverTile.StateText.Foreground = accentForeground;
                    }
                    else
                    {
                        screenSaverTile.StateText.Text = "Off";
                        screenSaverTile.StateText.Foreground = offForeground;
                    }
                    screenSaverTile.TileButton.Background = enabled ? tileOnBrush : tileOffBrush;
                    SetTileAccentBar(screenSaverTile, enabled);
                }

                // Battery tile - device battery in title, controllers in state text
                if (qsTileMap.TryGetValue("Battery", out var batteryTile) && batteryTile.TileButton != null)
                {
                    // Get device battery info (hide bolt at 100%)
                    int deviceBat = PowerManager.RemainingChargePercent;
                    // On AC = not NotPresent, matching the isOnAC check used elsewhere (ProfileSwitching.cs,
                    // PowerSourceEvents.cs) - NOT == Adequate, which can flip to Inadequate once the battery
                    // hits 100% (the charger stops actively drawing current), wrongly showing the non-charging icon.
                    bool deviceCharging = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
                    string deviceIndicator = (deviceCharging && deviceBat < 100) ? "⚡" : "";

                    // Get the tile content elements
                    var content = batteryTile.TileButton.Content as StackPanel;
                    var iconElement = content?.Children.Count >= 1 ? content.Children[0] as FontIcon : null;
                    var labelText = content?.Children.Count >= 2 ? content.Children[1] as TextBlock : null;

                    // Update battery icon based on level and charging state
                    // Battery icons: \uE850-\uE859 (0-9), \uE83F (full)
                    // Charging icons: \uE85A-\uE862 (0-8), \uEA93 (charging full = BatteryCharging10)
                    if (iconElement != null)
                    {
                        string glyph;
                        if (deviceCharging)
                        {
                            // Charging icons
                            if (deviceBat >= 90) glyph = ((char)0xEA93).ToString(); // Full charging (BatteryCharging10)
                            else if (deviceBat >= 70) glyph = "\uE862"; // Charging 8
                            else if (deviceBat >= 50) glyph = "\uE85F"; // Charging 5
                            else if (deviceBat >= 30) glyph = "\uE85C"; // Charging 2
                            else glyph = "\uE85A";                       // Charging 0
                        }
                        else
                        {
                            // Normal battery icons
                            if (deviceBat >= 90) glyph = "\uE83F";      // Full
                            else if (deviceBat >= 70) glyph = "\uE858"; // Battery 8
                            else if (deviceBat >= 50) glyph = "\uE855"; // Battery 5
                            else if (deviceBat >= 30) glyph = "\uE852"; // Battery 2
                            else glyph = "\uE850";                       // Battery 0 (low)
                        }
                        iconElement.Glyph = glyph;
                    }

                    string stateText;

                    // Update title with device battery
                    if (labelText != null)
                    {
                        labelText.Text = $"{deviceBat}%{deviceIndicator}";
                    }

                    if (legionGoDetected?.Value == true)
                    {
                        int leftBat = controllerBatteryLeft?.Value ?? -1;
                        int rightBat = controllerBatteryRight?.Value ?? -1;
                        bool leftCharging = controllerChargingLeft?.Value ?? false;
                        bool rightCharging = controllerChargingRight?.Value ?? false;

                        if (leftBat > 0 && rightBat > 0)
                        {
                            // Controllers connected - show L/R with % (hide bolt at 100%)
                            string leftIndicator = (leftCharging && leftBat < 100) ? "⚡" : "";
                            string rightIndicator = (rightCharging && rightBat < 100) ? "⚡" : "";
                            stateText = $"L:{leftBat}%{leftIndicator} R:{rightBat}%{rightIndicator}";
                        }
                        else
                        {
                            // Controllers not connected
                            stateText = "No Ctrl";
                        }
                    }
                    else
                    {
                        // Not Legion Go - just show "Device" in state
                        stateText = "Device";
                    }

                    // Background/border stay the same neutral as every other tile -
                    // subtitle + bottom bar color reflects charge level (green >50%,
                    // orange <=50%, red <=20%), except while charging, which is always
                    // green regardless of level.
                    SolidColorBrush batteryColor;
                    if (deviceCharging) batteryColor = tileSeverityGreenBrush;
                    else if (deviceBat <= 20) batteryColor = tileSeverityRedBrush;
                    else if (deviceBat <= 50) batteryColor = tileSeverityOrangeBrush;
                    else batteryColor = tileSeverityGreenBrush;

                    batteryTile.StateText.Text = stateText;
                    batteryTile.StateText.Foreground = batteryColor;
                    if (batteryTile.AccentBar != null) batteryTile.AccentBar.Background = batteryColor;
                    batteryTile.TileButton.Background = tileOffBrush;
                }

                // Re-evaluate scrolling for every tile whose state text may have
                // changed above. Text that fits centers; text that overflows the
                // tile's column starts the marquee loop.
                foreach (var t in qsTileDefinitions)
                {
                    if (t?.StateText != null && t.StateTextCanvas != null)
                    {
                        UpdateTileScrollAnimation(t);
                    }
                }

                Logger.Debug("Quick Settings tile states updated");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error updating Quick Settings tile states: {ex.Message}");
            }
        }

    }
}
