using System;
using System.Threading;

namespace XboxGamingBarHelper.PresentMon
{
    /// <summary>
    /// Thread-safe rolling-window aggregates of PresentMon CSV output.
    ///
    /// Producer: <see cref="PresentMonRunner"/> background reader, one CSV line per frame.
    /// Consumer: OSD template build at 1 Hz (and AutoTDP tick).
    ///
    /// Strategy:
    /// - Per-frame increments to volatile counters keyed by FrameType.
    /// - 1 Hz tick (timer) drains the counters into the public fps numbers and resets.
    /// - The "is live" flag is set on every Push call and cleared by Reset on the runner
    ///   thread. Consumers check freshness via LastUpdateTicksUtc.
    ///
    /// Fields are atomic-read-safe (long/double swap on x64) and only written on the
    /// background reader thread; consumers see consistent values without locking.
    /// </summary>
    internal sealed class PresentMonMetrics
    {
        private long _afmfFrameCount;     // FrameType=AMD_AFMF
        private long _displayedFrameCount; // application + repeated + generated (anything that hit the wire)

        // Render-cost sums: only Application rows carry non-zero CPU/GPU busy,
        // and only Application gaps are meaningful "frame time" (AFMF inserts
        // its own present halfway through, so its MsBetweenPresents is half of
        // the real render-to-render time). Keep these separate.
        private double _sumCpuBusyMs;
        private double _sumGpuBusyMs;
        private double _sumMsBetweenPresentsApp;
        private long _appSampleCount;

        // Sum of MsBetweenPresents across every row (any frame type) - used to derive FPS from
        // PresentMon's own per-frame ETW timestamps rather than from a raw per-window frame
        // count (see FlushPerSecond for why).
        private double _sumMsBetweenPresentsAll;

        private long _lastUpdateTicksUtc;

        // Last computed 1 s rates / averages exposed to consumers.
        private volatile int _appFps;
        private volatile int _afmfFps;
        private volatile int _displayedFps;
        private double _cpuBusyAvgMs;
        private double _gpuBusyAvgMs;
        private double _frametimeAvgMs;
        private double _cpuBusyPct;
        private double _gpuBusyPct;

        public int RenderedFps => _appFps;
        public int AfmfFps => _afmfFps;
        public int DisplayedFps => _displayedFps;
        public double CpuBusyAvgMs => Interlocked.CompareExchange(ref _cpuBusyAvgMs, 0, 0);
        public double GpuBusyAvgMs => Interlocked.CompareExchange(ref _gpuBusyAvgMs, 0, 0);
        public double FrametimeAvgMs => Interlocked.CompareExchange(ref _frametimeAvgMs, 0, 0);
        /// <summary>% of frame time the CPU spent on render work (MsCPUBusy / MsBetweenPresents over Application frames).</summary>
        public double CpuBusyPct => Interlocked.CompareExchange(ref _cpuBusyPct, 0, 0);
        /// <summary>% of frame time the GPU spent on render work (MsGPUBusy / MsBetweenPresents over Application frames).</summary>
        public double GpuBusyPct => Interlocked.CompareExchange(ref _gpuBusyPct, 0, 0);
        public long LastUpdateTicksUtc => Interlocked.Read(ref _lastUpdateTicksUtc);

        /// <summary>True when a frame line has been parsed in the last <paramref name="staleMs"/>.</summary>
        public bool IsLive(int staleMs = 2000)
        {
            long last = LastUpdateTicksUtc;
            if (last == 0) return false;
            return (DateTime.UtcNow.Ticks - last) < (staleMs * TimeSpan.TicksPerMillisecond);
        }

