using System;
using Windows.Data.Json;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace XboxGamingBar
{
    // Before 1.9.7a the widget kept its own copy of every profile in LocalSettings
    // (Profile_Global / Profile_AC / Profile_DC, Profile_Game_<name>[_AC|_DC]). The helper's
    // copy is the only one now, but a few settings only ever lived in the widget's copy: the FPS
    // limit, the Windows power mode, the overlay and the Custom SPL / SPPT / FPPT. Hand those to
    // the helper once, the first time this version connects. The old containers are left in
    // place (nothing reads them any more), so going back to an older build still finds them.
    public sealed partial class GamingWidget
    {
        private const string ProfilesMigratedKey = "ProfilesMigratedToHelper_197a";

        private void MigrateWidgetProfilesToHelperOnce()
        {
            try
            {
                if (!App.IsConnected) return;
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.ContainsKey(ProfilesMigratedKey)) return;

                var entries = new JsonArray();

                // Global: AC/DC profiles are gone; the AC copy (or the single Global one) stays.
                bool globalSplit = settings.Values.TryGetValue("PowerSourceProfileEnabled", out var gs) && gs is bool gsb && gsb;
                AddMigrationEntry(entries, settings, "Global", globalSplit ? new[] { "Profile_AC", "Profile_Global" } : new[] { "Profile_Global", "Profile_AC" });

                // Games, each once (prefer the single profile, else its AC copy, else DC).
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in settings.Containers.Keys)
                {
                    if (!key.StartsWith("Profile_Game_")) continue;
                    string game = key.Substring("Profile_Game_".Length);
                    if (game.EndsWith("_AC") || game.EndsWith("_DC")) game = game.Substring(0, game.Length - 3);
                    if (string.IsNullOrWhiteSpace(game) || !seen.Add(game)) continue;
                    AddMigrationEntry(entries, settings, game,
                        new[] { $"Profile_Game_{game}", $"Profile_Game_{game}_AC", $"Profile_Game_{game}_DC" });
                }

                var request = new ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.MigrateWidgetProfiles },
                    { "Content", entries.Stringify() }
                };
                App.PipeClient?.SendValueSet(request);
                settings.Values[ProfilesMigratedKey] = true;
                Logger.Info($"Sent {entries.Count} profile(s) to the helper (one-time 1.9.7a migration)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Profile migration failed: {ex.Message}");
            }
        }

        private static void AddMigrationEntry(JsonArray entries, ApplicationDataContainer settings, string name, string[] containerKeys)
        {
            foreach (var key in containerKeys)
            {
                if (!settings.Containers.ContainsKey(key)) continue;
                var v = settings.Containers[key].Values;

                double Num(string k, double fallback)
                {
                    if (!v.TryGetValue(k, out var o) || o == null) return fallback;
                    try { return Convert.ToDouble(o); } catch { return fallback; }
                }
                bool fpsOn = v.TryGetValue("FPSLimitEnabled", out var fo) && fo is bool fb && fb;

                var entry = new JsonObject
                {
                    ["name"] = JsonValue.CreateStringValue(name),
                    ["tdp"] = JsonValue.CreateNumberValue(Math.Round(Num("TDP", 0))),
                    ["tdpFast"] = JsonValue.CreateNumberValue(Math.Round(Num("TDPFast", 0))),
                    ["tdpPeak"] = JsonValue.CreateNumberValue(Math.Round(Num("TDPPeak", 0))),
                    ["fps"] = JsonValue.CreateNumberValue(fpsOn ? Math.Round(Num("FPSLimitValue", 0)) : 0),
                    ["osPower"] = JsonValue.CreateNumberValue(Math.Round(Num("OSPowerMode", -1))),
                    ["overlay"] = JsonValue.CreateNumberValue(Num("OverlayLevel", 0) > 0 ? 1 : 0),
                };
                entries.Add(entry);
                return;
            }
        }
    }
}
