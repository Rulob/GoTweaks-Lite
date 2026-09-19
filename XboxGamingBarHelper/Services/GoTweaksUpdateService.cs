using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NLog;
using Shared.Constants;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// Checks the GoTweaks GitHub releases feed for a newer version of the
    /// installed package, and optionally downloads + installs the signed
    /// .msixbundle via PowerShell's Add-AppxPackage (helper runs elevated
    /// so the child inherits admin, and AppX install requires it).
    ///
    /// The release repo is configured centrally in
    /// <see cref="Shared.Constants.UpdateConstants.Repo"/>. Leave that constant
    /// empty to disable updates entirely — every check then short-circuits to
    /// "up to date" and self-install is refused.
    ///
    /// Everything here is defensive — network issues, API rate limits, or
    /// asset-naming changes produce an empty/update-not-found result rather
    /// than throwing. The widget can always fall back to its manual
    /// "download from releases page" link.
    /// </summary>
    internal static class GoTweaksUpdateService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly HttpClient _http = CreateHttpClient();

        private static GoTweaksUpdateResult _lastResult;
        public static GoTweaksUpdateResult LastResult => _lastResult;

        private static HttpClient CreateHttpClient()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)12288; // Tls13
            }
            catch { }
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub API requires a UA header on every request.
            client.DefaultRequestHeaders.Add("User-Agent", "GoTweaks-UpdateCheck/1.0");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return client;
        }

        /// <summary>
        /// Fetches the latest published release from GitHub and compares its
        /// tag to the supplied <paramref name="currentVersion"/>. Returns a
        /// populated result with IsUpdateAvailable + DownloadUrl when newer,
        /// or IsUpdateAvailable=false when up to date / unreachable.
        /// </summary>
        public static async Task<GoTweaksUpdateResult> CheckAsync(string currentVersion)
        {
            var result = new GoTweaksUpdateResult
            {
                CurrentVersion = currentVersion ?? "",
            };

            // Updates are disabled while no release repo is configured. Never reach out
            // to the upstream repo — report "up to date" so the app does not self-update.
            if (!UpdateConstants.UpdatesEnabled)
            {
                Logger.Info("GoTweaks update check skipped — no release repo configured (updates disabled)");
                _lastResult = result;
                return result;
            }

            try
            {
                string url = UpdateConstants.LatestReleaseApiUrl;
                using var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn($"GoTweaks update check: GitHub returned HTTP {(int)response.StatusCode} (e.g. rate limit) — check did not actually complete");
                    result.CheckFailed = true;
                    _lastResult = result;
                    return result;
                }
                string body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    Logger.Warn("GoTweaks update check: unexpected response shape from GitHub — check did not actually complete");
                    result.CheckFailed = true;
                    _lastResult = result;
                    return result;
                }

                string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : "";
                string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() : "";
                string htmlUrl = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String
                    ? h.GetString() : "";

                result.LatestVersion = NormaliseVersion(tag);
                result.LatestTag = tag ?? "";
                result.ReleaseName = string.IsNullOrWhiteSpace(name) ? tag : name;
                result.ReleasePageUrl = htmlUrl ?? "";

                // Find the first msixbundle asset — that's the sideload-install
                // artefact. Skip cer/appxsym/pfx/etc.
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (asset.ValueKind != JsonValueKind.Object) continue;
                        var aname = asset.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String
                            ? an.GetString() ?? "" : "";
                        var aurl = asset.TryGetProperty("browser_download_url", out var au) && au.ValueKind == JsonValueKind.String
                            ? au.GetString() ?? "" : "";
                        if (aname.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase))
                        {
                            result.DownloadUrl = aurl;
                            result.AssetName = aname;
                            break;
                        }
                    }
                }

                // Compare by the BUILD version embedded in the .msixbundle asset filename
                // (e.g. "GoTweaks_0.3.2491.0.msixbundle" -> 0.3.2491.0), NOT the release tag.
                // Release tags are a cosmetic label in this fork (e.g. "1.0") and must never
                // drive the numeric comparison against the installed MSIX version — otherwise a
                // "1.0" tag always parses as newer than a "0.3.xxxx" build and loops forever.
                // Fall back to the tag only if the asset name has no parsable version.
                string compareVersion = ExtractVersionFromFileName(result.AssetName);
                if (string.IsNullOrEmpty(compareVersion))
                    compareVersion = result.LatestVersion;

                result.IsUpdateAvailable = IsNewer(compareVersion, currentVersion);
                Logger.Info($"GoTweaks update check: current={currentVersion}, tag={result.LatestTag}, bundleVersion={compareVersion}, update={result.IsUpdateAvailable}, asset={result.AssetName}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"GoTweaks update check threw: {ex.Message}");
                result.CheckFailed = true;
            }
            _lastResult = result;
            return result;
        }

        /// <summary>
        /// Downloads the msixbundle and launches `Add-AppxPackage` via
        /// PowerShell. Returns JSON the widget can display. Requires the
        /// msix signing cert to be trusted on the machine — if it isn't,
        /// the install fails cleanly and the user is pointed at the
        /// releases page.
        /// </summary>
        public static async Task<string> InstallAsync(string downloadUrl)
        {
            // Refuse self-install while updates are disabled (no repo configured).
            if (!UpdateConstants.UpdatesEnabled)
                return "{\"success\":false,\"message\":\"Updates are disabled (no release repo configured).\"}";

            if (string.IsNullOrWhiteSpace(downloadUrl))
                return "{\"success\":false,\"message\":\"No download URL from GitHub release.\"}";

            Uri uri;
            try { uri = new Uri(downloadUrl); }
            catch { return "{\"success\":false,\"message\":\"Invalid URL.\"}"; }
            if (uri.Scheme != "https")
                return "{\"success\":false,\"message\":\"Only https URLs are accepted.\"}";
            // Pin to GitHub — the only host that serves release assets.
            var host = uri.Host.ToLowerInvariant();
            bool trusted = host.EndsWith("github.com") || host.EndsWith("githubusercontent.com");
            if (!trusted)
                return "{\"success\":false,\"message\":\"Host not allowed: " + host + "\"}";

            string fileName = System.IO.Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "gotweaks.msixbundle";
            foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(bad, '_');

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GoTweaksSelfUpdate");
            try { System.IO.Directory.CreateDirectory(dir); }
            catch (Exception ex) { return "{\"success\":false,\"message\":\"Temp dir: " + ex.Message.Replace("\"", "'") + "\"}"; }

            string target = System.IO.Path.Combine(dir, fileName);
            Logger.Info($"GoTweaksUpdateService: downloading {downloadUrl} -> {target}");
            try
            {
                using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    return "{\"success\":false,\"message\":\"HTTP " + (int)response.StatusCode + " from GitHub.\"}";
                using (var src = await response.Content.ReadAsStreamAsync())
                using (var dst = System.IO.File.Create(target))
                    await src.CopyToAsync(dst);
            }
            catch (Exception ex)
            {
                Logger.Warn($"GoTweaks download failed: {ex.Message}");
                return "{\"success\":false,\"message\":\"Download failed: " + ex.Message.Replace("\"", "'") + "\"}";
            }

            // Kick off the install via PowerShell. Add-AppxPackage blocks for
            // seconds, so run it detached — we report "launched" and the
            // widget will disappear on its own once the app is reinstalled.
            try
            {
                // -ForceUpdateFromAnyVersion mirrors the manual installer (Install GoTweaks.ps1):
                // it lets the reinstall proceed even if the bundle's version isn't strictly higher
                // (e.g. a re-published release), so a self-update can never wedge on a version check.
                string psCommand = $"Add-AppxPackage -Path '{target.Replace("'", "''")}' -ForceApplicationShutdown -ForceUpdateFromAnyVersion";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi);
                Logger.Info($"GoTweaks update install started via PowerShell for {target}");
                return "{\"success\":true,\"message\":\"Installing update — the widget will reload when finished.\"}";
            }
            catch (Exception ex)
            {
                Logger.Warn($"GoTweaks install launch failed: {ex.Message}");
                return "{\"success\":false,\"message\":\"Install failed: " + ex.Message.Replace("\"", "'") + "\"}";
            }
        }

        /// <summary>
        /// Pulls the dotted build version out of a release asset filename
        /// ("GoTweaks_0.3.2491.0.msixbundle" → "0.3.2491.0"). Returns "" when the
        /// name carries no parsable version. This is what we compare against the
        /// installed MSIX version, so the release *tag* stays a free-form label.
        /// </summary>
        private static string ExtractVersionFromFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(fileName, @"(\d+\.\d+\.\d+(?:\.\d+)?)");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>Strips the leading "v" GitHub tags often carry ("v0.3.2" → "0.3.2").</summary>
        private static string NormaliseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            var t = tag.Trim();
            if (t.Length > 1 && (t[0] == 'v' || t[0] == 'V')) t = t.Substring(1);
            return t;
        }

        /// <summary>
        /// Dotted-numeric compare; returns true if <paramref name="candidate"/>
        /// is strictly newer than <paramref name="current"/>. Anything
        /// unparsable falls back to a case-insensitive not-equal check so we
        /// don't offer an "update" that's just a rename.
        /// </summary>
        private static bool IsNewer(string candidate, string current)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(current)) return false;
            var c = ParseVersion(candidate);
            var i = ParseVersion(current);
            if (c == null || i == null)
                return !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase);
            int len = Math.Max(c.Length, i.Length);
            for (int k = 0; k < len; k++)
            {
                long a = k < c.Length ? c[k] : 0;
                long b = k < i.Length ? i[k] : 0;
                if (a > b) return true;
                if (a < b) return false;
            }
            return false;
        }

        private static long[] ParseVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split('.');
            var r = new long[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var digits = new string(parts[i].TakeWhile(char.IsDigit).ToArray());
                if (digits.Length == 0) return null;
                if (!long.TryParse(digits, out r[i])) return null;
            }
            return r;
        }
    }

    internal sealed class GoTweaksUpdateResult
    {
        public bool IsUpdateAvailable { get; set; }
        // True when the check itself failed (network error, non-success HTTP status such as a
        // GitHub API rate-limit, or an unparsable response) rather than genuinely confirming the
        // installed version is current. Consumers that only look at IsUpdateAvailable still get
        // the safe "no update" behavior; consumers showing a status message to the user (the
        // System tab's manual "Check for Update") should use this to avoid claiming "you're up
        // to date" when the check couldn't actually complete.
        public bool CheckFailed { get; set; }
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string LatestTag { get; set; } = "";
        public string ReleaseName { get; set; } = "";
        public string ReleasePageUrl { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string AssetName { get; set; } = "";

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        }
    }
}
