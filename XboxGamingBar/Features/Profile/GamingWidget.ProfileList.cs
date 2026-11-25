using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace XboxGamingBar
{
    // Profiles tab. Since 1.9.7a the helper owns every profile (Global and per game): it applies
    // them on game start / exit and records each setting change in the active one. The widget
    // keeps no copy; this tab shows what the helper sends for the ProfileList request.
    public sealed partial class GamingWidget
    {
        private sealed class HelperProfileEntry
        {
            public string Name;
            public string ExePath;
            public bool IsGlobal;
            public bool Use;
            public PerformanceProfile Values;
        }

        private List<HelperProfileEntry> helperProfiles = new List<HelperProfileEntry>();
        private bool profileListRefreshQueued;

        /// <summary>
        /// Refreshes the Profiles tab from the helper. Calls within a short window are coalesced
        /// into one request (game changes and toggles often arrive in bursts).
        /// </summary>
        private async void UpdateProfileDisplay()
        {
            if (profileListRefreshQueued) return;
            profileListRefreshQueued = true;
            try
            {
                await Task.Delay(200);
                if (App.IsConnected)
                {
                    string json = await RequestStringAsync(Shared.Enums.Function.ProfileList);
                    if (!string.IsNullOrEmpty(json)) ParseProfileList(json);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Profile list refresh failed: {ex.Message}");
            }
            finally
            {
                profileListRefreshQueued = false;
            }

            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try { RenderProfilesTab(); }
                catch (Exception ex) { Logger.Warn($"Profiles tab render failed: {ex.Message}"); }
            });
        }

        private void ParseProfileList(string json)
        {
            var root = JsonObject.Parse(json);
            var list = new List<HelperProfileEntry>();
            foreach (var item in root.GetNamedArray("profiles", new JsonArray()))
            {
                if (item.ValueType != JsonValueType.Object) continue;
                var o = item.GetObject();
                int fps = (int)o.GetNamedNumber("fps", 0);
                int osPower = (int)o.GetNamedNumber("osPower", -1);
                int overlay = (int)o.GetNamedNumber("overlay", -1);
                list.Add(new HelperProfileEntry
                {
                    Name = o.GetNamedString("name", ""),
                    ExePath = o.GetNamedString("path", ""),
                    IsGlobal = o.GetNamedBoolean("global", false),
                    Use = o.GetNamedBoolean("use", false),
                    Values = new PerformanceProfile
                    {
                        LegionPerformanceMode = (int)o.GetNamedNumber("mode", 0),
                        TDP = o.GetNamedNumber("tdp", 15),
                        TDPFast = o.GetNamedNumber("tdpFast", 15),
                        TDPPeak = o.GetNamedNumber("tdpPeak", 15),
                        AutoTDPEnabled = o.GetNamedBoolean("autoTdp", false),
                        AutoTDPTargetFPS = (int)o.GetNamedNumber("autoTdpTarget", 60),
                        AutoTDPMinTDP = (int)o.GetNamedNumber("autoTdpMin", 8),
                        AutoTDPMaxTDP = (int)o.GetNamedNumber("autoTdpMax", 30),
                        CPUBoost = o.GetNamedBoolean("boost", false),
                        CPUEPP = o.GetNamedNumber("epp", 0),
                        MinCPUState = (int)o.GetNamedNumber("minState", 5),
                        MaxCPUState = (int)o.GetNamedNumber("maxState", 100),
                        FPSLimitEnabled = fps > 0,
                        FPSLimitValue = fps,
                        OSPowerMode = osPower,
                        OverlayLevel = overlay,
                    },
                });
            }
            helperProfiles = list;
        }

        private HelperProfileEntry FindHelperProfile(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;
            return helperProfiles.FirstOrDefault(p => !p.IsGlobal
                && string.Equals(p.Name?.Trim(), gameName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private void RenderProfilesTab()
        {
            if (GlobalProfileTDPModeRow == null) return; // XAML not ready yet

            var global = helperProfiles.FirstOrDefault(p => p.IsGlobal)?.Values ?? new PerformanceProfile();
            bool isLegion = legionGoDetected?.Value == true;

            GlobalProfileTDPModeRow.Visibility = isLegion ? Visibility.Visible : Visibility.Collapsed;
            GlobalProfileTDPModeText.Text = GetProfileTDPModeName(global);
            GlobalProfileTDPText.Text = GetProfileTDPText(global);
            GlobalProfileCPUBoostText.Text = global.CPUBoost ? "On" : "Off";
            GlobalProfileCPUEPPText.Text = $"{global.CPUEPP}";
            GlobalProfileCPUStateText.Text = $"{global.MinCPUState}-{global.MaxCPUState}%";
            GlobalProfileFPSLimitText.Text = GetFpsLimitText(global);
            GlobalProfilePowerModeText.Text = GetPowerModeShortName(global.OSPowerMode);
            GlobalProfileOverlayLevelText.Text = GetOverlayLevelShortName(global.OverlayLevel);

            // Active game card: only while that game's profile is actually in use.
            bool perGameInUse = HasValidGame(currentGameName) && (PerGameProfileToggle?.IsOn ?? false);
            var active = perGameInUse ? FindHelperProfile(currentGameName) : null;
            if (active != null)
            {
                var g = active.Values;
                GameProfileCard.Visibility = Visibility.Visible;
                GameProfileTitleNoPower.Text = currentGameName;
                GameProfileTDPModeRow.Visibility = isLegion ? Visibility.Visible : Visibility.Collapsed;
                GameProfileTDPModeText.Text = GetProfileTDPModeName(g);
                GameProfileTDPText.Text = GetProfileTDPText(g);
                GameProfileCPUBoostText.Text = g.CPUBoost ? "On" : "Off";
                GameProfileCPUEPPText.Text = $"{g.CPUEPP}";
                GameProfileCPUStateText.Text = $"{g.MinCPUState}-{g.MaxCPUState}%";
                GameProfileFPSLimitText.Text = GetFpsLimitText(g);
                GameProfilePowerModeText.Text = GetPowerModeShortName(g.OSPowerMode);
                GameProfileOverlayLevelText.Text = GetOverlayLevelShortName(g.OverlayLevel);
            }
            else
            {
                GameProfileCard.Visibility = Visibility.Collapsed;
            }

            RenderSavedGameProfiles(active);
        }

        // ---- Saved game profiles list -----------------------------------------------------------

        private bool _profileSortModeRestored;

        private void RenderSavedGameProfiles(HelperProfileEntry active)
        {
            if (AllGameProfilesContainer == null) return;

            // Restore the saved sort choice on first render (XAML default is "name").
            if (!_profileSortModeRestored && ProfileSortComboBox != null)
            {
                _profileSortModeRestored = true;
                try
                {
                    if (ApplicationData.Current.LocalSettings.Values.TryGetValue("ProfileSortMode", out var modeObj) && modeObj is string saved)
                    {
                        foreach (var item in ProfileSortComboBox.Items)
                        {
                            if (item is ComboBoxItem cbi && (cbi.Tag as string) == saved && ProfileSortComboBox.SelectedItem != cbi)
                            {
                                ProfileSortComboBox.SelectedItem = cbi; // SelectionChanged re-renders
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex) { Logger.Debug($"Restoring ProfileSortMode failed: {ex.Message}"); }
            }

            AllGameProfilesContainer.Children.Clear();

            var games = helperProfiles.Where(p => !p.IsGlobal && p != active).ToList();
            if (games.Count == 0)
            {
                AllGameProfilesContainer.Children.Add(new TextBlock
                {
                    Text = "No saved game profiles yet. Turn on Per-Game Profile while a game is running to create one.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 160, 160, 160)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                return;
            }

            // Profiles for the same executable (e.g. several titles run through one emulator)
            // share one expandable group.
            var groups = games
                .GroupBy(p => string.IsNullOrEmpty(p.ExePath) ? p.Name : Path.GetFileNameWithoutExtension(p.ExePath),
                         StringComparer.OrdinalIgnoreCase)
                .ToList();

            string sortMode = (ProfileSortComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "name";
            IEnumerable<IGrouping<string, HelperProfileEntry>> ordered = sortMode == "tdp"
                ? groups.OrderByDescending(g => g.Max(p => GetProfileTopTdp(p.Values)))
                : groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in ordered)
            {
                AllGameProfilesContainer.Children.Add(RenderProfileGroupExpander(group.Key, group.ToList()));
            }
        }

        private void ProfileSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var tag = (ProfileSortComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;
                if (!string.IsNullOrEmpty(tag)) ApplicationData.Current.LocalSettings.Values["ProfileSortMode"] = tag;
                RenderSavedGameProfiles(HasValidGame(currentGameName) && (PerGameProfileToggle?.IsOn ?? false) ? FindHelperProfile(currentGameName) : null);
            }
            catch (Exception ex)
            {
                Logger.Debug($"ProfileSortComboBox_SelectionChanged failed: {ex.Message}");
            }
        }

        /// <summary>Wattage used by the "TDP (high → low)" sort: what the TDP line shows.</summary>
        private int GetProfileTopTdp(PerformanceProfile p)
        {
            if (p.AutoTDPEnabled == true) return p.AutoTDPMaxTDP;
            if (legionGoDetected?.Value == true)
            {
                int modeIndex = Array.IndexOf(DefaultLegionModes, p.LegionPerformanceMode);
                if (modeIndex >= 0 && modeIndex < DefaultModeTdpValues.Length) return DefaultModeTdpValues[modeIndex];
            }
            return (int)p.TDP;
        }

        private Border RenderProfileGroupExpander(string groupName, List<HelperProfileEntry> entries)
        {
            var inner = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var entry in entries.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                inner.Children.Add(RenderProfileCard(entry));
            }

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var icon = CreateGameIcon(entries.Select(p => p.ExePath).FirstOrDefault(p => !string.IsNullOrEmpty(p)), 0);
            if (icon != null) header.Children.Add(icon);
            header.Children.Add(new TextBlock
            {
                Text = groupName,
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 200, 100)),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 80)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 1, 8, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = entries.Count == 1 ? "1 profile" : $"{entries.Count} profiles",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White)
                }
            });

            return new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Child = new Microsoft.UI.Xaml.Controls.Expander
                {
                    Header = header,
                    Content = inner,
                    IsExpanded = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                }
            };
        }

        private Border RenderProfileCard(HelperProfileEntry entry)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 42, 26)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 58, 58)),
                BorderThickness = new Thickness(1)
            };
            var stack = new StackPanel();
            card.Child = stack;

            // Title row: [icon] [name + "not in use"] [delete]
            var titleGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = CreateGameIcon(entry.ExePath, 8);
            if (icon != null)
            {
                Grid.SetColumn(icon, 0);
                titleGrid.Children.Add(icon);
            }

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontSize = 13,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 165, 0)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!entry.Use)
            {
                titleStack.Children.Add(new TextBlock
                {
                    Text = "Turned off (uses Global)",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150))
                });
            }
            Grid.SetColumn(titleStack, 1);
            titleGrid.Children.Add(titleStack);

            var deleteButton = new Button
            {
                Content = "🗑️",
                FontSize = 12,
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 0, 0)),
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = entry.Name,
                BorderBrush = new SolidColorBrush(Windows.UI.Colors.Transparent),
                BorderThickness = new Thickness(2)
            };
            deleteButton.Click += DeleteProfileButton_Click;
            deleteButton.GotFocus += (s, args) =>
            {
                deleteButton.BorderBrush = new SolidColorBrush(Windows.UI.Colors.White);
                deleteButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 50, 50));
            };
            deleteButton.LostFocus += (s, args) =>
            {
                deleteButton.BorderBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);
                deleteButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 0, 0));
            };
            Grid.SetColumn(deleteButton, 2);
            titleGrid.Children.Add(deleteButton);
            stack.Children.Add(titleGrid);

            // Settings grid
            var p = entry.Values;
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var rows = new List<(string Label, string Value)>();
            if (legionGoDetected?.Value == true) rows.Add(("TDP Mode", GetProfileTDPModeName(p)));
            rows.Add(("TDP", GetProfileTDPText(p)));
            rows.Add(("CPU Boost", p.CPUBoost ? "On" : "Off"));
            rows.Add(("CPU EPP", $"{p.CPUEPP}"));
            rows.Add(("CPU State", $"{p.MinCPUState}-{p.MaxCPUState}%"));
            rows.Add(("FPS Limit", GetFpsLimitText(p)));
            rows.Add(("Power Mode", GetPowerModeShortName(p.OSPowerMode)));
            rows.Add(("Overlay", GetOverlayLevelShortName(p.OverlayLevel)));
            for (int i = 0; i < rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var margin = new Thickness(0, i == 0 ? 0 : 3, 0, 0);
                AddTextBlock(grid, i, 0, rows[i].Label, 10, "#AAAAAA", margin: margin);
                AddTextBlock(grid, i, 1, rows[i].Value, 10, "#FFFFFF", margin: margin);
            }
            stack.Children.Add(grid);

            return card;
        }

        /// <summary>The game's icon cached by the helper (LocalCache/icons), or null.</summary>
        private Image CreateGameIcon(string exePath, double rightMargin)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            string iconPath = TryResolveCachedIconPath(exePath);
            if (string.IsNullOrEmpty(iconPath)) return null;
            return new Image
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, rightMargin, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = new BitmapImage(new Uri(iconPath))
            };
        }

        /// <summary>
        /// Path of the icon the helper's GameIconHelper cached for an exe
        /// (LocalCache/icons/&lt;exe&gt;_&lt;md5 of path&gt;.png), or null.
        /// </summary>
        private string TryResolveCachedIconPath(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            try
            {
                string iconsFolder = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "icons");
                if (!Directory.Exists(iconsFolder)) return null;

                string fileName;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(exePath.ToLowerInvariant()));
                    var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    var exeName = Path.GetFileNameWithoutExtension(exePath);
                    foreach (var c in Path.GetInvalidFileNameChars()) exeName = exeName.Replace(c, '_');
                    if (exeName.Length > 32) exeName = exeName.Substring(0, 32);
                    fileName = $"{exeName}_{hashString.Substring(0, 8)}.png";
                }
                string fullPath = Path.Combine(iconsFolder, fileName);
                return File.Exists(fullPath) ? fullPath : null;
            }
            catch (Exception ex)
            {
                Logger.Debug($"TryResolveCachedIconPath({exePath}) failed: {ex.Message}");
                return null;
            }
        }

        // ---- Deleting a game profile ---------------------------------------------------------

        private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string gameName)
            {
                Logger.Info($"Delete button clicked for game: {gameName}");
                DeleteGameProfile(gameName);
            }
        }

        /// <summary>Bin button on the active game's card.</summary>
        private void DeleteActiveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasValidGame(currentGameName))
            {
                Logger.Info("DeleteActiveProfileButton_Click ignored - no valid current game");
                return;
            }
            Logger.Info($"Delete active profile button clicked for: {currentGameName}");
            DeleteGameProfile(currentGameName);
        }

        /// <summary>
        /// Deletes a game's profile in the helper (profiles/&lt;exe&gt;.xml) and the game's
        /// widget-side controller profile, so the game starts from Global next time.
        /// </summary>
        private void DeleteGameProfile(string gameName)
        {
            var settings = ApplicationData.Current.LocalSettings;
            string controllerKey = $"ControllerProfile_Game_{gameName}";
            if (settings.Containers.ContainsKey(controllerKey))
            {
                settings.DeleteContainer(controllerKey);
                Logger.Info($"Deleted controller profile for {gameName}");
            }
            settings.Values.Remove($"ControllerProfileDisabled_{gameName}");

            // Turn the per-game profile off first when it's the running game's, so the helper
            // goes back to Global before the profile disappears.
            if (string.Equals(gameName, currentGameName, StringComparison.OrdinalIgnoreCase) && PerGameProfileToggle?.IsOn == true)
            {
                PerGameProfileToggle.IsOn = false;
            }

            // ForceSetValue so deleting the same name twice still sends.
            deleteGameProfile?.ForceSetValue(gameName);
            Logger.Info($"Sent DeleteGameProfile to helper for {gameName}");
            UpdateProfileDisplay();
        }

        // ---- Display helpers -----------------------------------------------------------------

        private static string GetFpsLimitText(PerformanceProfile p) => p.FPSLimitEnabled && p.FPSLimitValue > 0 ? $"{p.FPSLimitValue}" : "Off";

        private static string GetPowerModeShortName(int mode)
        {
            switch (mode)
            {
                case 0: return "Efficiency";
                case 1: return "Balanced";
                case 2: return "Performance";
                default: return "Unchanged";
            }
        }

        // -1 = the profile never recorded the overlay (it isn't changed when the profile applies).
        private static string GetOverlayLevelShortName(int level) => level < 0 ? "Unchanged" : level > 0 ? "On" : "Off";

        private static string GetLegionModeShortName(int mode)
        {
            switch (mode)
            {
                case 1: return "Quiet";
                case 2: return "Balanced";
                case 3: return "Performance";
                case 255: return "Custom";
                default: return "Balanced";
            }
        }

        private string GetProfileTDPModeName(PerformanceProfile profile) => GetLegionModeShortName(profile.LegionPerformanceMode);

        /// <summary>
        /// The "TDP" line. Quiet / Balanced / Performance are firmware modes with their own
        /// wattage (the stored TDP is the last Custom SPL), so show the mode's wattage there, the
        /// Custom limits in Custom, and AutoTDP's target while it picks the wattage itself.
        /// </summary>
        private string GetProfileTDPText(PerformanceProfile profile)
        {
            if (profile.AutoTDPEnabled == true) return $"Auto {profile.AutoTDPTargetFPS} FPS";
            if (legionGoDetected?.Value == true)
            {
                int modeIndex = Array.IndexOf(DefaultLegionModes, profile.LegionPerformanceMode);
                if (modeIndex >= 0 && modeIndex < DefaultModeTdpValues.Length) return $"{DefaultModeTdpValues[modeIndex]}W";
                if (profile.LegionPerformanceMode == 255) return $"{profile.TDP}/{profile.TDPFast}/{profile.TDPPeak}W";
            }
            return $"{profile.TDP}W";
        }
    }
}
