using Microsoft.Win32;
using NLog;
using Shared.Constants;
using Shared.Enums;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    /// <summary>
    /// "Show GoTweaks first in Game Bar": the widget sits in Game Bar's device-maker slot, the one
    /// left of Home (Legion Space on Legion devices). Always on since 1.9.8 (the toggle was
    /// removed): the slot is claimed at every helper start in case something rewrote it, and only
    /// Prepare for Uninstall gives it back.
    /// </summary>
    internal class GameBarFirstSlotProperty : HelperProperty<bool, SettingsManager>
    {
        public GameBarFirstSlotProperty(SettingsManager inManager) : base(true, null, Function.GameBarFirstSlot, inManager)
        {
            // Game Bar reads the slot only when it starts, so don't restart it from here: the
            // first claim shows up the next time Game Bar starts (e.g. after a reboot).
            GameBarOemSlot.Claim(restartGameBar: false);
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            // Nothing can turn it off any more; ignore a stale "off" from an old widget.
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"GameBarFirstSlot: ignoring a change to {Value}, the slot is always claimed");
        }
    }

    /// <summary>
    /// Reads / writes HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\GamingConfiguration
    /// \OEMGameBarWidget (REG_SZ, a package family name). This is what the OEMGameBarWidget
    /// unattend setting writes; Game Bar shows that package's widget first, left of Home.
    /// The value it replaces is kept in the helper settings and put back on Release.
    /// </summary>
    internal static class GameBarOemSlot
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GamingConfiguration";
        private const string ValueName = "OEMGameBarWidget";
        private const string BackupSettingsKey = "GameBarFirstSlotBackup";
        private const string NoValue = "<none>";
        private const int GameBarRestartDelayMs = 1500;

        private static readonly object gate = new object();

        private static bool IsOurs(string value)
        {
            return string.Equals(value, PackageConstants.PackageFamilyName, StringComparison.OrdinalIgnoreCase);
        }

        public static void Claim(bool restartGameBar)
        {
            lock (gate)
            {
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var key = hklm.CreateSubKey(KeyPath, true))
                    {
                        string current = key.GetValue(ValueName) as string;
                        if (IsOurs(current))
                        {
                            Logger.Info("GameBarFirstSlot: slot already points at GoTweaks");
                            return;
                        }

                        // Remember only the first value we replace, so a later re-claim (after
                        // something rewrote the slot) never overwrites the original.
                        if (!LocalSettingsHelper.TryGetValue<string>(BackupSettingsKey, out var backup) || string.IsNullOrEmpty(backup))
                        {
                            LocalSettingsHelper.SetValue(BackupSettingsKey, string.IsNullOrEmpty(current) ? NoValue : current);
                        }

                        key.SetValue(ValueName, PackageConstants.PackageFamilyName, RegistryValueKind.String);
                        Logger.Info($"GameBarFirstSlot: slot set to {PackageConstants.PackageFamilyName} (was '{current ?? NoValue}')");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"GameBarFirstSlot: could not claim the slot: {ex.Message}");
                    return;
                }
            }

            if (restartGameBar)
            {
                RestartGameBarSoon();
            }
        }

        public static void Release(bool restartGameBar)
        {
            lock (gate)
            {
                try
                {
                    LocalSettingsHelper.TryGetValue<string>(BackupSettingsKey, out var backup);

                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var key = hklm.OpenSubKey(KeyPath, true))
                    {
                        string current = key?.GetValue(ValueName) as string;
                        if (key == null || !IsOurs(current))
                        {
                            // Not ours (never claimed, or something else took it since): leave it.
                            Logger.Info($"GameBarFirstSlot: slot is '{current ?? NoValue}', nothing to restore");
                            LocalSettingsHelper.Remove(BackupSettingsKey);
                            return;
                        }

                        if (string.IsNullOrEmpty(backup) || backup == NoValue)
                        {
                            key.DeleteValue(ValueName, false);
                            Logger.Info("GameBarFirstSlot: slot cleared (there was no value before)");
                        }
                        else
                        {
                            key.SetValue(ValueName, backup, RegistryValueKind.String);
                            Logger.Info($"GameBarFirstSlot: slot restored to {backup}");
                        }
                    }
                    LocalSettingsHelper.Remove(BackupSettingsKey);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"GameBarFirstSlot: could not restore the slot: {ex.Message}");
                    return;
                }
            }

            if (restartGameBar)
            {
                RestartGameBarSoon();
            }
        }

        /// <summary>
        /// Game Bar reads the slot only when it starts. Close it after a short delay (so the
        /// widget's toggle reply gets through); it starts again the next time it is opened.
        /// </summary>
        private static void RestartGameBarSoon()
        {
            Task.Run(async () =>
            {
                await Task.Delay(GameBarRestartDelayMs);
                foreach (var process in Process.GetProcessesByName("GameBar"))
                {
                    try
                    {
                        process.Kill();
                        Logger.Info($"GameBarFirstSlot: closed Game Bar (pid {process.Id}) so it picks up the new slot");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"GameBarFirstSlot: could not close Game Bar: {ex.Message}");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            });
        }
    }
}
