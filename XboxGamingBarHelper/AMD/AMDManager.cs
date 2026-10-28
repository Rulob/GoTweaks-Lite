using Microsoft.Win32;
using System;
using System.Diagnostics;
//using XboxGamingBarHelper.Windows;
//using System.Windows.Forms;
using XboxGamingBarHelper.OnScreenDisplay;
//using XboxGamingBarHelper.Settings;
using Windows.UI.Input.Preview.Injection;
using Windows.System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace XboxGamingBarHelper.AMD
{
    internal class AMDManager : OnScreenDisplayManager
    {
        // AMD Software stuff
        // Computer\HKEY_CURRENT_USER\Software\AMD\CN\Performance
        private static readonly RegistryKey AMD_PERFORMANCE_KEY_ROOT = Registry.CurrentUser;
        private const string AMD_PERFORMANCE_KEY_PATH = @"Software\AMD\CN\Performance";
        private const string AMD_PERFORMANCE_STATE_KEY_NAME = "MetricsOverlayState";
        private const string AMD_PERFORMANCE_PROFILE_KEY_NAME = "MetricsProfile";

        // ADLX stuff
        private ADLX_RESULT adlxInitializeResult;
        private ADLXHelper adlxHelper;
        private IADLXSystem adlxSystemSevices;
        private IADLXGPU adlxInternalGPU;
        private IADLXGPU adlxDedicatedGPU;
        private IADLXGPU adlxSecondDedicatedGPU;

        private InputInjector inputInjector;
        private InjectedInputKeyboardInfo[] turnAMDOverlayOnOffKeyboardCombo;
        private InjectedInputKeyboardInfo[] changeAMDOverlayLevelKeyboardCombo;
        private List<Tuple<int, int>> amdOverlayLevelList;
        private Dictionary<int, int> amdOverlayLevelMap;

        private long lastUpdate;

        // Hard ceiling for the whole ADLX init. Certain AMD driver builds hang
        // forever inside a native ADLX probe with no way to interrupt the call
        // (issue #90: GetAMDFluidMotionFrames wedges on the Legion Go S / Z2 Go
        // driver). Running init on a watchdog thread and abandoning it past this
        // timeout lets the rest of the helper (Legion tab, TDP, everything
        // non-AMD) come up instead of the whole manager-init wave wedging.
        private const int AdlxInitTimeoutMs = 20000;
        private volatile bool adlxInitTimedOut;

        public AMDManager() : base()
        {
            RunAdlxInitWithWatchdog();
        }

        private void InitializeAdlxCore()
        {
                Logger.Info("Initializing ADLX...");

                // Log DLL search path info for debugging
                var currentDir = Environment.CurrentDirectory;
                var exeDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                Logger.Info($"ADLX DLL search - CurrentDir: {currentDir}, ExeDir: {exeDir}");

                // Check if ADLXCSharpBind.dll exists
                var dllPath = System.IO.Path.Combine(exeDir ?? currentDir, "ADLXCSharpBind.dll");
                var dllExists = System.IO.File.Exists(dllPath);
                Logger.Info($"ADLX DLL path check - {dllPath} exists: {dllExists}");

                // Note: SetDllDirectory is called in Program.cs before manager initialization
                // to ensure native DLLs are found when running elevated from deployed location

                // Initialize ADLX with ADLXHelper
                Logger.Info("Creating ADLXHelper instance...");
                adlxHelper = new ADLXHelper();
                Logger.Info("ADLXHelper instance created, calling Initialize()...");
                adlxInitializeResult = adlxHelper.Initialize();

                if (adlxInitializeResult != ADLX_RESULT.ADLX_OK)
                {
                    Logger.Error("AMD Manager initialize failed.");
                    throw new Exception("ADLX initialization returned non-OK result");
                }

                adlxSystemSevices = adlxHelper.GetSystemServices();
                if (adlxSystemSevices == null)
                {
                    Logger.Error("Can't get AMD system service.");
                    throw new Exception("ADLX GetSystemServices returned null");
                }


            // Get GPU
            var gpuListPointer = ADLX.new_gpuListP_Ptr();
            adlxSystemSevices.GetGPUs(gpuListPointer);
            var gpuList = ADLX.gpuListP_Ptr_value(gpuListPointer);

            Logger.Info($"Found {gpuList.Size()} GPU.");
            for (uint i = 0; i < gpuList.Size(); i++)
            {
                var gpuPointer = ADLX.new_gpuP_Ptr();
                gpuList.At(i, gpuPointer);
                var gpu = ADLX.gpuP_Ptr_value(gpuPointer);

                var gpuIsExternalPointer = ADLX.new_boolP();
                gpu.IsExternal(gpuIsExternalPointer);
                var gpuIsExternal = ADLX.boolP_value(gpuIsExternalPointer);

                Logger.Info($"GPU {i}: IsExternal={gpuIsExternal}");

                if (gpuIsExternal)
                {
                    if (adlxDedicatedGPU == null)
                    {
                        adlxDedicatedGPU = gpu;
                        Logger.Info($"Found a dGPU (external) at index {i}");
                    }
                    else if (adlxSecondDedicatedGPU == null)
                    {
                        adlxSecondDedicatedGPU = gpu;
                        Logger.Info($"Found second dGPU at index {i}");
                    }
                    else
                    {
                        Logger.Warn($"Found too many dGPUs at index {i}");
                    }
                }
                else
                {
                    if (adlxInternalGPU == null)
                    {
                        adlxInternalGPU = gpu;
                        Logger.Info($"Found an iGPU (internal) at index {i}");
                    }
                    else
                    {
                        // Store additional non-external GPUs as dedicated
                        if (adlxDedicatedGPU == null)
                        {
                            adlxDedicatedGPU = gpu;
                            Logger.Info($"Found additional GPU (storing as dGPU) at index {i}");
                        }
                        else
                        {
                            Logger.Warn($"Found too many GPUs at index {i}");
                        }
                    }
                }
            }

            // No iGPU (desktop with a discrete card): read metrics from the dGPU.
            if (adlxInternalGPU == null && adlxDedicatedGPU != null)
            {
                adlxInternalGPU = adlxDedicatedGPU;
            }
            if (adlxInternalGPU == null)
            {
                Logger.Error("No AMD GPU found! GPU metrics from ADLX will not be available.");
            }

            // Radeon feature probes (RSR, AFMF, Anti-Lag, Boost, Chill, Image Sharpening, display
            // color) were removed with the Display tab. ADLX is still used for the GPU metrics
            // fallback (TryGetCurrentGpuMetrics) and the AMD overlay keys below.
            Logger.Info("AMD Manager initialized successfully.");

            inputInjector = InputInjector.TryCreate();
            turnAMDOverlayOnOffKeyboardCombo = new InjectedInputKeyboardInfo[]
            {
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.LeftControl, KeyOptions = InjectedInputKeyOptions.None },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.LeftShift, KeyOptions = InjectedInputKeyOptions.None },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.O, KeyOptions = InjectedInputKeyOptions.None },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.LeftControl, KeyOptions = InjectedInputKeyOptions.KeyUp },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.LeftShift, KeyOptions = InjectedInputKeyOptions.KeyUp },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.O, KeyOptions = InjectedInputKeyOptions.KeyUp },
            };
            changeAMDOverlayLevelKeyboardCombo = new InjectedInputKeyboardInfo[]
            {
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.LeftControl, KeyOptions = InjectedInputKeyOptions.None },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.LeftShift, KeyOptions = InjectedInputKeyOptions.None },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.X, KeyOptions = InjectedInputKeyOptions.None },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.LeftControl, KeyOptions = InjectedInputKeyOptions.KeyUp },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.LeftShift, KeyOptions = InjectedInputKeyOptions.KeyUp },
                new InjectedInputKeyboardInfo { VirtualKey = (ushort)VirtualKey.X, KeyOptions = InjectedInputKeyOptions.KeyUp }
            };
            // In AMD Software: Adrenaline Edition:
            // Level 3 is basic (FPS only)                      => Our level 1 (FPS)
            // Level 1 is intermediate (FPS + Usage + Wattage)  => Our level 2 (BATTERY)
            // Level 2 is advanced (many elements)              => Our level 3 (DETAILED)
            // Level 0 is custom (user seletectable)            => Our level 4 (FULL)
            amdOverlayLevelList = new List<Tuple<int, int>>()
            {
                new Tuple<int, int>(1, 3),
                new Tuple<int, int>(2, 1),
                new Tuple<int, int>(3, 2),
                new Tuple<int, int>(4, 0),
            };
            amdOverlayLevelMap = new Dictionary<int, int>();
            foreach (var amdOverlayLevel in amdOverlayLevelList)
            {
                amdOverlayLevelMap.Add(amdOverlayLevel.Item1, amdOverlayLevel.Item2);
            }
            lastUpdate = 0;
        }

        /// <summary>
        /// Runs InitializeAdlxCore on a background thread with a hard timeout so
        /// a native ADLX probe hang (issue #90) can't wedge the manager-init
        /// wave. On timeout the thread is abandoned (a hung native call can't be
        /// safely aborted) and we proceed with ADLX_FAIL; the process keeps the
        /// dangling thread until exit, but the helper comes up.
        /// </summary>
        private void RunAdlxInitWithWatchdog()
        {
            Exception adlxException = null;
            var adlxThread = new System.Threading.Thread(() =>
            {
                try { InitializeAdlxCore(); }
                catch (Exception ex) { adlxException = ex; }
            })
            {
                IsBackground = true,
                Name = "AMDManager-ADLXInit",
            };

            adlxThread.Start();
            if (!adlxThread.Join(AdlxInitTimeoutMs))
            {
                adlxInitTimedOut = true;
                adlxInitializeResult = ADLX_RESULT.ADLX_FAIL;
                Logger.Error($"ADLX init did not complete within {AdlxInitTimeoutMs}ms — abandoning the init thread (likely a native driver probe hang, e.g. Legion Go S / Z2 Go FMF, issue #90). AMD features disabled; the helper continues so the Legion tab, TDP, and everything non-AMD still work.");
                return;
            }

            if (adlxException != null)
            {
                var ex = adlxException;
                Logger.Error($"ADLX initialization failed with exception: {ex.Message}");
                Logger.Error($"Exception type: {ex.GetType().FullName}");

                // Log inner exceptions to reveal the actual cause (especially for TypeInitializationException)
                var innerEx = ex.InnerException;
                while (innerEx != null)
                {
                    Logger.Error($"Inner exception: {innerEx.GetType().FullName}: {innerEx.Message}");
                    innerEx = innerEx.InnerException;
                }

                Logger.Error($"Stack trace: {ex.StackTrace}");

                // Provide guidance on common causes
                if (ex.Message.Contains("ADLXPINVOKE") || ex.Message.Contains("type initializer"))
                {
                    Logger.Error("ADLX DLL failed to load. Possible causes:");
                    Logger.Error("  1. AMD Adrenalin drivers not installed (ADLX requires AMD driver)");
                    Logger.Error("  2. AMD driver version incompatible with ADLX SDK");
                    Logger.Error("  3. Visual C++ Runtime not installed");
                    Logger.Error("  4. System has no AMD GPU");
                }

                adlxInitializeResult = ADLX_RESULT.ADLX_FAIL;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Logger.Info("AMDManager: Disposing ADLX resources");
                adlxInternalGPU?.Dispose();
                if (!ReferenceEquals(adlxDedicatedGPU, adlxInternalGPU)) adlxDedicatedGPU?.Dispose();
                adlxSecondDedicatedGPU?.Dispose();
                adlxHelper?.Dispose();
                Logger.Info("AMDManager: ADLX resources disposed");
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Snapshot of GPU metrics returned by ADLX's IADLXGPUMetrics. Each "Has" flag
        /// reports whether ADLX successfully populated the corresponding field — on
        /// hardware where ADLX itself doesn't support a given metric, the call returns
        /// non-OK and we leave the field at its default. Used as a fallback when
        /// LibreHardwareMonitor doesn't expose the matching sensor (Mute's Legion Go 2 /
        /// Z2-series APU showed GPU usage but no Wattage/Temperature/Clock; ADLX exposes
        /// them when LHM doesn't).
        /// </summary>
        internal struct GpuMetricsSnapshot
        {
            public bool HasUsage;
            public double UsagePercent;

            public bool HasClockMHz;
            public int GpuClockMHz;

            public bool HasVramClockMHz;
            public int VramClockMHz;

            public bool HasTemperatureC;
            public double GpuTemperatureC;

            public bool HasPowerW;
            public double GpuPowerW;

            public bool HasVramUsedMB;
            public int VramUsedMB;
        }

        /// <summary>
        /// Reads the current ADLX GPU metrics for the internal GPU (or the dedicated
        /// GPU on dGPU-only systems). Returns false if ADLX is unavailable, no GPU was
        /// resolved, or the metrics call failed wholesale; otherwise returns true with
        /// per-field "Has" flags indicating which metrics ADLX populated. Caller should
        /// treat each field as "use only when Has* is true."
        /// </summary>
        public bool TryGetCurrentGpuMetrics(out GpuMetricsSnapshot snapshot)
        {
            snapshot = default;

            if (adlxInitializeResult != ADLX_RESULT.ADLX_OK || adlxSystemSevices == null)
            {
                return false;
            }

            // Prefer iGPU (handhelds), fall back to dGPU (desktop with discrete card).
            var gpu = adlxInternalGPU ?? adlxDedicatedGPU;
            if (gpu == null)
            {
                return false;
            }

            IADLXPerformanceMonitoringServices perfServices = null;
            IADLXGPUMetrics metrics = null;
            SWIGTYPE_p_double dPtr = null;
            SWIGTYPE_p_int iPtr = null;

            try
            {
                var perfPtr = ADLX.new_performanceMonitoringSerP_Ptr();
                try
                {
                    var perfResult = adlxSystemSevices.GetPerformanceMonitoringServices(perfPtr);
                    if (perfResult != ADLX_RESULT.ADLX_OK)
                    {
                        return false;
                    }
                    perfServices = ADLX.performanceMonitoringSerP_Ptr_value(perfPtr);
                }
                finally
                {
                    ADLX.delete_performanceMonitoringSerP_Ptr(perfPtr);
                }

                if (perfServices == null)
                {
                    return false;
                }

                var metricsPtr = ADLX.new_gpuMetricsP_Ptr();
                try
                {
                    var metricsResult = perfServices.GetCurrentGPUMetrics(gpu, metricsPtr);
                    if (metricsResult != ADLX_RESULT.ADLX_OK)
                    {
                        return false;
                    }
                    metrics = ADLX.gpuMetricsP_Ptr_value(metricsPtr);
                }
                finally
                {
                    ADLX.delete_gpuMetricsP_Ptr(metricsPtr);
                }

                if (metrics == null)
                {
                    return false;
                }

                dPtr = ADLX.new_doubleP();
                iPtr = ADLX.new_intP();

                if (metrics.GPUUsage(dPtr) == ADLX_RESULT.ADLX_OK)
                {
                    snapshot.UsagePercent = ADLX.doubleP_value(dPtr);
                    snapshot.HasUsage = true;
                }
                if (metrics.GPUClockSpeed(iPtr) == ADLX_RESULT.ADLX_OK)
                {
                    snapshot.GpuClockMHz = ADLX.intP_value(iPtr);
                    snapshot.HasClockMHz = true;
                }
                if (metrics.GPUVRAMClockSpeed(iPtr) == ADLX_RESULT.ADLX_OK)
                {
                    snapshot.VramClockMHz = ADLX.intP_value(iPtr);
                    snapshot.HasVramClockMHz = true;
                }
                if (metrics.GPUTemperature(dPtr) == ADLX_RESULT.ADLX_OK)
                {
                    snapshot.GpuTemperatureC = ADLX.doubleP_value(dPtr);
                    snapshot.HasTemperatureC = true;
                }
                if (metrics.GPUPower(dPtr) == ADLX_RESULT.ADLX_OK)
                {
                    snapshot.GpuPowerW = ADLX.doubleP_value(dPtr);
                    snapshot.HasPowerW = true;
                }
                if (metrics.GPUVRAM(iPtr) == ADLX_RESULT.ADLX_OK)
                {
                    snapshot.VramUsedMB = ADLX.intP_value(iPtr);
                    snapshot.HasVramUsedMB = true;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"TryGetCurrentGpuMetrics: ADLX call threw — {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                if (dPtr != null) { ADLX.delete_doubleP(dPtr); }
                if (iPtr != null) { ADLX.delete_intP(iPtr); }
                metrics?.Dispose();
                perfServices?.Dispose();
            }
        }

        public override void Update()
        {
            base.Update();

            var now = DateTime.Now.Ticks;
            Logger.Debug($"Time since last update: {now - lastUpdate}");
            if (now - lastUpdate < TimeSpan.TicksPerSecond * 2)
            {
                return;
            }
            lastUpdate = now;

            if (IsInUsed)
            {
                SetAMDValues();
            }
        }

        public override void SetLevel(int level)
        {
            base.SetLevel(level);

            // SetAMDValues();
        }

        private async void SetAMDValues()
        {
            try
            {
                var (currentlyOn, currentLevel) = ReadCurrentMetricsProfile();
                if (onScreenDisplayLevel == 0)
                {
                    if (currentlyOn == 1)
                    {
                        Logger.Info("Turning OFF AMD On-Screen Display.");
                        inputInjector.InjectKeyboardInput(turnAMDOverlayOnOffKeyboardCombo);
                    }
                    else
                    {
                        Logger.Info("AMD On-Screen Display is already turned OFF.");
                    }
                }
                else
                {
                    if (currentlyOn == 0)
                    {
                        Logger.Info("Turning ON AMD On-Screen Display.");
                        inputInjector.InjectKeyboardInput(turnAMDOverlayOnOffKeyboardCombo);
                        await Task.Delay(100);
                    }

                    var targetLevel = amdOverlayLevelMap[onScreenDisplayLevel];
                    if (currentLevel != targetLevel)
                    {
                        var currentLevelIndex = 0;
                        var targetLevelIndex = 0;
                        for (var i = 0; i < amdOverlayLevelList.Count; i++)
                        {
                            if (amdOverlayLevelList[i].Item2 == currentLevel)
                            {
                                currentLevelIndex = i;
                            }
                            if (amdOverlayLevelList[i].Item2 == targetLevel)
                            {
                                targetLevelIndex = i;
                            }
                        }

                        var numberOfKeyPresses = Math.Abs(targetLevelIndex - currentLevelIndex);
                        Logger.Info($"Current AMD On-Screen Display level is {currentLevel} at index {currentLevelIndex}, need to change to {targetLevel} at index {targetLevelIndex}, need to press {numberOfKeyPresses} times.");
                        for (var i = 0; i < numberOfKeyPresses; i++)
                        {
                            inputInjector.InjectKeyboardInput(changeAMDOverlayLevelKeyboardCombo);
                            await Task.Delay(100);
                        }
                    }
                    else
                    {
                        Logger.Info($"Current AMD On-Screen Display level is {currentLevel} already matches {targetLevel}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in SetAMDValues: {ex.Message}");
            }
        }

        private static Tuple<int, int> ReadCurrentMetricsProfile()
        {
            try
            {
                using (RegistryKey subKey = AMD_PERFORMANCE_KEY_ROOT.OpenSubKey(AMD_PERFORMANCE_KEY_PATH))
                {
                    if (subKey != null)
                    {
                        object stateObject = subKey.GetValue(AMD_PERFORMANCE_STATE_KEY_NAME);

                        if (stateObject != null)
                        {
                            Logger.Debug($"Value of '{AMD_PERFORMANCE_STATE_KEY_NAME}' at '{AMD_PERFORMANCE_KEY_PATH}': {stateObject} of type {stateObject.GetType().Name}");
                            var stateValue = (int)stateObject;
                            if (stateValue == 0)
                            {
                                return new Tuple<int, int>(0, 0);
                            }
                            else
                            {
                                var profileObject = subKey.GetValue(AMD_PERFORMANCE_PROFILE_KEY_NAME);
                                if (profileObject != null)
                                {
                                    Logger.Debug($"Value of {AMD_PERFORMANCE_PROFILE_KEY_NAME} is {profileObject} of type {profileObject.GetType().Name}");
                                    var profileValue = (int)profileObject;
                                    return new Tuple<int, int>(stateValue, profileValue);
                                }
                                else
                                {
                                    return new Tuple<int, int>(stateValue, 0);
                                }
                            }
                        }
                        else
                        {
                            Logger.Warn($"Value '{AMD_PERFORMANCE_STATE_KEY_NAME}' not found in '{AMD_PERFORMANCE_KEY_PATH}'.");
                            return new Tuple<int, int>(0, 0);
                        }
                    }
                    else
                    {
                        Logger.Warn($"Registry key '{AMD_PERFORMANCE_KEY_PATH}' not found.");
                        return new Tuple<int, int>(0, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"An error occurred: {ex.Message}");
                return new Tuple<int, int>(0, 0);
            }
        }

        private static bool SetCurrentMetricsProfile(int metricOverlayState, int metricProfile)
        {
            try
            {
                using (RegistryKey subKey = AMD_PERFORMANCE_KEY_ROOT.OpenSubKey(AMD_PERFORMANCE_KEY_PATH))
                {
                    if (subKey != null)
                    {
                        subKey.SetValue(AMD_PERFORMANCE_STATE_KEY_NAME, metricOverlayState);
                        subKey.SetValue(AMD_PERFORMANCE_PROFILE_KEY_NAME, metricProfile);
                        Logger.Debug($"Set registry key '{AMD_PERFORMANCE_KEY_PATH}\\{AMD_PERFORMANCE_STATE_KEY_NAME}' to {metricOverlayState} and '{AMD_PERFORMANCE_KEY_PATH}\\{AMD_PERFORMANCE_PROFILE_KEY_NAME}' to {metricProfile}.");
                        return true;
                    }
                    else
                    {
                        Logger.Warn($"Registry key '{AMD_PERFORMANCE_KEY_PATH}' not found.");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"An error occurred: {ex.Message}");
                return false;
            }
        }
    }
}