        /// <summary>Called by PresentMonRunner for each CSV frame line.</summary>
        public void Push(FrameType frameType, double msBetweenPresents, double cpuBusyMs, double gpuBusyMs)
        {
            Interlocked.Increment(ref _displayedFrameCount);
            AddDouble(ref _sumMsBetweenPresentsAll, msBetweenPresents);
            switch (frameType)
            {
                case FrameType.Application:
                    // Only Application rows carry meaningful CPU/GPU busy times
                    // and the real render-to-render gap.
                    AddDouble(ref _sumCpuBusyMs, cpuBusyMs);
                    AddDouble(ref _sumGpuBusyMs, gpuBusyMs);
                    AddDouble(ref _sumMsBetweenPresentsApp, msBetweenPresents);
                    Interlocked.Increment(ref _appSampleCount);
                    break;
                case FrameType.AMD_AFMF:
                    Interlocked.Increment(ref _afmfFrameCount);
                    break;
            }
            Interlocked.Exchange(ref _lastUpdateTicksUtc, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Drain the per-second counters into the public fields and reset. Called by the
        /// runner at ~1 Hz from its own timer so the public values are stable for that
        /// window (OSD reads see a steady number for the second).
        ///
        /// FPS is derived from the average time BETWEEN PRESENTS that PresentMon itself measured
        /// (ETW timestamps), not from how many CSV rows we happened to receive during this
        /// window. A raw per-window row count is vulnerable to the child process's stdout being
        /// bursty (the OS pipe can buffer several lines and deliver them in one clump) - frames
        /// that actually landed smoothly at 75fps can arrive to us in an uneven trickle, making a
        /// naive count spike in one window and dip in the next even though nothing on screen
        /// changed. Averaging each frame's own measured gap sidesteps that entirely: it doesn't
        /// matter when WE read the data, only what PresentMon recorded at capture time.
        /// </summary>
        public void FlushPerSecond()
        {
            int afmf = (int)Interlocked.Exchange(ref _afmfFrameCount, 0);
            int disp = (int)Interlocked.Exchange(ref _displayedFrameCount, 0);
            long appSamples = Interlocked.Exchange(ref _appSampleCount, 0);
            double sumCpu = Interlocked.Exchange(ref _sumCpuBusyMs, 0);
            double sumGpu = Interlocked.Exchange(ref _sumGpuBusyMs, 0);
            double sumPresentApp = Interlocked.Exchange(ref _sumMsBetweenPresentsApp, 0);
            double sumPresentAll = Interlocked.Exchange(ref _sumMsBetweenPresentsAll, 0);

            _afmfFps = afmf; // only ever used as a >0 gate for the [FG] badge, a raw count is fine
            double allAvg = disp > 0 ? sumPresentAll / disp : 0;
            _displayedFps = allAvg > 0 ? (int)Math.Round(1000.0 / allAvg) : 0;

            if (appSamples > 0)
            {
                double cpuAvg = sumCpu / appSamples;
                double gpuAvg = sumGpu / appSamples;
                double ftAvg  = sumPresentApp / appSamples;
                Interlocked.Exchange(ref _cpuBusyAvgMs, cpuAvg);
                Interlocked.Exchange(ref _gpuBusyAvgMs, gpuAvg);
                Interlocked.Exchange(ref _frametimeAvgMs, ftAvg);
                _appFps = ftAvg > 0 ? (int)Math.Round(1000.0 / ftAvg) : 0;
                // Busy % = render-cost / available frame time. Clamp at 100
                // because PresentMon's CPU/GPU busy can overshoot the present
                // interval when a present is delayed (queue drain).
                double cpuPct = ftAvg > 0 ? System.Math.Min(100.0, cpuAvg / ftAvg * 100.0) : 0;
                double gpuPct = ftAvg > 0 ? System.Math.Min(100.0, gpuAvg / ftAvg * 100.0) : 0;
                Interlocked.Exchange(ref _cpuBusyPct, cpuPct);
                Interlocked.Exchange(ref _gpuBusyPct, gpuPct);
            }
            else
            {
                _appFps = 0;
                Interlocked.Exchange(ref _cpuBusyAvgMs, 0);
                Interlocked.Exchange(ref _gpuBusyAvgMs, 0);
                Interlocked.Exchange(ref _frametimeAvgMs, 0);
                Interlocked.Exchange(ref _cpuBusyPct, 0);
                Interlocked.Exchange(ref _gpuBusyPct, 0);
            }
        }

        /// <summary>Called when the runner stops so consumers see "no data" immediately.</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _afmfFrameCount, 0);
            Interlocked.Exchange(ref _displayedFrameCount, 0);
            Interlocked.Exchange(ref _appSampleCount, 0);
            Interlocked.Exchange(ref _sumCpuBusyMs, 0);
            Interlocked.Exchange(ref _sumGpuBusyMs, 0);
            Interlocked.Exchange(ref _sumMsBetweenPresentsApp, 0);
            Interlocked.Exchange(ref _sumMsBetweenPresentsAll, 0);
            Interlocked.Exchange(ref _lastUpdateTicksUtc, 0);
            _appFps = 0;
            _afmfFps = 0;
            _displayedFps = 0;
            Interlocked.Exchange(ref _cpuBusyAvgMs, 0);
            Interlocked.Exchange(ref _gpuBusyAvgMs, 0);
            Interlocked.Exchange(ref _frametimeAvgMs, 0);
            Interlocked.Exchange(ref _cpuBusyPct, 0);
            Interlocked.Exchange(ref _gpuBusyPct, 0);
        }

        private static void AddDouble(ref double target, double delta)
        {
            double initial, computed;
            do
            {
                initial = Interlocked.CompareExchange(ref target, 0, 0);
                computed = initial + delta;
            } while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
        }
    }

    internal enum FrameType : byte
    {
        Unknown = 0,
        Application = 1,
        Repeated = 2,
        Intel_XEFG = 50,
        AMD_AFMF = 100,
    }
}
