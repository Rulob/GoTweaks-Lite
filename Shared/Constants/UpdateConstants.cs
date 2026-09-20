namespace Shared.Constants
{
    /// <summary>
    /// Self-update configuration.
    ///
    /// <see cref="Repo"/> is the GitHub "owner/repo" the app checks for new
    /// releases. Leave it EMPTY to disable auto-updates entirely: every update
    /// check then short-circuits to "up to date" and every self-install is
    /// refused.
    /// </summary>
    public static class UpdateConstants
    {
        /// <summary>
        /// The human-facing "official" version — what users see in the About card and in
        /// every update-check surface, matching the GitHub release tag / releases.md heading
        /// (e.g. "1.1"). Bump this by hand exactly once per official release, at the same time
        /// you write the releases.md entry and tag the GitHub release — nothing else needs to
        /// change; the MSIX package version keeps auto-incrementing per build independently
        /// (Build-Release.ps1) and still drives the actual update-available comparison.
        ///
        /// Deliberately decoupled from the MSIX package version: that number must stay
        /// monotonically increasing per BUILD for Windows' AppX upgrade rules and the
        /// update-check comparison (see GoTweaksUpdateService.CheckAsync), so it can't also
        /// carry a friendly per-RELEASE number without breaking either. Mixing the two directly
        /// in the UI ("Your version: 0.3.2524.0, new version: 1.1") is what this constant fixes.
        /// </summary>
        public const string FriendlyVersion = "1.1";

        /// <summary>GitHub "owner/repo" to query for releases. Empty = updates disabled.</summary>
        public const string Repo = "Rulob/GoTweaks-Lite";

        /// <summary>True when a release repo is configured (update checks/installs allowed).</summary>
        public static bool UpdatesEnabled => !string.IsNullOrWhiteSpace(Repo);

        /// <summary>The GitHub "latest release" API endpoint, or empty when disabled.</summary>
        public static string LatestReleaseApiUrl =>
            UpdatesEnabled ? $"https://api.github.com/repos/{Repo}/releases/latest" : "";
    }
}
