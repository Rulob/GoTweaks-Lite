using NLog;
using Shared.Data;
using Shared.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.Devices.Libraries.Legion
{
    /// <summary>
    /// Manager for Legion Go hardware features including controller RGB, touchpad, and performance modes.
    /// </summary>
    internal partial class LegionManager : Manager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Legion Go services
        private LegionControllerService controllerService;
        private LenovoWMIService wmiService;

        // Device detection
        private DeviceInfo deviceInfo;
        private bool isLegionGoDetected = false;
        private bool isControllerConnected = false;

        // Current settings state (cached)
        private bool touchpadEnabled = true;
        private int lightMode = 1; // Solid
        private string lightColor = "#FFFFFF";
        private int lightBrightness = 100;
        private int lightSpeed = 50; // 0-100, default 50%
        private int performanceMode = 2; // Balanced

        // True hardware light state from the b0:01 readback (LegionManager.ControllerBattery.cs).
        // Preferred by the reactive-lighting getters over the stale lightBrightness/lightColor
        // defaults so reactive effects don't blast 100% before the widget reconnects after a restart.
        private bool _hasHwLightState;
        private int _hwLightBrightness;
        private byte _hwLightColorR, _hwLightColorG, _hwLightColorB;

        // True once the light state is known from a real source: a b0:01 hardware readback (even
        // when the light is off) or a widget/profile sync of color/brightness/speed. Until then the
        // lightMode/lightColor/lightBrightness fields hold uninitialized defaults (Solid/#FFFFFF/100)
        // that must NOT be pushed to hardware — doing so on a cold helper start (Kill/Update) flashed
        // a full-brightness white before the widget synced the user's real values (#81 white stick flash).
        private bool _lightStateKnown;
        // Internal-convention light mode (0=Off,1=Solid,2=Pulse,3=Dynamic,4=Spiral) captured from the
        // b0:01 readback so a restore reflects the real hardware mode (including Off) instead of default Solid.
        private int _hwLightMode = -1;

        /// <summary>
        /// Gets the current performance mode (Quiet=1, Balanced=2, Performance=3, Custom=255).
        /// </summary>
        public int CurrentPerformanceMode => performanceMode;

        /// <summary>
        /// Gets the display name for a performance mode value.
        /// </summary>
        public static string GetPerformanceModeName(int mode)
        {
            return mode switch
            {
                1 => "Quiet Mode",
                2 => "Balanced Mode",
                3 => "Performance Mode",
                255 => "Custom",
                _ => $"Mode {mode}"
            };
        }
        private int customTDPSlow = 15;
        private int customTDPFast = 25;
        private int customTDPPeak = 35;

        // Raised right after a Custom SPL/SPPT/FPPT limit is successfully written to WMI (cache
        // updated). PerformanceManager subscribes to refresh the "Limits" readout (currentTdp)
        // immediately instead of waiting for the 2 s poll — the cache is the only reliable signal
        // of the applied limits, since the WMI getter returns preset values in Custom mode.
        public event System.Action CustomTDPApplied;

        private bool fanFullSpeed = false;
        private bool gyroEnabled = false;
        private int vibrationLevel = 2; // Medium
        private bool powerLightEnabled = true;

        // Controller remapping state (cached)
        private bool nintendoLayoutEnabled = false;
        private int vibrationMode = 1; // FPS

        // HID command timing - delay between commands for firmware to process
        private const int HID_COMMAND_DELAY_MS = 20; // Reduced from 50ms for faster profile switches

        // Controller battery monitoring (uses controllerService, not a separate connection)
        private int leftControllerBattery = -1;
        private int rightControllerBattery = -1;
        private bool leftControllerCharging = false;
        private bool rightControllerCharging = false;
        private bool leftControllerConnected = false;
        private bool rightControllerConnected = false;

        // Fan speed (RPM)
        private int cpuFanSpeed = 0;

        // Fan curve visibility (widget tells us when to push temp/RPM updates)
        private bool fanCurveVisible = false;

        /// <summary>
        /// True when the fan-curve UI panel is open in the widget. Read by
        /// PerformanceManager's consumer-check to keep CPU/VRM temps fresh while
        /// the user has the panel open.
        /// </summary>
        public bool IsFanCurveVisible => fanCurveVisible;

        // TDP reapply timer (used when switching to Custom mode)
        private System.Timers.Timer tdpReapplyTimer;
        private int pendingTdpSlow;
        private int pendingTdpFast;
        private int pendingTdpPeak;


        // Rate-limiting for fan speed refresh (WMI calls are CPU-expensive).
        // We refresh every FAN_SPEED_REFRESH_INTERVAL_MS when someone needs live fan
        // data (fan-curve UI open, OSD fan item visible, game running, sidebar open),
        // and back off to FAN_SPEED_IDLE_INTERVAL_MS when the helper is otherwise idle.
        private DateTime lastFanSpeedRefresh = DateTime.MinValue;
        private const int FAN_SPEED_REFRESH_INTERVAL_MS = 2000;
        private const int FAN_SPEED_IDLE_INTERVAL_MS = 10000;

        // Startup grace period to ignore LegionCustomTDP slider sync during widget startup
        // The main TDP slider calls SetCustomTDP which syncs all 3 values, so we don't want
        // the widget's stale cached values to override them
        private DateTime startupTime;
        private const int STARTUP_GRACE_PERIOD_MS = 5000; // 5 seconds after manager init

        // Performance mode debouncing to prevent queue buildup from rapid mode changes
        private System.Threading.Timer performanceModeDebounceTimer;
        private int pendingPerformanceMode = -1; // -1 means no pending mode
        private readonly object performanceModeDebounceLock = new object();
        private const int PERFORMANCE_MODE_DEBOUNCE_MS = 150;

        // Properties
        /// <summary>
        /// Gets the detected device information (manufacturer, model, features)
        /// </summary>
        public DeviceInfo DetectedDevice => deviceInfo ?? new DeviceInfo();

        public readonly LegionGoDetectedProperty LegionGoDetected;
        public readonly LegionTouchpadEnabledProperty LegionTouchpadEnabled;
        public readonly LegionLightModeProperty LegionLightMode;
        public readonly LegionLightColorProperty LegionLightColor;
        public readonly LegionLightBrightnessProperty LegionLightBrightness;
        public readonly LegionLightSpeedProperty LegionLightSpeed;
        public readonly LegionPerformanceModeProperty LegionPerformanceMode;

        public readonly LegionCustomTDPSlowProperty LegionCustomTDPSlow;
        public readonly LegionCustomTDPFastProperty LegionCustomTDPFast;
        public readonly LegionCustomTDPPeakProperty LegionCustomTDPPeak;
        public readonly LegionFanFullSpeedProperty LegionFanFullSpeed;
        public readonly LegionCPUCurrentTempProperty LegionCPUCurrentTemp;
        public readonly LegionFanSensorTempProperty LegionFanSensorTemp;
        public readonly LegionCPUFanRPMProperty LegionCPUFanRPM;
        public readonly LegionFanCurveVisibleProperty LegionFanCurveVisible;
        public readonly LegionGyroEnabledProperty LegionGyroEnabled;
        public readonly LegionVibrationProperty LegionVibration;
        public readonly LegionPowerLightProperty LegionPowerLight;

        // Controller remapping properties
        public readonly LegionButtonY1Property LegionButtonY1;
        public readonly LegionButtonY2Property LegionButtonY2;
        public readonly LegionButtonY3Property LegionButtonY3;
        public readonly LegionButtonM1Property LegionButtonM1;
        public readonly LegionButtonM2Property LegionButtonM2;
        public readonly LegionButtonM3Property LegionButtonM3;
        public readonly LegionButtonDesktopProperty LegionButtonDesktop;
        public readonly LegionButtonPageProperty LegionButtonPage;
        public readonly LegionNintendoLayoutProperty LegionNintendoLayout;
        public readonly LegionVibrationModeProperty LegionVibrationMode;
        public readonly LegionControllerProfileEnabledProperty LegionControllerProfileEnabled;

        // Gyro settings properties (per-game profile)
        public readonly LegionGyroTargetProperty LegionGyroTarget;
        public readonly LegionGyroSensitivityXProperty LegionGyroSensitivityX;
        public readonly LegionGyroSensitivityYProperty LegionGyroSensitivityY;
        public readonly LegionGyroInvertXProperty LegionGyroInvertX;
        public readonly LegionGyroInvertYProperty LegionGyroInvertY;
        public readonly LegionGyroMappingTypeProperty LegionGyroMappingType;
        public readonly LegionGyroActivationModeProperty LegionGyroActivationMode;
        public readonly LegionGyroActivationButtonProperty LegionGyroActivationButton;

        // Advanced gyro properties (per-game profile)
        public readonly LegionGyroDeadzoneProperty LegionGyroDeadzone;

        // Stick deadzone properties (per-game profile)
        public readonly LegionLeftStickDeadzoneProperty LegionLeftStickDeadzone;
        public readonly LegionRightStickDeadzoneProperty LegionRightStickDeadzone;

        // Trigger travel properties (per-game profile)
        public readonly LegionLeftTriggerStartProperty LegionLeftTriggerStart;
        public readonly LegionLeftTriggerEndProperty LegionLeftTriggerEnd;
        public readonly LegionRightTriggerStartProperty LegionRightTriggerStart;
        public readonly LegionRightTriggerEndProperty LegionRightTriggerEnd;
        public readonly LegionHairTriggersProperty LegionHairTriggers;

        // Touchpad vibration property (GLOBAL setting)
        public readonly LegionTouchpadVibrationProperty LegionTouchpadVibration;

        // Joystick as mouse properties (per-game profile)

        // Gamepad button mapping (per-game profile)
        public readonly LegionGamepadMappingProperty LegionGamepadMapping;

        // Desktop controls preset (per-game profile state tracking)

        // Controller battery properties
        public readonly ControllerBatteryLeftProperty ControllerBatteryLeft;
        public readonly ControllerBatteryRightProperty ControllerBatteryRight;
        public readonly ControllerChargingLeftProperty ControllerChargingLeft;
        public readonly ControllerChargingRightProperty ControllerChargingRight;
        public readonly ControllerConnectedLeftProperty ControllerConnectedLeft;
        public readonly ControllerConnectedRightProperty ControllerConnectedRight;
        public readonly ControllerVidPidProperty ControllerVidPid;
        public readonly LegionControllerModeProperty LegionControllerMode;
        public readonly ControllerDeviceStatusProperty ControllerDeviceStatus;

        // Device capability properties (for widget UI visibility)
        public readonly DeviceDisplayNameProperty DeviceDisplayName;
        public readonly DeviceSupportsControllerRemapProperty DeviceSupportsControllerRemap;
        public readonly DeviceSupportsRgbLightingProperty DeviceSupportsRgbLighting;
        public readonly DeviceSupportsGyroProperty DeviceSupportsGyro;
        public readonly DeviceHasScrollWheelProperty DeviceHasScrollWheel;
        public readonly DeviceHasDetachableControllersProperty DeviceHasDetachableControllers;
        public readonly DeviceHasTouchpadProperty DeviceHasTouchpad;

        // Reference to PerformanceManager for LibreHardwareMonitor sensor access
        private PerformanceManager performanceManager;

        /// <summary>
        /// Sets the PerformanceManager reference for CPU temperature sensor access.
        /// Called from Program.cs after both managers are initialized.
        /// </summary>
        public void SetPerformanceManager(PerformanceManager manager)
        {
            performanceManager = manager;
            Logger.Info($"PerformanceManager reference set, CPUTemperature sensor available: {manager?.CPUTemperature != null}");
        }

        public LegionManager() : base()
        {
            Logger.Info("Initializing Legion Manager...");
            var constructorTimer = System.Diagnostics.Stopwatch.StartNew();

            // Record startup time for grace period
            startupTime = DateTime.Now;

            // Try to detect Legion Go device
            var detectTimer = System.Diagnostics.Stopwatch.StartNew();
            DetectLegionGo();
            detectTimer.Stop();
            Logger.Info($"[TIMING] LegionManager.DetectLegionGo: {detectTimer.ElapsedMilliseconds}ms");

            // Touchpad: persist user's last choice across helper restarts. Prior
            // behavior used the hardcoded `true` default at LegionManager.cs:35
            // every boot, so users who turned the touchpad OFF would see the
            // widget toggle re-show as ON after restart even though hardware
            // stayed off (touchpad state is firmware-side persistent). The first
            // b0:01 readback further reconciles when something else (LegionSpace,
            // OS gesture toggle) changed the hardware state behind our back.
            if (Settings.LocalSettingsHelper.TryGetValue<bool>("LegionTouchpadEnabled", out bool savedTouchpadEnabled))
            {
                touchpadEnabled = savedTouchpadEnabled;
                Logger.Info($"LegionTouchpadEnabled loaded from settings: {touchpadEnabled}");
            }

            // Initialize properties (pass this as manager)
            LegionGoDetected = new LegionGoDetectedProperty(isLegionGoDetected, this);
            LegionTouchpadEnabled = new LegionTouchpadEnabledProperty(touchpadEnabled, this);
            LegionLightMode = new LegionLightModeProperty(lightMode, this);
            LegionLightColor = new LegionLightColorProperty(lightColor, this);
            LegionLightBrightness = new LegionLightBrightnessProperty(lightBrightness, this);
            LegionLightSpeed = new LegionLightSpeedProperty(lightSpeed, this);
            LegionPerformanceMode = new LegionPerformanceModeProperty(performanceMode, this);
            LegionCustomTDPSlow = new LegionCustomTDPSlowProperty(customTDPSlow, this);
            LegionCustomTDPFast = new LegionCustomTDPFastProperty(customTDPFast, this);
            LegionCustomTDPPeak = new LegionCustomTDPPeakProperty(customTDPPeak, this);
            LegionFanFullSpeed = new LegionFanFullSpeedProperty(fanFullSpeed, this);

            LegionCPUCurrentTemp = new LegionCPUCurrentTempProperty(0, this);
            LegionFanSensorTemp = new LegionFanSensorTempProperty(0, this);
            LegionCPUFanRPM = new LegionCPUFanRPMProperty(0, this);
            LegionFanCurveVisible = new LegionFanCurveVisibleProperty(false, this);

            LegionGyroEnabled = new LegionGyroEnabledProperty(gyroEnabled, this);
            LegionVibration = new LegionVibrationProperty(vibrationLevel, this);
            LegionPowerLight = new LegionPowerLightProperty(powerLightEnabled, this);

            // Battery charge limit (80%) was removed in 1.9.7c. The Legion Go 2 EC drops it at every
            // boot and GoTweaks used to re-apply it from a saved preference; without that it stays off.

            // Initialize controller remapping properties (JSON ButtonMapping format)
            LegionButtonY1 = new LegionButtonY1Property("", this);
            LegionButtonY2 = new LegionButtonY2Property("", this);
            LegionButtonY3 = new LegionButtonY3Property("", this);
            LegionButtonM1 = new LegionButtonM1Property("", this);
            LegionButtonM2 = new LegionButtonM2Property("", this);
            LegionButtonM3 = new LegionButtonM3Property("", this);
            LegionButtonDesktop = new LegionButtonDesktopProperty("", this);
            LegionButtonPage = new LegionButtonPageProperty("", this);
            LegionNintendoLayout = new LegionNintendoLayoutProperty(nintendoLayoutEnabled, this);
            LegionVibrationMode = new LegionVibrationModeProperty(vibrationMode, this);
            LegionControllerProfileEnabled = new LegionControllerProfileEnabledProperty(false, this);

            // Initialize gyro settings properties
            LegionGyroTarget = new LegionGyroTargetProperty(gyroTarget, this);
            LegionGyroSensitivityX = new LegionGyroSensitivityXProperty(gyroSensitivityX, this);
            LegionGyroSensitivityY = new LegionGyroSensitivityYProperty(gyroSensitivityY, this);
            LegionGyroInvertX = new LegionGyroInvertXProperty(gyroInvertX, this);
            LegionGyroInvertY = new LegionGyroInvertYProperty(gyroInvertY, this);
            LegionGyroMappingType = new LegionGyroMappingTypeProperty(gyroMappingType, this);
            LegionGyroActivationMode = new LegionGyroActivationModeProperty(gyroActivationMode, this);
            LegionGyroActivationButton = new LegionGyroActivationButtonProperty(gyroActivationButton, this);

            // Initialize advanced gyro properties
            LegionGyroDeadzone = new LegionGyroDeadzoneProperty(gyroDeadzone, this);

            // Initialize stick deadzone properties
            LegionLeftStickDeadzone = new LegionLeftStickDeadzoneProperty(leftStickDeadzone, this);
            LegionRightStickDeadzone = new LegionRightStickDeadzoneProperty(rightStickDeadzone, this);

            // Initialize trigger travel properties
            LegionLeftTriggerStart = new LegionLeftTriggerStartProperty(0, this);
            LegionLeftTriggerEnd = new LegionLeftTriggerEndProperty(0, this);
            LegionRightTriggerStart = new LegionRightTriggerStartProperty(0, this);
            LegionRightTriggerEnd = new LegionRightTriggerEndProperty(0, this);
            LegionHairTriggers = new LegionHairTriggersProperty(false, this);

            // Initialize touchpad vibration property (GLOBAL setting).
            // Touchpad vibration is the one Legion setting the WIDGET does not persist (it's not
            // part of any controller profile), so the helper owns its persistence: restore the
            // saved level before building the property (so BatchGet reports the real value, not the
            // hard default), then re-apply it to hardware below since the firmware resets on reboot.
            LoadTouchpadVibrationLevel();
            LegionTouchpadVibration = new LegionTouchpadVibrationProperty(touchpadVibrationLevel, this);
            try { SetTouchpadVibration(touchpadVibrationLevel); }
            catch (Exception ex) { Logger.Warn($"Touchpad vibration startup apply failed: {ex.Message}"); }

            // Initialize joystick as mouse properties (per-game profile)

            // Initialize gamepad button mapping property (per-game profile)
            LegionGamepadMapping = new LegionGamepadMappingProperty("", this);

            // Initialize desktop controls property (per-game profile state tracking)

            // Initialize controller battery properties
            ControllerBatteryLeft = new ControllerBatteryLeftProperty(-1, this);
            ControllerBatteryRight = new ControllerBatteryRightProperty(-1, this);
            ControllerChargingLeft = new ControllerChargingLeftProperty(false, this);
            ControllerChargingRight = new ControllerChargingRightProperty(false, this);
            ControllerConnectedLeft = new ControllerConnectedLeftProperty(false, this);
            ControllerConnectedRight = new ControllerConnectedRightProperty(false, this);
            ControllerVidPid = new ControllerVidPidProperty("", this);
            // Controller input mode (X-Input / D-Input). Starts from what the USB device list says
            // right now; kept current from the controller VID:PID (UpdateControllerVidPid).
            detectedControllerMode = isLegionGoDetected ? DetectControllerModeFromHid() : LegionControllerModes.Unknown;
            LegionControllerMode = new LegionControllerModeProperty(detectedControllerMode, this);
            ControllerDeviceStatus = new ControllerDeviceStatusProperty("", this);

            // Initialize device capability properties (for widget UI visibility)
            // Get display name and capabilities from the detected device config
            var deviceConfig = DeviceRegistry.GetByType(deviceInfo?.DeviceType ?? Shared.Enums.DeviceType.Generic);
            string displayName = deviceConfig?.DisplayName ?? "Legion Go Controller";
            bool supportsControllerRemap = deviceInfo?.SupportsControllerRemap ?? true;
            bool supportsRgbLighting = deviceInfo?.SupportsRgbLighting ?? true;
            bool supportsGyro = deviceInfo?.SupportsGyro ?? true;
            bool hasScrollWheel = deviceInfo?.HasScrollWheel ?? true;
            bool hasDetachableControllers = deviceInfo?.HasDetachableControllers ?? true;
            bool hasTouchpad = deviceInfo?.HasTouchpad ?? true;

            DeviceDisplayName = new DeviceDisplayNameProperty(displayName, this);
            DeviceSupportsControllerRemap = new DeviceSupportsControllerRemapProperty(supportsControllerRemap, this);
            DeviceSupportsRgbLighting = new DeviceSupportsRgbLightingProperty(supportsRgbLighting, this);
            DeviceSupportsGyro = new DeviceSupportsGyroProperty(supportsGyro, this);
            DeviceHasScrollWheel = new DeviceHasScrollWheelProperty(hasScrollWheel, this);
            DeviceHasDetachableControllers = new DeviceHasDetachableControllersProperty(hasDetachableControllers, this);
            DeviceHasTouchpad = new DeviceHasTouchpadProperty(hasTouchpad, this);

            Logger.Info($"Device capabilities - Name: {displayName}, ControllerRemap: {supportsControllerRemap}, RGB: {supportsRgbLighting}, Gyro: {supportsGyro}, ScrollWheel: {hasScrollWheel}, DetachableControllers: {hasDetachableControllers}, Touchpad: {hasTouchpad}");

            // NOTE: Battery monitoring is started from Program.cs AFTER widget connection
            // is established. Starting it here blocks the AppService connection.

            if (isLegionGoDetected)
            {
                // Read current performance mode and TDP values IN PARALLEL (with timeouts to prevent hangs)
                const int wmiReadTimeoutMs = 5000;
                var parallelTimer = System.Diagnostics.Stopwatch.StartNew();

                Logger.Info("Reading performance mode and TDP values in parallel...");
                var perfModeTask = Task.Run(() =>
                {
                    try { ReadCurrentPerformanceMode(); }
                    catch (Exception ex) { Logger.Warn($"ReadCurrentPerformanceMode exception: {ex.Message}"); }
                });
                var tdpTask = Task.Run(() =>
                {
                    try { ReadCurrentTDPValues(); }
                    catch (Exception ex) { Logger.Warn($"ReadCurrentTDPValues exception: {ex.Message}"); }
                });

                // Wait for both tasks to complete (or timeout)
                if (!Task.WaitAll(new[] { perfModeTask, tdpTask }, wmiReadTimeoutMs))
                {
                    Logger.Warn($"WMI reads timed out after {wmiReadTimeoutMs}ms");
                }
                parallelTimer.Stop();
                Logger.Info($"[TIMING] LegionManager.ReadPerformanceMode+TDP (parallel): {parallelTimer.ElapsedMilliseconds}ms");

                // Update properties with the values read from device
                // Use silent update to avoid triggering WMI calls back
                LegionPerformanceMode.SetValueSilent(performanceMode);
                LegionCustomTDPSlow.SetValueSilent(customTDPSlow);
                LegionCustomTDPFast.SetValueSilent(customTDPFast);
                LegionCustomTDPPeak.SetValueSilent(customTDPPeak);
                LegionFanFullSpeed.SetValueSilent(fanFullSpeed);

                // Custom mode runs on the fan table GoTweaks writes: Lenovo's stock curve (1.9.7c).
                ApplyStockFanCurveIfCustom("startup");
            }

            constructorTimer.Stop();
            Logger.Info($"[TIMING] LegionManager constructor total: {constructorTimer.ElapsedMilliseconds}ms");
            Logger.Info($"Legion Manager initialized. Legion Go detected: {isLegionGoDetected}");
        }

        private void DetectLegionGo()
        {
            try
            {
                var stepTimer = System.Diagnostics.Stopwatch.StartNew();

                // Step 1: Use DeviceDetector to get device info from WMI (Manufacturer, Model)
                deviceInfo = DeviceDetector.DetectDevice();
                Logger.Info($"[TIMING] DeviceDetector.DetectDevice: {stepTimer.ElapsedMilliseconds}ms");
                stepTimer.Restart();

                // Check if device is a known Legion model
                if (deviceInfo.IsLegionDevice)
                {
                    isLegionGoDetected = true;
                    Logger.Info($"Legion device detected via DeviceDetector: {deviceInfo.DeviceType} ({deviceInfo.Model})");
                }

                // If debug mode is active and device is NOT Legion, skip hardware fallback checks
                // This allows testing non-Legion devices on actual Legion hardware
                bool skipHardwareFallbacks = DeviceDetector.IsDebugModeActive && !deviceInfo.IsLegionDevice;
                if (skipHardwareFallbacks)
                {
                    Logger.Warn("DEBUG MODE: Skipping Legion hardware fallback checks (non-Legion device override active)");
                    deviceInfo.SupportsWmiTdp = false;
                    deviceInfo.SupportsControllerRemap = false;
                }
                else
                {
                    // Step 2: Validate WMI capability (even if model matches, verify GAMEZONE WMI works)
                    wmiService = new LenovoWMIService();
                    Logger.Info($"[TIMING] LenovoWMIService created: {stepTimer.ElapsedMilliseconds}ms");
                    stepTimer.Restart();

                    // Try GetSmartFanMode - if it works, this device supports Legion WMI TDP control
                    var fanModeResult = wmiService.GetSmartFanMode();
                    Logger.Info($"[TIMING] GetSmartFanMode (WMI check): {stepTimer.ElapsedMilliseconds}ms");
                    stepTimer.Restart();

                    if (fanModeResult.Success)
                    {
                        isLegionGoDetected = true;
                        deviceInfo.SupportsWmiTdp = true;
                        Logger.Info($"Legion WMI TDP control confirmed (SmartFanMode={fanModeResult.Result})");
                    }
                    else if (deviceInfo.IsLegionDevice)
                    {
                        // Model matches but WMI failed - log warning but keep detected
                        Logger.Warn($"Legion device model detected but WMI TDP control unavailable");
                        deviceInfo.SupportsWmiTdp = false;
                    }

                    // Step 3: Try to connect to controller service (for button remapping, RGB, etc.)
                    controllerService = new LegionControllerService();
                    Logger.Info($"[TIMING] LegionControllerService created: {stepTimer.ElapsedMilliseconds}ms");
                    stepTimer.Restart();

                    var connectResult = controllerService.Connect();
                    Logger.Info($"[TIMING] Controller Connect: {stepTimer.ElapsedMilliseconds}ms");

                    if (connectResult.Success)
                    {
                        isControllerConnected = true;
                        isLegionGoDetected = true;
                        // Don't override SupportsControllerRemap here - respect the DeviceConfig value
                        // Legion Go S has different HID structure that doesn't support remapping even if controller connects
                        if (deviceInfo.SupportsControllerRemap)
                        {
                            Logger.Info($"Legion Go controller connected: {connectResult.Message}");
                        }
                        else
                        {
                            Logger.Info($"Legion Go controller connected but remapping not supported for this device variant: {connectResult.Message}");
                        }
                    }
                    else
                    {
                        // Only disable if it was expected to be supported
                        if (deviceInfo.SupportsControllerRemap)
                        {
                            deviceInfo.SupportsControllerRemap = false;
                            Logger.Info($"Legion Go controller not connected: {connectResult.Message}");
                        }
                        else
                        {
                            Logger.Info($"Legion Go controller not connected (not supported for this device): {connectResult.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error detecting Legion Go: {ex.Message}");
                isLegionGoDetected = false;
                if (deviceInfo == null)
                {
                    deviceInfo = new DeviceInfo();
                }
            }
        }

        private void ReadCurrentPerformanceMode()
        {
            try
            {
                if (wmiService != null)
                {
                    var result = wmiService.GetSmartFanMode();
                    if (result.Success && result.Result.HasValue)
                    {
                        performanceMode = (int)result.Result.Value;
                        Logger.Info($"Current performance mode: {result.Result.Value}");
                    }

                    var fanFullResult = wmiService.GetFanFullSpeed();
                    if (fanFullResult.Success && fanFullResult.Result.HasValue)
                    {
                        fanFullSpeed = fanFullResult.Result.Value == 1;
                        Logger.Info($"Fan full speed: {fanFullSpeed}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error reading performance mode: {ex.Message}");
            }
        }

        private void ReadCurrentTDPValues()
        {
            try
            {
                if (wmiService != null)
                {
                    var slowResult = wmiService.GetCPUShortTermPowerLimit();
                    if (slowResult.Success && slowResult.Result.HasValue)
                    {
                        customTDPSlow = slowResult.Result.Value;
                        Logger.Info($"Current Slow TDP (SPL): {customTDPSlow}W");
                    }

                    var fastResult = wmiService.GetCPULongTermPowerLimit();
                    if (fastResult.Success && fastResult.Result.HasValue)
                    {
                        customTDPFast = fastResult.Result.Value;
                        Logger.Info($"Current Fast TDP (SPPL): {customTDPFast}W");
                    }

                    var peakResult = wmiService.GetCPUPeakPowerLimit();
                    if (peakResult.Success && peakResult.Result.HasValue)
                    {
                        customTDPPeak = peakResult.Result.Value;
                        Logger.Info($"Current Peak TDP (FPPT): {customTDPPeak}W");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error reading TDP values: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the current TDP values.
        /// When in Custom mode (255), returns our cached values since WMI returns preset values.
        /// Returns (slow/SPL, fast/SPPL, peak/FPPT) or null values if not available.
        /// </summary>
        public (int? slow, int? fast, int? peak) GetCurrentTDPValues()
        {
            // In Custom mode, return our cached values since WMI GetFeatureValue
            // returns the preset TDP limits, not the custom values we've applied
            if (performanceMode == 255)
            {
                return (customTDPSlow, customTDPFast, customTDPPeak);
            }

            int? slow = null, fast = null, peak = null;

            try
            {
                if (wmiService != null)
                {
                    var slowResult = wmiService.GetCPUShortTermPowerLimit();
                    if (slowResult.Success && slowResult.Result.HasValue)
                    {
                        slow = slowResult.Result.Value;
                    }

                    var fastResult = wmiService.GetCPULongTermPowerLimit();
                    if (fastResult.Success && fastResult.Result.HasValue)
                    {
                        fast = fastResult.Result.Value;
                    }

                    var peakResult = wmiService.GetCPUPeakPowerLimit();
                    if (peakResult.Success && peakResult.Result.HasValue)
                    {
                        peak = peakResult.Result.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting TDP values: {ex.Message}");
            }

            return (slow, fast, peak);
        }

        public void SetTouchpadEnabled(bool enabled)
        {
            if (!isControllerConnected || controllerService == null)
            {
                Logger.Warn("Cannot set touchpad: controller not connected");
                return;
            }

            try
            {
                var result = controllerService.SetTouchpadEnabled(enabled);
                if (result.Success)
                {
                    touchpadEnabled = enabled;
                    try { Settings.LocalSettingsHelper.SetValue("LegionTouchpadEnabled", enabled); }
                    catch (Exception persistEx) { Logger.Debug($"Failed to persist LegionTouchpadEnabled: {persistEx.Message}"); }
                    Logger.Info($"Touchpad {(enabled ? "enabled" : "disabled")}");
                }
                else
                {
                    Logger.Error($"Failed to set touchpad: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting touchpad: {ex.Message}");
            }
        }

        public void SetLightMode(int mode)
        {
            // Check if we have a valid controller for RGB
            bool hasStandardController = isControllerConnected && controllerService != null;

            if (!hasStandardController)
            {
                Logger.Warn("Cannot set light mode: no RGB controller connected");
                return;
            }

            try
            {
                // Parse current color
                ParseHexColor(lightColor, out byte r, out byte g, out byte b);

                // Use Go S controller if available (Legion Go S uses different HID protocol)
                // Standard Legion Go controller. Widget sends mode=0 to mean "Off" but
                // RgbMode enum starts at Solid=1, so casting (RgbMode)0 produces an
                // invalid byte that the firmware silently rejects (and leaves the light
                // in whatever state it was previously). Route mode=0 through SetRgbEnabled
                // for both controllers, mirroring the Go S branch above.
                bool success;
                if (mode == 0)
                {
                    var leftOff = controllerService.SetRgbEnabled(Controller.Left, false);
                    var rightOff = controllerService.SetRgbEnabled(Controller.Right, false);
                    success = leftOff.Success && rightOff.Success;
                    if (success)
                    {
                        lightMode = mode;
                        Logger.Info("Light mode set to Off (SetRgbEnabled false on both controllers)");
                    }
                    else
                    {
                        Logger.Error($"Failed to disable stick lights: L={leftOff.Message}, R={rightOff.Message}");
                    }
                }
                else
                {
                    // Re-enable the light first in case it was previously off — writing
                    // a profile alone doesn't re-enable a disabled light per the firmware.
                    controllerService.SetRgbEnabled(Controller.Left, true);
                    controllerService.SetRgbEnabled(Controller.Right, true);

                    RgbMode rgbMode = (RgbMode)mode;
                    var result = controllerService.SetStickLightMode(rgbMode, r, g, b, lightBrightness / 100f, lightSpeed / 100f);
                    success = result.Success;
                    if (success)
                    {
                        lightMode = mode;
                        Logger.Info($"Light mode set to {rgbMode}");
                    }
                    else
                    {
                        Logger.Error($"Failed to set light mode: {result.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting light mode: {ex.Message}");
            }
            // Re-read hardware state so the Info card reflects the new mode promptly.
            RequestDeviceStatusRefresh();
        }

        public void RestoreLightSettings()
        {
            // Don't push light to hardware until we actually know the light state from a real source
            // (b0:01 hardware readback or a widget/profile sync). On a cold helper start (Kill/Update)
            // the light fields still hold uninitialized defaults (mode=Solid, #FFFFFF, 100%); applying
            // those blasted a full-brightness white flash before the widget synced the user's real
            // values ~9s later (#81 white stick flash). Reactive-disabled init and gyro LED revert both
            // route through here, so this gate covers every restore caller.
            if (!_lightStateKnown)
            {
                Logger.Info("RestoreLightSettings skipped: light state not yet known (avoiding default white flash).");
                return;
            }

            // Sync lightColor from the property value (widget is source of truth).
            // The cached lightColor field may still be the default #FFFFFF if SetLightColor
            // was never called (e.g., _hasUserModified check skipped it, or HasExplicitLighting was false).
            string propertyColor = LegionLightColor?.Value;
            if (!string.IsNullOrEmpty(propertyColor) && propertyColor != lightColor)
            {
                Logger.Info($"RestoreLightSettings: updating cached color from {lightColor} to {propertyColor} (from property)");
                lightColor = propertyColor;
            }

            // If the widget hasn't synced yet but the b0:01 readback knows the true hardware
            // brightness/color, prefer those over the stale defaults (mode=Solid/100/#FFFFFF) so a
            // restore can't flash full-brightness white before sync (#81 brightness flash).
            if (_hasHwLightState)
            {
                if (lightBrightness == 100 && _hwLightBrightness != 100) lightBrightness = _hwLightBrightness;
                if (lightColor == "#FFFFFF")
                {
                    lightColor = $"#{_hwLightColorR:X2}{_hwLightColorG:X2}{_hwLightColorB:X2}";
                }
            }

            // Don't sync lightMode from the b0:01 readback here: during a reactive effect the
            // helper writes Solid+color frames to hardware, so b0:01 reports mode=Solid even when
            // the user's saved static mode is Off. Overriding lightMode with hw mode at release
            // time would re-enable the light to Solid instead of returning to Off. The widget's
            // sync of LegionLightMode is the authoritative source for the user's static mode.

            Logger.Info($"Restoring light settings: mode={lightMode}, color={lightColor}, brightness={lightBrightness}");
            SetLightMode(lightMode);
        }

        /// <summary>
        /// Set both controllers to a SOLID color immediately, used by the reactive-lighting
        /// effect loop (flash on press, etc). Routes through the same LegionControllerService /
        /// Go S path as the static lighting so there's a single RGB writer. Does NOT touch the
        /// persisted lightMode/lightColor — call <see cref="ReleaseReactiveLighting"/> to return
        /// to the user's static setting. Keep callers throttled (~60Hz); the service serializes
        /// HID writes internally.
        /// </summary>
        public void SetReactiveStickColor(byte r, byte g, byte b, int brightnessPercent)
        {
            bool hasStandardController = isControllerConnected && controllerService != null;
            if (!hasStandardController) return;

            int bright = Math.Max(0, Math.Min(100, brightnessPercent));
            // Preserve the user's configured speed in the profile. For a Solid reactive frame
            // speed has no visible effect (no animation), but it's still written to the profile
            // and read back by b0:01 — hardcoding 0.5 made the Info card report speed 50%.
            float speed = Math.Max(0, Math.Min(100, lightSpeed)) / 100f;
            try
            {
                // The static light may be Off (RGB disabled at firmware level); writing a
                // profile alone won't re-enable it. Enable first so reactive flashes show
                // even when the user's static mode is Off. ReleaseReactiveLighting ->
                // RestoreLightSettings will disable it again when the effect ends if Off.
                if (lightMode == 0)
                {
                    controllerService.SetRgbEnabled(Controller.Left, true);
                    controllerService.SetRgbEnabled(Controller.Right, true);
                }
                controllerService.SetStickLightMode(RgbMode.Solid, r, g, b, bright / 100f, speed);
            }
            catch (Exception ex) { Logger.Debug($"SetReactiveStickColor failed: {ex.Message}"); }
        }

        /// <summary>Return the controllers to the user's persisted static lighting after a
        /// reactive effect ends (or when reactive lighting is disabled).</summary>
        public void ReleaseReactiveLighting()
        {
            try { RestoreLightSettings(); }
            catch (Exception ex) { Logger.Debug($"ReleaseReactiveLighting failed: {ex.Message}"); }
        }

        /// <summary>Brightness (0-100) for the reactive loop. Prefers the true hardware value from
        /// the b0:01 readback over the helper's stale default (which is 100 after a restart and
        /// would make reactive effects peak at full brightness before the widget reconnects).</summary>
        public int CurrentLightBrightness => _hasHwLightState ? _hwLightBrightness : lightBrightness;

        /// <summary>Current static color (parsed RGB), so the reactive fade can blend toward it
        /// instead of toward black — avoids a visible snap back at the end of a flash. Prefers the
        /// hardware readback color; when static mode is Off, returns black so flashes fade fully out.</summary>
        public (byte r, byte g, byte b) CurrentLightColorRgb
        {
            get
            {
                if (lightMode == 0) return (0, 0, 0);
                if (_hasHwLightState) return (_hwLightColorR, _hwLightColorG, _hwLightColorB);
                try { ParseHexColor(lightColor, out byte r, out byte g, out byte b); return (r, g, b); }
                catch { return (0, 0, 0); }
            }
        }


        public void SetLightColor(string hexColor)
        {
            // An external color set is a real source of truth for the light state.
            _lightStateKnown = true;

            bool hasStandardController = isControllerConnected && controllerService != null;

            if (!hasStandardController)
            {
                Logger.Warn("Cannot set light color: no RGB controller connected");
                return;
            }

            try
            {
                ParseHexColor(hexColor, out byte r, out byte g, out byte b);

                // When the user has the light mode set to Off (lightMode=0), don't
                // push a profile — that would write an invalid (RgbMode)0 byte and
                // leak through to the firmware as garbage. Just remember the color
                // so the next mode change (back to Solid/Pulse/etc.) can apply it.
                if (lightMode == 0)
                {
                    lightColor = hexColor;
                    Logger.Info($"Light color cached as {hexColor} (mode is Off, not pushing profile)");
                }
                else
                {
                    RgbMode rgbMode = (RgbMode)lightMode;
                    var result = controllerService.SetStickLightMode(rgbMode, r, g, b, lightBrightness / 100f, lightSpeed / 100f);
                    if (result.Success)
                    {
                        lightColor = hexColor;
                        Logger.Info($"Light color set to {hexColor}");
                    }
                    else
                    {
                        Logger.Error($"Failed to set light color: {result.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting light color: {ex.Message}");
            }
            // Re-read hardware state so the Info card reflects the new color/mode promptly.
            RequestDeviceStatusRefresh();
        }

        private void ParseHexColor(string hexColor, out byte r, out byte g, out byte b)
        {
            // Default to white
            r = 255; g = 255; b = 255;

            if (string.IsNullOrEmpty(hexColor)) return;

            // Remove # if present
            string hex = hexColor.TrimStart('#');

            if (hex.Length == 6)
            {
                r = Convert.ToByte(hex.Substring(0, 2), 16);
                g = Convert.ToByte(hex.Substring(2, 2), 16);
                b = Convert.ToByte(hex.Substring(4, 2), 16);
            }
        }

        public void SetLightBrightness(int brightness)
        {
            // An external brightness set is a real source of truth for the light state.
            _lightStateKnown = true;

            bool hasStandardController = isControllerConnected && controllerService != null;

            if (!hasStandardController)
            {
                Logger.Warn("Cannot set light brightness: no RGB controller connected");
                return;
            }

            try
            {
                brightness = Math.Max(0, Math.Min(100, brightness));
                ParseHexColor(lightColor, out byte r, out byte g, out byte b);

                // Skip the wire push when the light is in Off mode — writing
                // (RgbMode)0 is invalid and the firmware will reject or
                // misinterpret it. Just cache for the next mode change.
                if (lightMode == 0)
                {
                    lightBrightness = brightness;
                    Logger.Info($"Light brightness cached as {brightness}% (mode is Off, not pushing profile)");
                }
                else
                {
                    RgbMode rgbMode = (RgbMode)lightMode;
                    var result = controllerService.SetStickLightMode(rgbMode, r, g, b, brightness / 100f, lightSpeed / 100f);
                    if (result.Success)
                    {
                        lightBrightness = brightness;
                        Logger.Info($"Light brightness set to {brightness}%");
                    }
                    else
                    {
                        Logger.Error($"Failed to set light brightness: {result.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting light brightness: {ex.Message}");
            }
        }

        public void SetLightSpeed(int speed)
        {
            // An external speed set is a real source of truth for the light state.
            _lightStateKnown = true;

            bool hasStandardController = isControllerConnected && controllerService != null;

            if (!hasStandardController)
            {
                Logger.Warn("Cannot set light speed: no RGB controller connected");
                return;
            }

            try
            {
                speed = Math.Max(0, Math.Min(100, speed));
                ParseHexColor(lightColor, out byte r, out byte g, out byte b);

                if (lightMode == 0)
                {
                    lightSpeed = speed;
                    Logger.Info($"Light speed cached as {speed}% (mode is Off, not pushing profile)");
                }
                else
                {
                    RgbMode rgbMode = (RgbMode)lightMode;
                    var result = controllerService.SetStickLightMode(rgbMode, r, g, b, lightBrightness / 100f, speed / 100f);
                    if (result.Success)
                    {
                        lightSpeed = speed;
                        Logger.Info($"Light speed set to {speed}%");
                    }
                    else
                    {
                        Logger.Error($"Failed to set light speed: {result.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting light speed: {ex.Message}");
            }
        }

        public void SetPerformanceMode(int mode)
        {
            // Debounce rapid mode changes to prevent queue buildup
            int oldMode;
            bool modeActuallyChanged;
            lock (performanceModeDebounceLock)
            {
                oldMode = performanceMode;
                modeActuallyChanged = oldMode != mode;
                pendingPerformanceMode = mode;

                // Update cached mode immediately so SetCustomTDP knows our intended mode
                // This prevents race conditions where TDP is skipped because mode hasn't changed yet
                performanceMode = mode;

                // Cancel existing timer and start a new one
                performanceModeDebounceTimer?.Dispose();
                performanceModeDebounceTimer = new System.Threading.Timer(
                    _ => ApplyPendingPerformanceMode(),
                    null,
                    PERFORMANCE_MODE_DEBOUNCE_MS,
                    System.Threading.Timeout.Infinite // Don't repeat
                );

                Logger.Debug($"SetPerformanceMode: Debouncing mode change to {mode} (will apply in {PERFORMANCE_MODE_DEBOUNCE_MS}ms if no new changes)");
            }

            // Per-mode fan curve swap fires on every actual mode transition (not on no-op
            // "set to current" calls). Independent from the WMI debounce that gates
            // SetSmartFanMode rapid-fire suppression — we want the active curve to swap
            // immediately.
            if (modeActuallyChanged)
            {
                OnPerformanceModeChanged(oldMode, mode);
            }
        }

        /// <summary>
        /// Called by the debounce timer to apply the pending performance mode.
        /// </summary>
        private void ApplyPendingPerformanceMode()
        {
            int modeToApply;
            lock (performanceModeDebounceLock)
            {
                if (pendingPerformanceMode < 0)
                {
                    return; // No pending mode
                }
                modeToApply = pendingPerformanceMode;
                pendingPerformanceMode = -1; // Clear pending
            }

            ApplyPerformanceModeInternal(modeToApply);
        }

        /// <summary>
        /// Internal method that actually applies the performance mode via WMI.
        /// Called after debouncing completes.
        /// Note: performanceMode is already set optimistically in SetPerformanceMode.
        /// We only need to revert it here if the WMI call fails.
        /// </summary>
        private void ApplyPerformanceModeInternal(int mode)
        {
            if (wmiService == null)
            {
                Logger.Warn("Cannot set performance mode: WMI service not available");
                return;
            }

            try
            {
                TdpMode tdpMode = (TdpMode)mode;
                var result = wmiService.SetSmartFanMode(tdpMode);
                if (result.Success)
                {
                    // Mode was already set optimistically, just log success
                    Logger.Info($"Performance mode set to {tdpMode}");

                    // NOTE: We do NOT reset the fan curve when switching to preset modes.
                    // The hardware's preset modes (Quiet, Balanced, Performance) have their own
                    // built-in fan curves that automatically take effect when SetSmartFanMode is called.
                    // Setting any fan curve here would override the hardware's preset behavior.
                    // The custom fan curve only applies when in Custom mode (255).

                    // Entering Custom: the firmware resets SPL/SPPT/FPPT to its own defaults when
                    // it switches into Custom mode. The widget pushes the three limits via the
                    // individual ApplyCustomTDP* messages, but those land BEFORE this mode-switch
                    // WMI call (mode is debounced 150ms; sliders are sent immediately), so the
                    // firmware reset wins and the console ends up at ~15W. Reapply the cached
                    // limits once the hardware confirms Custom so they stick.
                    if (mode == 255)
                    {
                        ReapplyCustomTDPAfterModeSwitch();
                    }
                }
                else
                {
                    Logger.Error($"Failed to set performance mode: {result.Message}");
                    // Note: We don't revert performanceMode here because rapid toggling
                    // means the cached value might be for a different pending change
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting performance mode: {ex.Message}");
            }
        }

        /// <summary>
        /// Reapplies the cached SPL/SPPT/FPPT after the hardware confirms it has entered Custom
        /// mode. Entering Custom makes the firmware reset the three limits to its own defaults
        /// (~15W), which would otherwise clobber the values the widget pushed moments earlier.
        /// Mirrors the confirm-then-apply guard in <see cref="SetCustomTDP"/> and schedules a
        /// second delayed reapply as a safety net against late firmware settling.
        /// Runs on the performance-mode debounce timer thread, so the short blocking poll is fine.
        /// </summary>
        /// <summary>
        /// Stores the Custom SPL/SPPT/FPPT to use the next time Custom is entered, without writing
        /// them (only Custom mode takes them; the firmware manages the preset modes' limits).
        /// </summary>
        public void RememberCustomTDP(int slow, int fast, int peak)
        {
            customTDPSlow = slow;
            customTDPFast = fast;
            customTDPPeak = peak;
        }

        private void ReapplyCustomTDPAfterModeSwitch()
        {
            if (wmiService == null) return;

            int slow = customTDPSlow;
            int fast = customTDPFast;
            int peak = customTDPPeak;

            // Wait for the firmware to actually report Custom before writing limits — the WMI
            // SetSmartFanMode call returns fast but the hardware takes ~400ms to switch (and to
            // perform its default-limit reset). Writing before that completes would be wiped.
            const int maxAttempts = 10; // ~500ms max wait
            const int delayMs = 50;
            for (int i = 0; i < maxAttempts; i++)
            {
                // A newer mode change may have superseded this one (e.g. user toggled away again).
                // Bail out so we don't force Custom limits onto a preset mode.
                if (performanceMode != 255)
                {
                    Logger.Info($"Skipping Custom TDP reapply - mode changed to {performanceMode} during confirm wait");
                    return;
                }

                var modeResult = wmiService.GetSmartFanMode();
                if (modeResult.Success && modeResult.Result == TdpMode.Custom)
                {
                    Logger.Info($"Hardware confirmed Custom mode after {(i + 1) * delayMs}ms - reapplying limits SPL={slow}W, SPPT={fast}W, FPPT={peak}W");
                    break;
                }
                System.Threading.Thread.Sleep(delayMs);
            }

            if (performanceMode != 255) return;

            ApplyTDPValues(slow, fast, peak);
            // Safety net: some firmware revisions finish the default-limit reset slightly after
            // they report Custom. A single delayed reapply ensures the user's values win.
            ScheduleTDPReapply(slow, fast, peak);
        }

        public void SetCustomTDP(int slow, int fast, int peak)
        {
            if (wmiService == null)
            {
                Logger.Warn("Cannot set custom TDP: WMI service not available");
                return;
            }

            try
            {
                // Only apply TDP if in Custom mode (255)
                // Don't automatically switch modes - preset modes manage TDP via hardware
                // The widget should explicitly switch to Custom mode first if needed
                if (performanceMode != 255)
                {
                    Logger.Info($"Skipping SetCustomTDP({slow}, {fast}, {peak}) - not in Custom mode (current mode: {performanceMode})");
                    return;
                }

                // If there's a pending Custom mode change waiting on debounce, apply it immediately
                // This ensures the hardware is in Custom mode before we try to set TDP values
                bool flushedModeChange = false;
                lock (performanceModeDebounceLock)
                {
                    if (pendingPerformanceMode == 255)
                    {
                        Logger.Info("Flushing pending Custom mode change before applying TDP");
                        performanceModeDebounceTimer?.Dispose();
                        performanceModeDebounceTimer = null;
                        pendingPerformanceMode = -1;

                        // Apply mode change synchronously
                        TdpMode tdpMode = (TdpMode)255;
                        var modeResult = wmiService.SetSmartFanMode(tdpMode);
                        if (modeResult.Success)
                        {
                            Logger.Info($"Performance mode set to {tdpMode}");
                            flushedModeChange = true;
                        }
                        else
                        {
                            Logger.Error($"Failed to set performance mode: {modeResult.Message}");
                            return; // Don't try to set TDP if mode change failed
                        }
                    }
                }

                // If we just flushed a mode change, wait for hardware to confirm it's in Custom mode
                // The WMI call returns quickly but hardware takes ~400ms to actually switch modes
                if (flushedModeChange)
                {
                    const int maxAttempts = 10; // ~500ms max wait
                    const int delayMs = 50;
                    bool hardwareInCustomMode = false;

                    for (int i = 0; i < maxAttempts; i++)
                    {
                        var result = wmiService.GetSmartFanMode();
                        if (result.Success && result.Result == TdpMode.Custom)
                        {
                            hardwareInCustomMode = true;
                            Logger.Info($"Hardware confirmed Custom mode after {(i + 1) * delayMs}ms");
                            break;
                        }
                        System.Threading.Thread.Sleep(delayMs);
                    }

                    if (!hardwareInCustomMode)
                    {
                        Logger.Warn("Hardware did not confirm Custom mode within 500ms, applying TDP anyway and scheduling reapply");
                        // Apply TDP values now and schedule a reapply to ensure they stick
                        ApplyTDPValues(slow, fast, peak);
                        ScheduleTDPReapply(slow, fast, peak);
                        return;
                    }
                }

                // Apply TDP values
                ApplyTDPValues(slow, fast, peak);

                // Keep the helper's internal cache (customTDPSlow/Fast/Peak via these properties) in
                // sync with the applied values, but do NOT SyncToRemote: the widget owns the Custom
                // limits (it drives them live from the TDP / SPPT Boost / FPPT Boost sliders), so the
                // helper never needs to push them back. The cache feeds ReassertCustomTDP, the OSD
                // current-TDP readout and GetCurrentTDPValues.
                LegionCustomTDPSlow.SetValueSilent(slow);
                LegionCustomTDPFast.SetValueSilent(fast);
                LegionCustomTDPPeak.SetValueSilent(peak);
                Logger.Info($"Synced Legion Custom TDP cache (internal): Slow={slow}W, Fast={fast}W, Peak={peak}W");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting custom TDP: {ex.Message}");
            }
        }

        /// <summary>True when the helper is in Custom performance mode (255).</summary>
        public bool IsInCustomMode => performanceMode == 255;

        /// <summary>
        /// Re-pushes the cached Custom SPL/SPPT/FPPT limits to hardware. Used by the master-TDP
        /// reapply paths (ReapplyTDP / power-source change / global-profile apply) which carry a
        /// SINGLE master TDP value — on Legion that value must NOT collapse the three independent
        /// Custom limits to v/v/v. Re-asserts the cached triplet instead. No-op outside Custom mode
        /// (preset modes are firmware-managed).
        /// </summary>
        public void ReassertCustomTDP()
        {
            if (performanceMode != 255) return;
            Logger.Info($"Reasserting cached Custom TDP limits: SPL={customTDPSlow}W, SPPT={customTDPFast}W, FPPT={customTDPPeak}W");
            SetCustomTDP(customTDPSlow, customTDPFast, customTDPPeak);
        }

        /// <summary>
        /// Applies TDP values via WMI with rollback on partial failure.
        /// All three values are applied atomically - if any fails, previous values are restored.
        /// </summary>
        private void ApplyTDPValues(int slow, int fast, int peak)
        {
            // Store previous values for rollback on partial failure
            int previousSlow = customTDPSlow;
            int previousFast = customTDPFast;
            int previousPeak = customTDPPeak;

            // Step 1: Set Slow TDP (SPL)
            var slowResult = wmiService.SetCPUShortTermPowerLimit(slow);
            if (!slowResult.Success)
            {
                Logger.Error($"Failed to set Slow TDP: {slowResult.Message}");
                return; // Nothing to rollback yet
            }
            Logger.Info($"Slow TDP (SPL) set to {slow}W");

            // Step 2: Set Fast TDP (SPPL)
            var fastResult = wmiService.SetCPULongTermPowerLimit(fast);
            if (!fastResult.Success)
            {
                Logger.Error($"Failed to set Fast TDP: {fastResult.Message}");
                // Rollback Slow TDP
                Logger.Warn($"Rolling back Slow TDP to {previousSlow}W due to Fast TDP failure");
                wmiService.SetCPUShortTermPowerLimit(previousSlow);
                return;
            }
            Logger.Info($"Fast TDP (SPPL) set to {fast}W");

            // Step 3: Set Peak TDP (FPPT)
            var peakResult = wmiService.SetCPUPeakPowerLimit(peak);
            if (!peakResult.Success)
            {
                Logger.Error($"Failed to set Peak TDP: {peakResult.Message}");
                // Rollback both Slow and Fast TDP
                Logger.Warn($"Rolling back Slow TDP to {previousSlow}W and Fast TDP to {previousFast}W due to Peak TDP failure");
                wmiService.SetCPUShortTermPowerLimit(previousSlow);
                wmiService.SetCPULongTermPowerLimit(previousFast);
                return;
            }
            Logger.Info($"Peak TDP (FPPT) set to {peak}W");

            // All succeeded - update cached values
            customTDPSlow = slow;
            customTDPFast = fast;
            customTDPPeak = peak;
            Logger.Info($"All TDP values applied successfully: SPL={slow}W, SPPL={fast}W, FPPT={peak}W");
            CustomTDPApplied?.Invoke();
        }

        /// <summary>
        /// Schedules a TDP reapply after 5 seconds (used when switching to Custom mode)
        /// </summary>
        private void ScheduleTDPReapply(int slow, int fast, int peak)
        {
            // Store pending values
            pendingTdpSlow = slow;
            pendingTdpFast = fast;
            pendingTdpPeak = peak;

            // Cancel any existing timer
            if (tdpReapplyTimer != null)
            {
                tdpReapplyTimer.Stop();
                tdpReapplyTimer.Dispose();
            }

            // Create new timer for 5 seconds
            tdpReapplyTimer = new System.Timers.Timer(5000);
            tdpReapplyTimer.AutoReset = false;
            tdpReapplyTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    Logger.Info($"Reapplying TDP after mode change: SPL={pendingTdpSlow}W, SPPL={pendingTdpFast}W, FPPT={pendingTdpPeak}W");
                    ApplyTDPValues(pendingTdpSlow, pendingTdpFast, pendingTdpPeak);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error reapplying TDP: {ex.Message}");
                }
                finally
                {
                    tdpReapplyTimer?.Dispose();
                    tdpReapplyTimer = null;
                }
            };
            tdpReapplyTimer.Start();
            Logger.Info("Scheduled TDP reapply in 5 seconds");
        }

        /// <summary>
        /// Apply individual Slow TDP (SPL) value
        /// </summary>
        public void ApplyCustomTDPSlow(int slow)
        {
            // Only meaningful in Custom mode; preset modes are firmware-managed.
            if (performanceMode != 255)
            {
                Logger.Debug($"Skipping ApplyCustomTDPSlow({slow}W) - not in Custom mode (mode={performanceMode})");
                return;
            }
            // Skip during startup grace period to prevent widget sync from overriding main TDP slider
            if ((DateTime.Now - startupTime).TotalMilliseconds < STARTUP_GRACE_PERIOD_MS)
            {
                Logger.Info($"Skipping ApplyCustomTDPSlow({slow}W) - still in startup grace period");
                return;
            }

            if (wmiService == null)
            {
                Logger.Warn("Cannot set custom TDP Slow: WMI service not available");
                return;
            }

            try
            {
                var result = wmiService.SetCPUShortTermPowerLimit(slow);
                if (result.Success)
                {
                    customTDPSlow = slow;
                    Logger.Info($"Slow TDP (SPL) set to {slow}W");
                    CustomTDPApplied?.Invoke();
                }
                else
                {
                    Logger.Error($"Failed to set Slow TDP: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting Slow TDP: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply individual Fast TDP (SPPL) value
        /// </summary>
        public void ApplyCustomTDPFast(int fast)
        {
            // Only meaningful in Custom mode; preset modes are firmware-managed.
            if (performanceMode != 255)
            {
                Logger.Debug($"Skipping ApplyCustomTDPFast({fast}W) - not in Custom mode (mode={performanceMode})");
                return;
            }
            // Skip during startup grace period to prevent widget sync from overriding main TDP slider
            if ((DateTime.Now - startupTime).TotalMilliseconds < STARTUP_GRACE_PERIOD_MS)
            {
                Logger.Info($"Skipping ApplyCustomTDPFast({fast}W) - still in startup grace period");
                return;
            }

            if (wmiService == null)
            {
                Logger.Warn("Cannot set custom TDP Fast: WMI service not available");
                return;
            }

            try
            {
                var result = wmiService.SetCPULongTermPowerLimit(fast);
                if (result.Success)
                {
                    customTDPFast = fast;
                    Logger.Info($"Fast TDP (SPPL) set to {fast}W");
                    CustomTDPApplied?.Invoke();
                }
                else
                {
                    Logger.Error($"Failed to set Fast TDP: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting Fast TDP: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply individual Peak TDP (FPPT) value
        /// </summary>
        public void ApplyCustomTDPPeak(int peak)
        {
            // Only meaningful in Custom mode; preset modes are firmware-managed.
            if (performanceMode != 255)
            {
                Logger.Debug($"Skipping ApplyCustomTDPPeak({peak}W) - not in Custom mode (mode={performanceMode})");
                return;
            }
            // Skip during startup grace period to prevent widget sync from overriding main TDP slider
            if ((DateTime.Now - startupTime).TotalMilliseconds < STARTUP_GRACE_PERIOD_MS)
            {
                Logger.Info($"Skipping ApplyCustomTDPPeak({peak}W) - still in startup grace period");
                return;
            }

            if (wmiService == null)
            {
                Logger.Warn("Cannot set custom TDP Peak: WMI service not available");
                return;
            }

            try
            {
                var result = wmiService.SetCPUPeakPowerLimit(peak);
                if (result.Success)
                {
                    customTDPPeak = peak;
                    Logger.Info($"Peak TDP (FPPT) set to {peak}W");
                    CustomTDPApplied?.Invoke();
                }
                else
                {
                    Logger.Error($"Failed to set Peak TDP: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting Peak TDP: {ex.Message}");
            }
        }

        // === Per-mode fan curve storage (Rodpad pattern) ============================


        public void SetFanFullSpeed(bool enabled)
        {
            if (wmiService == null)
            {
                Logger.Warn("Cannot set fan full speed: WMI service not available");
                return;
            }

            try
            {
                var result = wmiService.SetFanFullSpeed(enabled);
                if (result.Success)
                {
                    fanFullSpeed = enabled;
                    Logger.Info($"Fan full speed {(enabled ? "enabled" : "disabled")}");
                }
                else
                {
                    Logger.Error($"Failed to set fan full speed: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting fan full speed: {ex.Message}");
            }
        }

        public void SetGyroEnabled(bool enabled)
        {
            // WIP - Gyro control not working currently
            Logger.Warn("Gyro control is WIP - not implemented");
            gyroEnabled = enabled;
        }

        public bool TrySetVibration(int level)
        {
            if (!isControllerConnected || controllerService == null)
            {
                Logger.Warn("Cannot set vibration: controller not connected");
                return false;
            }

            if (level < 0 || level > 3)
            {
                Logger.Warn($"Invalid vibration level: {level}");
                return false;
            }

            try
            {
                var vibLevel = (ControllerVibrationLevel)level;
                var result = controllerService.SetBothControllersVibration(vibLevel);
                if (result.Success)
                {
                    vibrationLevel = level;
                    Logger.Info($"Vibration set to level {level} ({result.Message})");

                    // The intensity HID command (05 06 67 02 ...) is a silent config write —
                    // unlike SetVibrationMode's command (05 00 06 04 03 ...), the controller
                    // firmware doesn't play a confirmation buzz for it. Re-issue the current
                    // vibration mode (unchanged) right after so the user gets the same demo
                    // pulse they'd feel from changing mode, now at the newly set intensity —
                    // gives tactile feedback for the Quick-tab intensity tile. Skipped when
                    // turning vibration Off, since a demo buzz there would contradict the
                    // user's intent.
                    if (level > 0)
                    {
                        try { SetVibrationMode(vibrationMode); }
                        catch (Exception demoEx) { Logger.Debug($"Vibration demo pulse after intensity change failed: {demoEx.Message}"); }
                    }

                    return true;
                }

                Logger.Error($"Failed to set vibration: {result.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting vibration: {ex.Message}");
                return false;
            }
        }

        public void SetVibration(int level)
        {
            TrySetVibration(level);
        }

        public void SetPowerLight(bool enabled)
        {
            if (wmiService == null)
            {
                Logger.Warn("Cannot set power light: WMI service not available");
                return;
            }

            try
            {
                var result = wmiService.SetPowerLight(enabled);
                if (result.Success)
                {
                    powerLightEnabled = enabled;
                    Logger.Info($"Power light {(enabled ? "enabled" : "disabled")}");
                }
                else
                {
                    Logger.Error($"Failed to set power light: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting power light: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the button mapping for a remappable button (legacy gamepad-only method).
        /// </summary>
        /// <param name="buttonIndex">Button index: 0=Y1, 1=Y2, 2=Y3, 3=M1, 4=M2, 5=M3</param>
        /// <param name="actionIndex">Action index matching RemapAction enum (0-28)</param>
        public void SetButtonMapping(int buttonIndex, int actionIndex)
        {
            // Forward to advanced method with gamepad type
            SetButtonMappingAdvanced(buttonIndex, 0, new int[] { actionIndex });
        }

        /// <summary>
        /// Sets the button mapping with type support (Gamepad, Keyboard, Mouse).
        /// </summary>
        /// <param name="buttonIndex">Button index: 0=Y1, 1=Y2, 2=Y3, 3=M1, 4=M2, 5=M3</param>
        /// <param name="mappingType">0=Gamepad, 1=Keyboard, 2=Mouse</param>
        /// <param name="values">Mapping values (gamepad action, keyboard keys[], or mouse button)</param>
        public void SetButtonMappingAdvanced(int buttonIndex, int mappingType, int[] values)
        {
            try
            {
                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    Logger.Warn("Cannot set button mapping: controller not connected");
                    return;
                }

                // Map button index to RemappableButton enum
                RemappableButton remapButton = buttonIndex switch
                {
                    0 => RemappableButton.Y1,
                    1 => RemappableButton.Y2,
                    2 => RemappableButton.Y3,
                    3 => RemappableButton.M1,
                    4 => RemappableButton.M2,
                    5 => RemappableButton.M3,
                    _ => throw new ArgumentException($"Invalid button index: {buttonIndex}")
                };

                // Log the button details for debugging
                var ctrl = LegionGoController.GetControllerForButton(remapButton);
                Logger.Info($"SetButtonMappingAdvanced: buttonIndex={buttonIndex}, button={remapButton}(0x{(byte)remapButton:X2}), controller={ctrl}(0x{(byte)ctrl:X2}), mappingType={mappingType}, values=[{string.Join(",", values ?? Array.Empty<int>())}]");

                bool success;
                if (mappingType == 0 && (values == null || values.Length == 0 || values[0] == 0))
                {
                    // Clear mapping (disabled)
                    success = controller.ClearButtonMapping(remapButton);
                    Logger.Info($"Button {remapButton} mapping cleared (HID: 05 00 12 0A {(byte)ctrl:X2} 01 11 01 {(byte)remapButton:X2} 01)");
                }
                else
                {
                    // Set mapping with type
                    var type = (MappingType)(mappingType + 1);  // 0→1, 1→2, 2→3
                    byte[] mappings;

                    if (mappingType == 0)
                    {
                        // Gamepad: use RemapAction
                        var action = RemapActionHelper.GetByIndex(values[0]);
                        mappings = new byte[] { (byte)action };
                    }
                    else
                    {
                        // Keyboard or Mouse: use raw values
                        mappings = values.Select(v => (byte)v).ToArray();
                    }

                    success = controller.SetButtonMappingAdvanced(remapButton, type, mappings);
                    var hidBytes = $"05 00 12 0A {(byte)ctrl:X2} 01 11 01 {(byte)remapButton:X2} {(byte)type:X2} {string.Join(" ", mappings.Select(b => b.ToString("X2")))}";
                    Logger.Info($"Button {remapButton} mapped to {type} with mappings=[{string.Join(",", mappings.Select(b => $"0x{b:X2}"))}] (HID: {hidBytes})");
                }

                if (!success)
                {
                    Logger.Error($"Failed to set button mapping for {remapButton}");
                }

                // Delay to allow controller firmware to process command before next one
                System.Threading.Thread.Sleep(HID_COMMAND_DELAY_MS);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting button mapping: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets mapping for Legion-specific buttons (Desktop, Page) which use GamepadButton codes.
        /// </summary>
        /// <param name="button">The GamepadButton to map (DesktopButton=0x25, PageButton=0x26)</param>
        /// <param name="mappingType">0=Gamepad, 1=Keyboard, 2=Mouse</param>
        /// <param name="values">Mapping values (key codes, button codes, etc.)</param>
        public void SetLegionButtonMapping(GamepadButton button, int mappingType, int[] values)
        {
            try
            {
                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    Logger.Warn($"Cannot set {button} mapping: controller not connected");
                    return;
                }

                var type = (MappingType)(mappingType + 1); // 0->Gamepad(1), 1->Keyboard(2), 2->Mouse(3)
                byte[] mappings;

                if (mappingType == 0)
                {
                    // Gamepad: use RemapActionHelper to convert to HID button code
                    if (values.Length == 0 || values[0] == 0)
                    {
                        // Disabled - clear the mapping
                        controller.ClearGamepadButtonMapping(button);
                        Logger.Info($"{button} mapping cleared (disabled)");
                        return;
                    }
                    var action = RemapActionHelper.GetByIndex(values[0]);
                    mappings = new byte[] { (byte)action };
                }
                else
                {
                    // Keyboard or Mouse: use raw values
                    mappings = values.Select(v => (byte)v).ToArray();
                }

                bool success = controller.SetGamepadButtonMappingAdvanced(button, type, mappings);
                var hidBytes = $"05 00 12 0A 03 01 11 01 {(byte)button:X2} {(byte)type:X2} {string.Join(" ", mappings.Select(b => b.ToString("X2")))}";
                Logger.Info($"{button} mapped to {type} with mappings=[{string.Join(",", mappings.Select(b => $"0x{b:X2}"))}] (HID: {hidBytes})");

                if (!success)
                {
                    Logger.Error($"Failed to set {button} mapping");
                }

                System.Threading.Thread.Sleep(HID_COMMAND_DELAY_MS);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting {button} mapping: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the Nintendo layout mode (swaps A↔B and X↔Y face buttons).
        /// </summary>
        public void SetNintendoLayout(bool enabled)
        {
            try
            {
                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    Logger.Warn("Cannot set Nintendo layout: controller not connected");
                    return;
                }

                bool success = controller.SetNintendoLayout(enabled);
                if (success)
                {
                    nintendoLayoutEnabled = enabled;
                    Logger.Info($"Nintendo layout {(enabled ? "enabled" : "disabled")}");
                }
                else
                {
                    Logger.Error("Failed to set Nintendo layout");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting Nintendo layout: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the vibration mode preset (game-specific vibration patterns).
        /// </summary>
        /// <param name="mode">Vibration mode: 1=FPS, 2=Racing, 3=AVG, 4=SPG, 5=RPG</param>
        public void SetVibrationMode(int mode)
        {
            try
            {
                using var controller = new LegionGoController();
                if (!controller.Connect())
                {
                    Logger.Warn("Cannot set vibration mode: controller not connected");
                    return;
                }

                VibrationMode vibMode = (VibrationMode)mode;
                bool success = controller.SetVibrationMode(vibMode);
                if (success)
                {
                    vibrationMode = mode;
                    Logger.Info($"Vibration mode set to {vibMode}");
                }
                else
                {
                    Logger.Error($"Failed to set vibration mode to {vibMode}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting vibration mode: {ex.Message}");
            }
        }
        public override void Update()
        {
            // Detect if controller service has disconnected (e.g., controller removed)
            if (isControllerConnected && controllerService != null && !controllerService.IsConnected)
            {
                isControllerConnected = false;
                Logger.Info("Controller disconnected - will attempt reconnection");
                // Battery monitoring will stop automatically when connection is lost
            }

            // Periodically check for controller connection if not connected
            if (!isControllerConnected && controllerService != null)
            {
                try
                {
                    var connectResult = controllerService.Connect();
                    if (connectResult.Success)
                    {
                        isControllerConnected = true;
                        isLegionGoDetected = true;
                        LegionGoDetected.SetDetected(true);
                        Logger.Info($"Legion Go controller reconnected: {connectResult.Message}");

                        // Restart battery monitoring on reconnection
                        StartBatteryMonitoring();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Controller reconnection attempt failed: {ex.Message}");
                }
            }

            // Rate-limit fan speed refresh to reduce WMI call overhead. The active
            // cadence is 2s when someone needs the data (fan-curve UI open, sidebar/OSD
            // showing, game running); otherwise back off to 10s so an idle helper isn't
            // spending ~30ms of WMI every two seconds for nobody.
            // Note: TDP refresh is disabled to prevent conflicts with user-set values
            if (isLegionGoDetected && wmiService != null)
            {
                bool active = fanCurveVisible || (performanceManager?.IsAnyMetricsConsumerActive ?? true);
                int intervalMs = active ? FAN_SPEED_REFRESH_INTERVAL_MS : FAN_SPEED_IDLE_INTERVAL_MS;
                var now = DateTime.Now;
                if ((now - lastFanSpeedRefresh).TotalMilliseconds >= intervalMs)
                {
                    RefreshFanSpeed();
                    lastFanSpeedRefresh = now;
                }
            }
        }

        /// <summary>
        /// Reads current CPU fan speed from device for internal use (OSD display).
        /// Also pushes temp/RPM updates to widget when fan curve is visible.
        /// </summary>
        private void RefreshFanSpeed()
        {
            try
            {
                // Read fan RPM for internal use (OSD)
                var fanResult = wmiService.GetCpuFanSpeed();
                if (fanResult.Success && fanResult.Result.HasValue)
                {
                    cpuFanSpeed = fanResult.Result.Value;
                }

                // Always push RPM and CPU temp so the OSD + quick-settings fan readout (and the
                // fan-curve card header) show a value instead of "--" even when the fan-curve
                // panel hasn't been opened (issue #83/#88 for RPM; the header temp had the same
                // bug - it went right back to "--" on every collapse since only RPM was
                // unconditional). The fan-control sensor (0x01) read below stays gated on the
                // panel being visible since it's only consumed by the fan-curve UI's own info
                // panel, not the always-visible header.
                LegionCPUFanRPM.UpdateRPM(cpuFanSpeed);

                // Send the true CPU temperature (Tctl/Tdie). This is what the fan-curve UI
                // displays — NOT
                // the EC fan-control sensor (0x01) below, which reads a chipset/board area
                // and was being mistaken for the CPU temp.
                if (performanceManager != null && performanceManager.SoCTemperature > 0)
                {
                    int cpuTemp = (int)performanceManager.SoCTemperature;
                    LegionCPUCurrentTemp.UpdateTemp(cpuTemp);
                }

                if (fanCurveVisible)
                {
                    // Read the fan control sensor temp (0x01 sensor) - this is what the EC uses for fan curve lookup
                    var fanSensorResult = wmiService.GetFanControlSensorTemp();
                    if (fanSensorResult.Success && fanSensorResult.Result.HasValue)
                    {
                        LegionFanSensorTemp.UpdateTemp(fanSensorResult.Result.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error reading fan speed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by LegionFanCurveVisibleProperty when widget sets visibility.
        /// </summary>
        public void SetFanCurveVisible(bool visible)
        {
            fanCurveVisible = visible;
            Logger.Debug($"Fan curve visibility set to: {visible}");

            // Immediately push current values when fan curve becomes visible
            if (visible)
            {
                LegionCPUFanRPM.UpdateRPM(cpuFanSpeed);

                // Read the fan control sensor temp (0x01 sensor) - this is what the EC uses for fan curve lookup
                var fanSensorResult = wmiService?.GetFanControlSensorTemp();
                if (fanSensorResult?.Success == true && fanSensorResult.Value.Result.HasValue)
                {
                    LegionFanSensorTemp.UpdateTemp(fanSensorResult.Value.Result.Value);
                }

                // Push the true CPU temperature (Tctl/Tdie) — see the polling path above.
                if (performanceManager != null && performanceManager.SoCTemperature > 0)
                {
                    int cpuTemp = (int)performanceManager.SoCTemperature;
                    LegionCPUCurrentTemp.UpdateTemp(cpuTemp);
                }
            }
        }

        // Fan curves (1.9.7c): Quiet / Balanced / Performance run the firmware's own curves. In
        // Custom the firmware runs the custom fan table, which GoTweaks sets to Lenovo's stock
        // curve whenever the device enters Custom (the firmware doesn't expose its Balanced
        // table, so it can't be copied). The fan curve editor was removed.
        private void OnPerformanceModeChanged(int oldMode, int newMode)
        {
            if (oldMode == newMode) return;
            if (newMode == 255) ApplyStockFanCurveIfCustom("mode change");
        }

        private void ApplyStockFanCurveIfCustom(string reason)
        {
            if (performanceMode != 255 || wmiService == null) return;
            try
            {
                var result = wmiService.SetFanCurve(LenovoWMIService.DefaultFanCurve);
                if (result.Success)
                    Logger.Info($"Custom mode ({reason}): stock fan curve applied [{string.Join(",", LenovoWMIService.DefaultFanCurve)}]%");
                else
                    Logger.Warn($"Custom mode ({reason}): stock fan curve not applied: {result.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Custom mode ({reason}): stock fan curve threw: {ex.Message}");
            }
        }
        /// <summary>
        /// Gets the current CPU fan speed in RPM. Returns 0 if Legion Go is not detected.
        /// </summary>
        public int GetCpuFanSpeed()
        {
            return isLegionGoDetected ? cpuFanSpeed : 0;
        }

        /// <summary>
        /// Reads current TDP values from device and updates properties silently (without triggering WMI set calls)
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Logger.Info("Disposing Legion Manager...");

                try
                {
                    // Stop battery monitoring
                    StopBatteryMonitoring();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error stopping battery monitoring: {ex.Message}");
                }

                try
                {
                    // Dispose TDP reapply timer
                    if (tdpReapplyTimer != null)
                    {
                        tdpReapplyTimer.Stop();
                        tdpReapplyTimer.Dispose();
                        tdpReapplyTimer = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error disposing TDP reapply timer: {ex.Message}");
                }

                try
                {
                    controllerService?.Dispose();
                    controllerService = null;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error disposing controller service: {ex.Message}");
                }

                wmiService = null;
                Logger.Info("Legion Manager disposed.");
            }

            base.Dispose(disposing);
        }
    }
}
