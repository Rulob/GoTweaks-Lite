using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NLog;

namespace XboxGamingBarHelper.PresentMon
{
    /// <summary>
    /// #66 PresentMon CLI integration. Spawns bundled PresentMon.exe as a per-game
    /// subprocess and parses its CSV-on-stdout stream into <see cref="PresentMonMetrics"/>.
    ///
    /// Command shape:
    ///   PresentMon.exe --session_name GoTweaks_PMon_&lt;pid&gt;_&lt;ticks&gt;
    ///                  --stop_existing_session
    ///                  --output_stdout
    ///                  --no_console_stats
    ///                  --track_frame_type
    ///                  --terminate_on_proc_exit
    ///                  --process_id &lt;pid&gt;
    ///
    /// History: we briefly tried --output_file but PresentMon's fopen opens the
    /// CSV with no FileShare, so the tail FileStream fails immediately with
    /// IOException. The earlier "silent stdout" symptom that pushed us toward
    /// file output was actually the orphan-ETW-session bug (see
    /// Program.CleanupOrphanEtwSessions): killed PresentMons leave behind
    /// GoTweaks_PMon_* realtime sessions that lock Microsoft-Windows-DxgKrnl /
    /// Intel-PresentMon, and the next spawn hangs silently on provider enable
    /// without ever writing to stdout. With ETW cleanup in place stdout streams
    /// fine.
    ///
    /// Per-second flush converts CSV rows into FrameType-classified pushes:
    ///   - FrameType column "Application"  → Application (rendered frames)
    ///   - FrameType column "AMD_AFMF"     → AMD_AFMF  (interpolated frames)
    ///   - FrameType column "Intel_XEFG"   → Intel_XEFG
    ///   - FrameType column "Repeated"     → Repeated  (DWM repeat, not user-visible new content)
    /// MsBetweenPresents and CPUBusy/GPUBusy are copied through verbatim — they're
    /// what the OSD/AutoTDP code already expects from PresentMonMetrics.Push().
    ///
    /// History: an earlier helper-spawned PresentMon attempt died silently because
    /// (a) RTSS-injected ETW session conflicts and (b) orphaned GoTweaks_PMon sessions
    /// from previous helper crashes. Both are addressed here via per-spawn unique
    /// session names and an orphan-PresentMon kill on init.
    /// </summary>
    internal sealed class PresentMonRunner : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly string _exePath;
        private readonly PresentMonMetrics _metrics;
        private readonly object _gate = new object();

        private Process _process;
        private int _targetPid;
        private string _targetExe;
        private bool _active;
        private bool _disposed;
        private Timer _flushTimer;
        private Timer _heartbeatTimer;
        private int _restartCount;
        private long _linesParsed;
        private long _heartbeatLastLines;
        private int _afmfRows;
        private int _repeatedRows;
        private int _applicationRows;

        // Lossless Scaling support. LSFG presents from its own process, not the
        // game, so --process_id <game> misses them entirely. When LSFG is
        // running at spawn time we switch to --process_name (which supports
        // multiple) and record both pids; rows whose ProcessID column matches
        // LSFG get re-tagged as FrameType.AMD_AFMF so existing FG plumbing
        // (AfmfFps, OSD [FG] badge) lights up without new wiring.
        private volatile int _lsfgPid;

        // CSV column indices, filled when the header row arrives.
        private int _colApplication = -1;
        private int _colProcessId   = -1;
        private int _colFrameType   = -1;
        private int _colMsBetween   = -1;
        private int _colCpuBusy     = -1;
        private int _colGpuBusy     = -1;
        private int _colMsRenderPresentLatency = -1;
        private int _colMsUntilDisplayed = -1;
        private bool _headerLogged;

