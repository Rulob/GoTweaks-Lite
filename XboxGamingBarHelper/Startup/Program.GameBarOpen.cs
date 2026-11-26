using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace XboxGamingBarHelper
{
    internal partial class Program
    {
        // Game Bar's own link for opening it on one widget. Win+G always opens Game Bar on Home,
        // and the widget's ActivateAsync doesn't switch away from Home when the widget sits in the
        // device-maker slot left of it (Show GoTweaks first). The widget id is the package family
        // name, the app id and the widget's extension id (Package.appxmanifest: App / GamingWidget).
        private const string GameBarActivateLinkPrefix = "ms-gamebar://launchForeground/activate/";
        private static string GoTweaksGameBarLink =>
            GameBarActivateLinkPrefix + Shared.Constants.PackageConstants.PackageFamilyName + "_App_GamingWidget";

        // How long the link gets to open Game Bar before Win+G is pressed instead.
        private const int GameBarLinkWaitMs = 3000;
        private const int GameBarLinkPollMs = 150;

        /// <summary>
        /// Whether Game Bar is open, read from Windows' GameBar.IsInputRedirected (true while Game
        /// Bar has the controller and keyboard). Game Bar draws in its own layer, so the foreground
        /// window doesn't show it. Null when Windows won't say.
        /// </summary>
        private static bool? IsGameBarOpen()
        {
            try
            {
                return global::Windows.Gaming.UI.GameBar.IsInputRedirected;
            }
            catch (Exception ex)
            {
                Logger.Debug($"GameBar.IsInputRedirected unavailable: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Opens Game Bar on the GoTweaks widget with Game Bar's activate link (also switches to it
        /// when Game Bar is already open on another tab). Returns false when the link could not be
        /// launched or Game Bar did not open within <see cref="GameBarLinkWaitMs"/>, so the caller
        /// can fall back to Win+G.
        /// </summary>
        private static async Task<bool> OpenGameBarOnGoTweaksAsync()
        {
            bool? wasOpen = IsGameBarOpen();
            try
            {
                using (Process.Start(new ProcessStartInfo(GoTweaksGameBarLink) { UseShellExecute = true })) { }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Game Bar activate link failed to launch: {ex.Message}");
                return false;
            }

            // Already open: the link only switches the tab, there is nothing to wait for. Unknown
            // state: assume it worked, since pressing Win+G on an open Game Bar would close it.
            if (wasOpen != false)
            {
                Logger.Info($"Game Bar activate link sent (Game Bar {(wasOpen == true ? "already open" : "state unknown")})");
                return true;
            }

            var waited = Stopwatch.StartNew();
            while (waited.ElapsedMilliseconds < GameBarLinkWaitMs)
            {
                if (IsGameBarOpen() != false)
                {
                    Logger.Info($"Game Bar opened on GoTweaks via its activate link ({waited.ElapsedMilliseconds} ms)");
                    return true;
                }
                await Task.Delay(GameBarLinkPollMs);
            }

            Logger.Warn("Game Bar activate link opened nothing; falling back to Win+G");
            return false;
        }

        /// <summary>
        /// The "Open Game Bar" (Xbox Guide) button action. It opens Game Bar on the GoTweaks widget,
        /// like the device-maker button does for Legion Space; when Game Bar is already open (so
        /// the press closes it), or that fails, it is Win+G.
        /// </summary>
        private static async void OpenGameBarFromButton()
        {
            try
            {
                // GoTweaks always holds the device-maker slot (1.9.8).
                if (IsGameBarOpen() != true && await OpenGameBarOnGoTweaksAsync())
                {
                    return;
                }
                SendKeyboardShortcutViaInputInjector("Win+G");
            }
            catch (Exception ex)
            {
                Logger.Error($"OpenGameBarFromButton failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs a remapped button's keyboard shortcut. The Xbox Guide action arrives here as
        /// "Win+G" and goes through <see cref="OpenGameBarFromButton"/>.
        /// </summary>
        private static void RunButtonShortcut(string shortcutKeys)
        {
            if (string.Equals(shortcutKeys?.Replace(" ", ""), "Win+G", StringComparison.OrdinalIgnoreCase))
            {
                OpenGameBarFromButton();
                return;
            }
            SendKeyboardShortcutViaInputInjector(shortcutKeys);
        }
    }
}
