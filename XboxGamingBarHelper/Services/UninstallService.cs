using System;
using System.Diagnostics;
using NLog;

namespace XboxGamingBarHelper.Services
{
    /// <summary>
    /// One-shot "--uninstall" mode: restores everything GoTweaks changed on the
    /// system so an uninstall actually leaves the machine the way we found it
    /// (inspired by Handheld Companion 0.31's OEM/driver-stack restoration).
    ///
    /// Stops peer helper processes, then removes the scheduled task and the
    /// deployed helper copy. GoTweaks Lite installs no drivers of its own, so
    /// there is nothing else to take out of the system.
    /// </summary>
    internal static class UninstallService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static void Run()
        {
            Logger.Info("=== GoTweaks uninstall restoration ===");

            Step("stop peer helper processes", StopPeerHelpers);
            Step("remove scheduled task + deployed helper", ElevationBootstrapper.Uninstall);

            Logger.Info("=== Uninstall restoration complete ===");
        }

        private static void Step(string name, Action action)
        {
            try
            {
                Logger.Info($"Uninstall step: {name}");
                action();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Uninstall step '{name}' failed (continuing): {ex.Message}");
            }
        }

        private static void StopPeerHelpers()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("XboxGamingBarHelper"))
            {
                using (p)
                {
                    if (p.Id == self) continue;
                    try
                    {
                        Logger.Info($"Stopping helper process PID {p.Id}");
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not stop PID {p.Id}: {ex.Message}");
                    }
                }
            }
        }
    }
}
