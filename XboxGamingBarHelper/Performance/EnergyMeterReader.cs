using System;
using System.Runtime.InteropServices;
using NLog;

namespace XboxGamingBarHelper.Performance
{
    /// <summary>
    /// Reads the whole-package power of the APU from Windows' own "Energy Meter" performance
    /// counters (the RAPL_Package0_PKG instance), through PDH. No driver is needed: Windows
    /// exposes the AMD RAPL package meter itself on the Legion Go 2 (checked on device
    /// 2026-11-27). Returns -1 when the counter is missing or reading fails, and gives up for
    /// good after repeated failures so a broken counter costs nothing per tick.
    /// </summary>
    internal sealed class EnergyMeterReader : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const string CounterPath = @"\Energy Meter(RAPL_Package0_PKG)\Power";
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_NOCAP100 = 0x00008000;
        private const int MaxFailures = 10;

        [StructLayout(LayoutKind.Explicit)]
        private struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double doubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        private IntPtr query;
        private IntPtr counter;
        private bool available;
        private int failures;
        private bool loggedFirst;

        public EnergyMeterReader()
        {
            try
            {
                uint r = PdhOpenQueryW(null, IntPtr.Zero, out query);
                if (r != 0)
                {
                    Logger.Info($"Energy Meter: PdhOpenQuery failed (0x{r:X8}); SoC power falls back to LHM");
                    return;
                }
                r = PdhAddEnglishCounterW(query, CounterPath, IntPtr.Zero, out counter);
                if (r != 0)
                {
                    Logger.Info($"Energy Meter: {CounterPath} not available (0x{r:X8}); SoC power falls back to LHM");
                    PdhCloseQuery(query);
                    query = IntPtr.Zero;
                    return;
                }
                PdhCollectQueryData(query); // prime it
                available = true;
                Logger.Info($"Energy Meter: reading SoC package power from {CounterPath}");
            }
            catch (Exception ex)
            {
                Logger.Info($"Energy Meter: unavailable ({ex.GetType().Name}: {ex.Message})");
                available = false;
            }
        }

        public bool IsAvailable => available;

        /// <summary>Package power in watts, or -1 when unknown.</summary>
        public float ReadWatts()
        {
            if (!available)
            {
                return -1f;
            }

            try
            {
                if (PdhCollectQueryData(query) == 0 &&
                    PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0 &&
                    v.CStatus == 0)
                {
                    double raw = v.doubleValue;
                    // The counter reports milliwatts. Guard against a source in watts anyway: a
                    // handheld package never draws 500 W, and never reads under 0.5 W as milliwatts.
                    double watts = raw >= 500 ? raw / 1000.0 : raw;
                    if (!loggedFirst)
                    {
                        loggedFirst = true;
                        Logger.Info($"Energy Meter: first reading raw={raw:F0} -> {watts:F1} W");
                    }
                    failures = 0;
                    return watts > 0 && watts < 500 ? (float)watts : -1f;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Energy Meter read threw: {ex.Message}");
            }

            if (++failures >= MaxFailures)
            {
                Logger.Info("Energy Meter: stopped after repeated read failures; SoC power falls back to LHM");
                Dispose();
            }
            return -1f;
        }

        public void Dispose()
        {
            available = false;
            if (query != IntPtr.Zero)
            {
                try { PdhCloseQuery(query); } catch { }
                query = IntPtr.Zero;
            }
        }
    }
}