        public PresentMonRunner(string exePath, PresentMonMetrics metrics)
        {
            _exePath = exePath ?? throw new ArgumentNullException(nameof(exePath));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        public void Start(int processId, string processExeName)
        {
            if (processId <= 0) return;
            lock (_gate)
            {
                if (_disposed) return;
                if (_active && _targetPid == processId) return;
                StopUnlocked("game change");
                _targetPid = processId;
                _targetExe = processExeName ?? processId.ToString();
                _active = true;
                _restartCount = 0;
                _linesParsed = 0;
                _heartbeatLastLines = 0;
                _applicationRows = 0;
                _afmfRows = 0;
                _repeatedRows = 0;
                _lsfgPid = 0;
                ResetColumns();
                _metrics.Reset();
                SpawnUnlocked();
            }
        }

        public void Stop(string reason)
        {
            lock (_gate) StopUnlocked(reason);
        }

        private void StopUnlocked(string reason)
        {
            _active = false;
            int pid = _targetPid;
            _targetPid = 0;
            _targetExe = null;
            try { _flushTimer?.Dispose(); } catch { }
            _flushTimer = null;
            try { _heartbeatTimer?.Dispose(); } catch { }
            _heartbeatTimer = null;

            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        Logger.Info($"PresentMonRunner: stopping subprocess (reason={reason})");
                        try { _process.Kill(); } catch { }
                    }
                    _process.Dispose();
                }
                catch (Exception ex) { Logger.Debug($"PresentMonRunner: stop exception: {ex.Message}"); }
                _process = null;
            }

            // PresentMon.Kill() does not release its ETW session; if we don't
            // stop the leftover session ourselves, the next spawn hangs silently
            // on provider enable. Cheap, no-op when there's nothing to clean.
            try { Program.CleanupOrphanEtwSessions(); }
            catch (Exception ex) { Logger.Debug($"PresentMonRunner: post-stop ETW cleanup threw {ex.Message}"); }

            ResetColumns();
            _metrics.Reset();
        }

        private void ResetColumns()
        {
            _colApplication = _colProcessId = _colFrameType = _colMsBetween = -1;
            _colCpuBusy = _colGpuBusy = _colMsRenderPresentLatency = _colMsUntilDisplayed = -1;
            _headerLogged = false;
        }

        private void SpawnUnlocked()
        {
            try
            {
                if (!File.Exists(_exePath))
                {
                    Logger.Warn($"PresentMonRunner: binary not found at {_exePath}; integration off");
                    _active = false;
                    return;
                }

                long ticks = DateTime.UtcNow.Ticks % 1000000;
                string sessionName = $"GoTweaks_PMon_{_targetPid}_{ticks}";

                // LSFG detection at spawn time. If running, capture both the
                // game and LSFG via --process_name (repeatable). Otherwise
                // stay with --process_id (more precise — survives same-exe
                // restarts of the game until the heartbeat respawn catches
                // it).
                int lsfgPid = FindLosslessScalingPid(out string lsfgExe);
                _lsfgPid = lsfgPid;

                string targetClause;
                if (lsfgPid > 0 && !string.IsNullOrEmpty(_targetExe))
                {
                    targetClause = $"--process_name {_targetExe} --process_name {lsfgExe}";
                    Logger.Info($"PresentMonRunner: LSFG detected (pid={lsfgPid}, exe='{lsfgExe}'); capturing both '{_targetExe}' and '{lsfgExe}' via --process_name");
                }
                else
                {
                    targetClause = $"--process_id {_targetPid}";
                }

                var args =
                    $"--session_name {sessionName} " +
                    "--stop_existing_session " +
                    "--output_stdout " +
                    "--no_console_stats " +
                    "--track_frame_type " +
                    "--terminate_on_proc_exit " +
                    targetClause;

                Logger.Info($"PresentMonRunner: spawning for '{_targetExe}' (pid={_targetPid}), restart#{_restartCount}, session={sessionName}");

                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += OnStdoutLine;
                p.ErrorDataReceived += OnStderrLine;
                p.Exited += OnProcessExited;
                if (!p.Start())
                {
                    Logger.Warn("PresentMonRunner: Process.Start returned false");
                    _active = false;
                    return;
                }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _process = p;
                _flushTimer = new Timer(_ => { try { _metrics.FlushPerSecond(); } catch { } }, null, 1000, 1000);
                _heartbeatTimer = new Timer(_ => OnHeartbeat(), null, 5000, 5000);
            }
            catch (Exception ex)
            {
                Logger.Warn($"PresentMonRunner: spawn failed: {ex.GetType().Name}: {ex.Message}");
                _active = false;
            }
        }

        private void OnStdoutLine(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            try
            {
                if (_colFrameType < 0 && _colApplication < 0)
                {
                    if (TryParseHeader(e.Data))
                    {
                        if (!_headerLogged)
                        {
                            Logger.Info($"PresentMonRunner: CSV header parsed (FrameType@{_colFrameType}, CPUBusy@{_colCpuBusy}, GPUBusy@{_colGpuBusy}, MsBetweenPresents@{_colMsBetween})");
                            _headerLogged = true;
                        }
                        return;
                    }
                    Logger.Debug($"PresentMonRunner: non-header pre-data line: {e.Data}");
                    return;
                }
                ParseDataLine(e.Data);
            }
            catch (Exception ex)
            {
                if (Interlocked.Read(ref _linesParsed) == 0)
                    Logger.Warn($"PresentMonRunner: first line parse threw: {ex.Message}");
            }
        }

        private bool TryParseHeader(string line)
        {
            var parts = line.Split(',');
            if (parts.Length < 5) return false;
            int app = -1, pid = -1, ft = -1, ms = -1, cpu = -1, gpu = -1, rpl = -1, untilDisp = -1;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Equals("Application", StringComparison.OrdinalIgnoreCase)) app = i;
                else if (p.Equals("ProcessID", StringComparison.OrdinalIgnoreCase)) pid = i;
                else if (p.Equals("FrameType", StringComparison.OrdinalIgnoreCase)) ft = i;
                else if (p.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase)) ms = i;
                // PresentMon 2.x uses "MsCPUBusy"/"MsGPUBusy"; accept legacy "CPUBusy"/"GPUBusy" too
                else if (p.Equals("MsCPUBusy", StringComparison.OrdinalIgnoreCase)
                      || p.Equals("CPUBusy",   StringComparison.OrdinalIgnoreCase)) cpu = i;
                else if (p.Equals("MsGPUBusy", StringComparison.OrdinalIgnoreCase)
                      || p.Equals("GPUBusy",   StringComparison.OrdinalIgnoreCase)) gpu = i;
                else if (p.Equals("MsRenderPresentLatency", StringComparison.OrdinalIgnoreCase)) rpl = i;
                else if (p.Equals("MsUntilDisplayed", StringComparison.OrdinalIgnoreCase)) untilDisp = i;
            }
            // Header must include at least FrameType (Beta + --track_frame_type) and
            // MsBetweenPresents — without those there's nothing useful to do.
            if (ft < 0 && app < 0) return false;
            _colApplication = app;
            _colProcessId = pid;
            _colFrameType = ft;
            _colMsBetween = ms;
            _colCpuBusy = cpu;
            _colGpuBusy = gpu;
            _colMsRenderPresentLatency = rpl;
            _colMsUntilDisplayed = untilDisp;
            return true;
        }

        private void ParseDataLine(string line)
        {
            var parts = line.Split(',');
            FrameType ft = FrameType.Application;
            if (_colFrameType >= 0 && _colFrameType < parts.Length)
                ft = ParseFrameType(parts[_colFrameType]);

            // If this row comes from the LSFG process, re-tag as AMD_AFMF so
            // existing FG plumbing (AfmfFps, OSD [FG] badge, FrameBudget
            // hide) treats it identically. PresentMon has no native LSFG
            // FrameType — it's a userspace overlay, not a driver-level
            // generator — so the per-row classification has to happen here.
            int lsfg = _lsfgPid;
            if (lsfg > 0 && _colProcessId >= 0 && _colProcessId < parts.Length)
            {
                if (int.TryParse(parts[_colProcessId], out int rowPid) && rowPid == lsfg)
                {
                    ft = FrameType.AMD_AFMF;
                }
            }

            double ms = 0;
            if (_colMsBetween >= 0 && _colMsBetween < parts.Length)
                TryParseDouble(parts[_colMsBetween], out ms);

            double cpu = 0, gpu = 0;
            if (_colCpuBusy >= 0 && _colCpuBusy < parts.Length) TryParseDouble(parts[_colCpuBusy], out cpu);
            if (_colGpuBusy >= 0 && _colGpuBusy < parts.Length) TryParseDouble(parts[_colGpuBusy], out gpu);

            _metrics.Push(ft, ms, cpu, gpu);
            long n = Interlocked.Increment(ref _linesParsed);
            switch (ft)
            {
                case FrameType.Application: Interlocked.Increment(ref _applicationRows); break;
                case FrameType.Repeated:    Interlocked.Increment(ref _repeatedRows); break;
                case FrameType.AMD_AFMF:    Interlocked.Increment(ref _afmfRows); break;
            }
            if (n == 1 || n == 100 || n % 1000 == 0)
            {
                Logger.Info($"PresentMonRunner: parsed line #{n} (FrameType={ft}, ms={ms:F2}, cpu={cpu:F2}, gpu={gpu:F2})");
            }
        }

        private void OnHeartbeat()
        {
            try
            {
                long n = Interlocked.Read(ref _linesParsed);
                long prev = Interlocked.Exchange(ref _heartbeatLastLines, n);
                long delta = n - prev;
                int app = Interlocked.CompareExchange(ref _applicationRows, 0, 0);
                int afmf = Interlocked.CompareExchange(ref _afmfRows, 0, 0);
                int rep = Interlocked.CompareExchange(ref _repeatedRows, 0, 0);
                Logger.Info($"PresentMonRunner: heartbeat lines={n} (+{delta} in 5s), App={app}, AFMF={afmf}, Repeated={rep}");

                // Auto-recover from same-exe game restart. When the user closes
                // Forza and reopens it, the widget's RunningGame property
                // doesn't fire (same exe path) so OnRunningGameChanged never
                // calls Start with the new pid. PresentMon's
                // --terminate_on_proc_exit also doesn't reliably catch the
                // pid death — so we end up tracking a dead pid forever,
                // emitting zero rows, freezing the OSD on its last value.
                // If our target pid is no longer alive but a process with the
                // same exe name IS alive, stop the stale child and respawn
                // against the new pid.
                if (delta == 0 && _active && _targetPid > 0)
                {
                    if (!IsProcessAlive(_targetPid))
                    {
                        int replacementPid = FindProcessByName(_targetExe);
                        if (replacementPid > 0 && replacementPid != _targetPid)
                        {
                            Logger.Info($"PresentMonRunner: target pid {_targetPid} died, found replacement pid {replacementPid} for '{_targetExe}'; restarting");
                            Start(replacementPid, _targetExe);
                            return;
                        }
                        else
                        {
                            Logger.Info($"PresentMonRunner: target pid {_targetPid} died and no replacement '{_targetExe}' found; stopping");
                            Stop("target pid died");
                            return;
                        }
                    }
                }

                // LSFG midway-detect. If LSFG was running at spawn we captured
                // both names via --process_name; if not we used --process_id.
                // When LSFG starts or stops AFTER spawn, the existing capture
                // shape is wrong — either missing LSFG presents entirely, or
                // wasting a --process_name slot on a dead process. Respawn so
                // the next args reflect current reality.
                if (_active && _targetPid > 0)
                {
                    int currentLsfg = FindLosslessScalingPid(out _);
                    bool spawnedWithLsfg = _lsfgPid > 0;
                    bool nowHasLsfg = currentLsfg > 0;
                    if (spawnedWithLsfg != nowHasLsfg)
                    {
                        Logger.Info($"PresentMonRunner: LSFG state changed (spawnedWith={spawnedWithLsfg}, now={nowHasLsfg}); respawning");
                        int pidToRestart = _targetPid;
                        string exeToRestart = _targetExe;
                        Stop("LSFG state change");
                        Start(pidToRestart, exeToRestart);
                    }
                }
            }
            catch { }
        }

        private static bool IsProcessAlive(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p != null && !p.HasExited; }
            catch { return false; }
        }

        /// <summary>
        /// Looks for a Lossless Scaling process. Steam ships the app as
        /// "Lossless Scaling.exe" (with space) but some older variants /
        /// portable installs use "LosslessScaling.exe". We check both.
        /// Returns the pid (or 0) and emits the matching exe filename (with
        /// .exe) for use as a --process_name arg.
        /// </summary>
        private static int FindLosslessScalingPid(out string exeNameWithExt)
        {
            string[] candidates = new[] { "Lossless Scaling", "LosslessScaling" };
            foreach (var name in candidates)
            {
                int pid = FindProcessByName(name + ".exe");
                if (pid > 0)
                {
                    exeNameWithExt = name + ".exe";
                    return pid;
                }
            }
            exeNameWithExt = null;
            return 0;
        }

        private static int FindProcessByName(string exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return 0;
            string baseName = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? exeName.Substring(0, exeName.Length - 4)
                : exeName;
            try
            {
                var procs = Process.GetProcessesByName(baseName);
                try
                {
                    foreach (var p in procs)
                    {
                        try { if (!p.HasExited) return p.Id; } catch { }
                    }
                }
                finally
                {
                    foreach (var p in procs) { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return 0;
        }

        private static bool TryParseDouble(string s, out double v) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

        private static FrameType ParseFrameType(string raw)
        {
            string s = raw.Trim();
            if (s.Length == 0) return FrameType.Application;
            // PresentMon 2.x emits with spaces (e.g. "AMD AFMF", "Intel XeSS-FG").
            // Earlier versions / docs sometimes used underscores. Match both.
            switch (s)
            {
                case "Application": return FrameType.Application;
                case "Repeated":    return FrameType.Repeated;
                case "NotSet":      return FrameType.Application; // PresentMon 2.x sometimes emits NotSet for app frames
                case "AMD AFMF":
                case "AMD_AFMF":    return FrameType.AMD_AFMF;
                case "Intel XeSS-FG":
                case "Intel_XEFG":  return FrameType.Intel_XEFG;
                default:
                    if (byte.TryParse(s, out byte b)) return (FrameType)b;
                    return FrameType.Unknown;
            }
        }

        private void OnStderrLine(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            Logger.Info($"PresentMonRunner: stderr: {e.Data}");
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            int exitCode = -1;
            try { exitCode = _process?.ExitCode ?? -1; } catch { }
            Logger.Info($"PresentMonRunner: subprocess exited (exitCode={exitCode}, linesParsed={Interlocked.Read(ref _linesParsed)})");
            lock (_gate)
            {
                if (!_active || _disposed) return;
                if (_restartCount >= 2)
                {
                    Logger.Warn("PresentMonRunner: hit restart cap, will not respawn");
                    _active = false;
                    return;
                }
                _restartCount++;
                Timer t = null;
                t = new Timer(_ =>
                {
                    try { t.Dispose(); } catch { }
                    lock (_gate)
                    {
                        if (!_active || _disposed) return;
                        try { _process?.Dispose(); } catch { }
                        _process = null;
                        ResetColumns();
                        try { SpawnUnlocked(); }
                        catch (Exception ex) { Logger.Warn($"PresentMon restart failed: {ex.Message}"); }
                    }
                }, null, 2000, Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                StopUnlocked("dispose");
            }
        }
    }
}
