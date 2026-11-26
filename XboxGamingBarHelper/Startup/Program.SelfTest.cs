using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace XboxGamingBarHelper
{
    /// <summary>
    /// "XboxGamingBarHelper.exe --self-test" (1.9.9): loads the parts of the helper that depend on
    /// the runtime and on native/WinRT interop, reports each one and exits. It changes nothing (no
    /// scheduled task, no deploy, no hardware writes, no pipe), so it is safe to run on any PC.
    /// The report is written to %TEMP%\GoTweaks-SelfTest.txt and to the helper log; the exit code is
    /// the number of failed checks.
    /// </summary>
    internal partial class Program
    {
        private static int RunSelfTest()
        {
            var report = new StringBuilder();
            int failures = 0;

            void Check(string name, Func<string> test, bool required = true)
            {
                try
                {
                    string result = test(); // run first: an interpolated AppendLine writes as it goes
                    report.AppendLine($"OK    {name}: {result}");
                }
                catch (Exception ex)
                {
                    if (required) failures++;
                    report.AppendLine($"{(required ? "FAIL" : "INFO")}  {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            report.AppendLine($"GoTweaks helper self-test, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Exe: {Environment.ProcessPath}");
            report.AppendLine();

            Check("Runtime", () => $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}, self-contained={!File.Exists(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "dotnet.exe"))}");
            Check("WinRT ValueSet + pipe message", () =>
            {
                var set = new global::Windows.Foundation.Collections.ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Set },
                    { "Function", (int)Shared.Enums.Function.OSD },
                    { "Content", 1 },
                };
                var json = Shared.IPC.PipeMessage.FromValueSet(set).ToJson();
                var back = Shared.IPC.PipeMessage.FromJson(json);
                if (back.Function != Shared.Enums.Function.OSD) throw new InvalidDataException("round trip changed the function");
                return json;
            });
            Check("Package identity", () => global::Windows.ApplicationModel.Package.Current.Id.FamilyName, required: false);
            Check("Helper settings file", () => Settings.LocalSettingsHelper.SettingsFilePath ?? "(none)");
            Check("WMI", () =>
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                {
                    var item = searcher.Get().Cast<System.Management.ManagementObject>().First();
                    return $"{item["Manufacturer"]} {item["Model"]}";
                }
            });
            Check("Services", () => $"{System.ServiceProcess.ServiceController.GetServices().Length} services");
            Check("HID devices (HidSharp)", () => $"{HidSharp.DeviceList.Local.GetHidDevices().Count()} devices");
            Check("Input injection", () => global::Windows.UI.Input.Preview.Injection.InputInjector.TryCreate() != null ? "available" : "not available");
            Check("WinForms (tray)", () => typeof(System.Windows.Forms.NotifyIcon).Assembly.GetName().Version.ToString());
            Check("LibreHardwareMonitor", () => typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version.ToString());
            Check("RTSS", () =>
            {
                if (!Shared.Utilities.RTSSHelper.IsInstalled()) return "not installed";
                if (!Shared.Utilities.RTSSHelper.IsRunning()) return "installed, not running";
                var apps = RTSS.Interop.OSD.GetAppEntries(RTSS.Interop.AppFlags.MASK);
                return $"running, {apps.Length} hooked app(s): " + string.Join(", ", apps.Select(a => $"{Path.GetFileName(a.Name)} {a.InstantaneousFrames} FPS"));
            });
            foreach (var native in new[] { "libryzenadj.dll", "inpoutx64.dll", "ADLXCSharpBind.dll" })
            {
                Check($"Native {native}", () =>
                {
                    var path = Path.Combine(AppContext.BaseDirectory, native);
                    if (!NativeLibrary.TryLoad(path, out var handle)) throw new DllNotFoundException(path);
                    NativeLibrary.Free(handle);
                    return "loads";
                });
            }
            Check("AMD ADLX", () =>
            {
                using (var adlx = new ADLXHelper())
                {
                    var result = adlx.Initialize();
                    if (result == ADLX_RESULT.ADLX_OK) adlx.Terminate();
                    return result.ToString();
                }
            }, required: false);

            report.AppendLine();
            report.AppendLine(failures == 0 ? "All required checks passed." : $"{failures} required check(s) failed.");

            string text = report.ToString();
            Logger.Info("Self-test:\n" + text);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "GoTweaks-SelfTest.txt"), text); }
            catch (Exception ex) { Logger.Warn($"Self-test: could not write the report file: {ex.Message}"); }
            NLog.LogManager.Flush();
            return failures;
        }
    }
}
