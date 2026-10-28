using Microsoft.Win32;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.LosslessScaling.Properties;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.LosslessScaling
{
    internal class LosslessScalingManager : Manager
    {
        // Lossless Scaling is no longer configured from the widget: this manager only detects
        // it, launches it if needed and sends its scaling hotkey for the Quick Settings tile.

        // P/Invoke for window management (minimize LS after launching it)
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MINIMIZE = 6;

        private const string PROCESS_NAME = "LosslessScaling";
        private const int STEAM_APP_ID = 993090;

        private static readonly string SETTINGS_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lossless Scaling",
            "Settings.xml"
        );

        // Input injection for hotkeys
        private readonly InputInjector inputInjector;
        private InjectedInputKeyboardInfo[] toggleScalingKeyboardCombo;
        private string currentHotkeyDescription = "Ctrl+Alt+S";

        #region Properties

        private readonly LosslessScalingInstalledProperty losslessScalingInstalled;
        public LosslessScalingInstalledProperty LosslessScalingInstalled => losslessScalingInstalled;

        private readonly LosslessScalingRunningProperty losslessScalingRunning;
        public LosslessScalingRunningProperty LosslessScalingRunning => losslessScalingRunning;

        private readonly LosslessScalingEnabledProperty losslessScalingEnabled;
        public LosslessScalingEnabledProperty LosslessScalingEnabled => losslessScalingEnabled;

        #endregion

        // State tracking
        private bool isScalingActive = false;
        private bool isRunningLastResult = false;

        // Guards the throttle/cache fields below, which are touched from both the manager
        // Update() timer thread and the pipe thread (property-change handler). Kept for short
        // synchronous sections only — never held across an await.
        private readonly object stateLock = new object();

        // IsRunning() result cache. The poll enumerates processes on every Update()
        // tick and is also read by several action paths; a short TTL de-dupes those
        // reads. Paths that just launched or killed LS call IsRunning() directly so the
        // fresh result is observed at once (and the cache refreshed).
        private bool cachedIsRunning = false;
        private DateTime lastRunningCheck = DateTime.MinValue;
        private static readonly TimeSpan RunningCacheTtl = TimeSpan.FromMilliseconds(1000);

        // Installation re-detection throttle (so the Quick Settings tile can appear/disappear
        // at runtime without a helper restart when LS is installed/uninstalled).
        private DateTime lastInstalledCheck = DateTime.MinValue;
        private static readonly TimeSpan InstalledRecheckInterval = TimeSpan.FromSeconds(10);

        public LosslessScalingManager() : base()
        {
            Logger.Info("Initializing Lossless Scaling Manager...");

            bool isInstalled = IsInstalled();
            bool isRunning = IsRunning();
            Logger.Info($"Lossless Scaling installed: {isInstalled}, running: {isRunning}");

            // Initialize all properties
            losslessScalingInstalled = new LosslessScalingInstalledProperty(isInstalled, this);
            losslessScalingRunning = new LosslessScalingRunningProperty(isRunning, this);
            losslessScalingEnabled = new LosslessScalingEnabledProperty(false, this);

            // Subscribe to action properties
            losslessScalingEnabled.PropertyChanged += LosslessScalingEnabled_PropertyChanged;

            inputInjector = InputInjector.TryCreate();

            // Read hotkey from Settings.xml (user may have customized it)
            BuildToggleScalingHotkey();

            Logger.Info("Lossless Scaling Manager initialized successfully.");
        }

        public override void Update()
        {
            base.Update();

            bool currentlyRunning = IsRunningCached();
            if (losslessScalingRunning.Value != currentlyRunning)
            {
                losslessScalingRunning.SetValue(currentlyRunning, DateTime.Now.Ticks);

                // LS exited → no scaling can be active. Reset the best-effort tracker so
                // the next enable starts from a known state. (Toggles made via LS's own
                // hotkey while it runs cannot be observed — that stays a blind spot.)
                if (!currentlyRunning)
                {
                    lock (stateLock) { isScalingActive = false; }
                }
            }

            // Periodically re-detect installation so the Quick Settings tile
            // appears/disappears without a helper restart when the user installs,
            // uninstalls, or first-launches LS while GoTweaks is already running.
            bool doInstalledCheck;
            lock (stateLock)
            {
                doInstalledCheck = DateTime.UtcNow - lastInstalledCheck >= InstalledRecheckInterval;
                if (doInstalledCheck)
                {
                    lastInstalledCheck = DateTime.UtcNow;
                }
            }
            if (doInstalledCheck)
            {
                bool currentlyInstalled = IsInstalled();
                if (losslessScalingInstalled.Value != currentlyInstalled)
                {
                    Logger.Info($"Lossless Scaling installation state changed → {currentlyInstalled}");
                    losslessScalingInstalled.SetValue(currentlyInstalled, DateTime.Now.Ticks);
                }
            }
        }

        #region Core Methods

        private bool IsInstalled()
        {
            // Presence of the LosslessScaling.exe (located via the Steam library) is
            // the source of truth. Settings.xml is created only on the FIRST LS launch,
            // so requiring it here would hide the Lossless tile for a freshly-installed but
            // never-run LS. When Settings.xml is missing we simply fall back to the
            // built-in LosslessScalingSettings defaults.
            string exePath = FindLosslessScalingExePath();
            return !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
        }

        /// <summary>
        /// Cached view of <see cref="IsRunning"/> for high-frequency callers. Returns the
        /// last result within <see cref="RunningCacheTtl"/>, otherwise re-polls. Paths that
        /// just launched or killed LS call <see cref="IsRunning"/> directly so the fresh
        /// result is observed at once (and the cache refreshed).
        /// </summary>
        private bool IsRunningCached()
        {
            lock (stateLock)
            {
                if (DateTime.UtcNow - lastRunningCheck < RunningCacheTtl)
                {
                    return cachedIsRunning;
                }
            }

            // Not installed -> can never be running. Skip the process-table scan (the
            // actually expensive part, done every tick otherwise) entirely; installation
            // state itself is already re-checked periodically by Update() so this stays
            // fresh enough without a separate poll here.
            if (losslessScalingInstalled != null && !losslessScalingInstalled.Value)
            {
                lock (stateLock)
                {
                    cachedIsRunning = false;
                    lastRunningCheck = DateTime.UtcNow;
                }
                return false;
            }

            return IsRunning();
        }

        private bool IsRunning()
        {
            // Only log on a state transition (or a genuine failure) to keep the helper
            // log clean — this is polled frequently.
            // (vvalente30 issue #79: LS process name can be absent or the enumeration
            // can throw on some machines — the Warn below surfaces that case.)
            int procCount = -1;
            string error = null;
            try
            {
                var processes = Process.GetProcessesByName(PROCESS_NAME);
                procCount = processes.Length;
                foreach (var proc in processes)
                {
                    proc.Dispose();
                }
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            bool isRunning = procCount > 0;

            lock (stateLock)
            {
                if (error != null)
                {
                    Logger.Warn($"Lossless Scaling IsRunning() poll threw — {error}");
                }
                else if (isRunning != isRunningLastResult)
                {
                    Logger.Info($"Lossless Scaling running state changed → {isRunning} (procs={procCount})");
                }

                isRunningLastResult = isRunning;
                cachedIsRunning = isRunning;
                lastRunningCheck = DateTime.UtcNow;
            }

            return isRunning;
        }

        public async Task<bool> LaunchLosslessScalingAsync()
        {
            if (IsRunningCached())
            {
                Logger.Info("Lossless Scaling is already running.");
                return true;
            }

            // A freshly launched LS always starts with scaling OFF, so reset the
            // best-effort tracker here. The enable handler then toggles it on if needed.
            lock (stateLock) { isScalingActive = false; }

            try
            {
                // Try to find and launch via direct exe path first
                string exePath = FindLosslessScalingExePath();
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    Logger.Info($"Launching Lossless Scaling directly from: {exePath}");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                        WindowStyle = ProcessWindowStyle.Minimized,
                        UseShellExecute = true
                    };
                    Process.Start(startInfo);
                    await Task.Delay(2000);

                    // Ensure window is minimized after launch
                    var processes = Process.GetProcessesByName(PROCESS_NAME);
                    try
                    {
                        foreach (var proc in processes)
                        {
                            if (proc.MainWindowHandle != IntPtr.Zero)
                            {
                                ShowWindow(proc.MainWindowHandle, SW_MINIMIZE);
                            }
                        }
                    }
                    finally
                    {
                        foreach (var proc in processes)
                        {
                            proc.Dispose();
                        }
                    }

                    bool success = IsRunning();
                    Logger.Info($"Lossless Scaling launch {(success ? "successful" : "failed")}");
                    return success;
                }
                else
                {
                    // Fallback to Steam URI if exe not found
                    Logger.Info("Exe not found, falling back to Steam URI launch...");
                    var uri = new Uri($"steam://rungameid/{STEAM_APP_ID}");
                    await global::Windows.System.Launcher.LaunchUriAsync(uri);
                    await Task.Delay(3000);

                    bool success = IsRunning();
                    Logger.Info($"Lossless Scaling launch via Steam {(success ? "successful" : "failed")}");
                    return success;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to launch Lossless Scaling: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finds the Lossless Scaling executable path from Steam installation.
        /// </summary>
        private string FindLosslessScalingExePath()
        {
            try
            {
                // Try to find Steam installation path from registry
                string steamPath = null;

                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    steamPath = key?.GetValue("SteamPath") as string;
                }

                if (string.IsNullOrEmpty(steamPath))
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    {
                        steamPath = key?.GetValue("InstallPath") as string;
                    }
                }

                if (string.IsNullOrEmpty(steamPath))
                {
                    Logger.Debug("Could not find Steam installation path in registry");
                    return null;
                }

                Logger.Debug($"Found Steam path: {steamPath}");

                // Check default steamapps location first
                string defaultLibrary = Path.Combine(steamPath, "steamapps", "common", "Lossless Scaling", "LosslessScaling.exe");
                if (File.Exists(defaultLibrary))
                {
                    Logger.Debug($"Found Lossless Scaling at default location: {defaultLibrary}");
                    return defaultLibrary;
                }

                // Parse libraryfolders.vdf to find additional Steam library folders
                string libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraryFoldersPath))
                {
                    var libraryContent = File.ReadAllText(libraryFoldersPath);
                    var pathMatches = System.Text.RegularExpressions.Regex.Matches(libraryContent, "\"path\"\\s+\"([^\"]+)\"");

                    foreach (System.Text.RegularExpressions.Match match in pathMatches)
                    {
                        string libraryPath = match.Groups[1].Value.Replace("\\\\", "\\");
                        string lsPath = Path.Combine(libraryPath, "steamapps", "common", "Lossless Scaling", "LosslessScaling.exe");

                        if (File.Exists(lsPath))
                        {
                            Logger.Debug($"Found Lossless Scaling in library: {lsPath}");
                            return lsPath;
                        }
                    }
                }

                Logger.Debug("Could not find Lossless Scaling executable in any Steam library");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error finding Lossless Scaling exe path: {ex.Message}");
                return null;
            }
        }

        private void ToggleScaling()
        {
            if (!IsRunningCached())
            {
                Logger.Warn("Cannot toggle scaling: Lossless Scaling is not running");
                return;
            }

            if (inputInjector == null)
            {
                Logger.Error("Input injector not available");
                return;
            }

            try
            {
                Logger.Info($"Sending {currentHotkeyDescription} hotkey to toggle Lossless Scaling...");
                inputInjector.InjectKeyboardInput(toggleScalingKeyboardCombo);
                bool nowActive;
                lock (stateLock)
                {
                    isScalingActive = !isScalingActive;
                    nowActive = isScalingActive;
                }
                Logger.Info($"Scaling toggled to: {nowActive}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to send hotkey: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the hotkey configuration from Settings.xml and builds the keyboard combo.
        /// Falls back to Ctrl+Alt+S if Settings.xml cannot be read.
        /// </summary>
        private void BuildToggleScalingHotkey()
        {
            var modifiers = new System.Collections.Generic.List<VirtualKey>();
            VirtualKey mainKey = VirtualKey.S;
            var descParts = new System.Collections.Generic.List<string>();

            try
            {
                if (File.Exists(SETTINGS_PATH))
                {
                    var doc = XDocument.Load(SETTINGS_PATH);

                    // Read Hotkey element (the main key, e.g., "S", "F")
                    string hotkeyValue = doc.Root?.Element("Hotkey")?.Value;
                    if (!string.IsNullOrEmpty(hotkeyValue))
                    {
                        mainKey = ParseKeyString(hotkeyValue);
                        Logger.Info($"Read main hotkey from Settings.xml: {hotkeyValue}");
                    }

                    // Read HotkeyModifierKeys element (e.g., "Alt Control")
                    string modifierKeysValue = doc.Root?.Element("HotkeyModifierKeys")?.Value;
                    if (!string.IsNullOrEmpty(modifierKeysValue))
                    {
                        Logger.Info($"Read modifier keys from Settings.xml: {modifierKeysValue}");
                        var modifierParts = modifierKeysValue.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var mod in modifierParts)
                        {
                            switch (mod.ToLower())
                            {
                                case "control":
                                case "ctrl":
                                    modifiers.Add(VirtualKey.LeftControl);
                                    descParts.Add("Ctrl");
                                    break;
                                case "alt":
                                case "menu":
                                    modifiers.Add(VirtualKey.LeftMenu);
                                    descParts.Add("Alt");
                                    break;
                                case "shift":
                                    modifiers.Add(VirtualKey.LeftShift);
                                    descParts.Add("Shift");
                                    break;
                                case "windows":
                                case "win":
                                    modifiers.Add(VirtualKey.LeftWindows);
                                    descParts.Add("Win");
                                    break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to read hotkey from Settings.xml, using default: {ex.Message}");
            }

            // If no modifiers were read, use default Ctrl+Alt
            if (modifiers.Count == 0)
            {
                modifiers.Add(VirtualKey.LeftControl);
                modifiers.Add(VirtualKey.LeftMenu);
                descParts.Add("Ctrl");
                descParts.Add("Alt");
            }

            // Build description
            descParts.Add(GetKeyDescription(mainKey));
            currentHotkeyDescription = string.Join("+", descParts);

            // Build the keyboard combo array: press modifiers, press main key, release all
            var comboList = new System.Collections.Generic.List<InjectedInputKeyboardInfo>();

            // Key down for modifiers
            foreach (var mod in modifiers)
            {
                comboList.Add(new InjectedInputKeyboardInfo { VirtualKey = (ushort)mod, KeyOptions = InjectedInputKeyOptions.None });
            }

            // Key down for main key
            comboList.Add(new InjectedInputKeyboardInfo { VirtualKey = (ushort)mainKey, KeyOptions = InjectedInputKeyOptions.None });

            // Key up for modifiers (reverse order)
            for (int i = modifiers.Count - 1; i >= 0; i--)
            {
                comboList.Add(new InjectedInputKeyboardInfo { VirtualKey = (ushort)modifiers[i], KeyOptions = InjectedInputKeyOptions.KeyUp });
            }

            // Key up for main key
            comboList.Add(new InjectedInputKeyboardInfo { VirtualKey = (ushort)mainKey, KeyOptions = InjectedInputKeyOptions.KeyUp });

            toggleScalingKeyboardCombo = comboList.ToArray();
            Logger.Info($"Lossless Scaling hotkey configured: {currentHotkeyDescription}");
        }

        /// <summary>
        /// Parses a key string (e.g., "S", "F", "F5") to a VirtualKey.
        /// </summary>
        private VirtualKey ParseKeyString(string keyStr)
        {
            if (string.IsNullOrEmpty(keyStr))
                return VirtualKey.S;

            keyStr = keyStr.Trim().ToUpper();

            // Single letter keys A-Z
            if (keyStr.Length == 1 && char.IsLetter(keyStr[0]))
            {
                return (VirtualKey)(65 + (keyStr[0] - 'A'));
            }

            // Number keys 0-9
            if (keyStr.Length == 1 && char.IsDigit(keyStr[0]))
            {
                return (VirtualKey)(48 + (keyStr[0] - '0'));
            }

            // Function keys F1-F24
            if (keyStr.StartsWith("F") && keyStr.Length >= 2 && int.TryParse(keyStr.Substring(1), out int fNum) && fNum >= 1 && fNum <= 24)
            {
                return (VirtualKey)(111 + fNum); // F1 = 112
            }

            // Try direct VirtualKey enum parse
            if (Enum.TryParse<VirtualKey>(keyStr, true, out var vk))
            {
                return vk;
            }

            Logger.Warn($"Could not parse key '{keyStr}', defaulting to S");
            return VirtualKey.S;
        }

        /// <summary>
        /// Gets a friendly description for a VirtualKey.
        /// </summary>
        private string GetKeyDescription(VirtualKey key)
        {
            int keyVal = (int)key;

            // Letters A-Z (65-90)
            if (keyVal >= 65 && keyVal <= 90)
                return ((char)keyVal).ToString();

            // Numbers 0-9 (48-57)
            if (keyVal >= 48 && keyVal <= 57)
                return ((char)keyVal).ToString();

            // Function keys F1-F24 (112-135)
            if (keyVal >= 112 && keyVal <= 135)
                return $"F{keyVal - 111}";

            return key.ToString();
        }

        #endregion

        #region Property Change Handlers

        private async void LosslessScalingEnabled_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                bool shouldBeEnabled = losslessScalingEnabled.Value;
                Logger.Info($"Lossless Scaling enabled changed to: {shouldBeEnabled}");

                if (!IsRunning())
                {
                    if (shouldBeEnabled)
                    {
                        Logger.Info("Lossless Scaling not running, launching...");
                        await LaunchLosslessScalingAsync();
                        await Task.Delay(1000);
                    }
                    else
                    {
                        Logger.Info("Lossless Scaling not running and should be disabled, nothing to do");
                        return;
                    }
                }

                bool active;
                lock (stateLock) { active = isScalingActive; }
                if (active != shouldBeEnabled)
                {
                    ToggleScaling();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in LosslessScalingEnabled_PropertyChanged: {ex.Message}");
            }
        }

        #endregion
    }
}
