using RTSSSharedMemoryNET;
using System.Drawing;
using XboxGamingBarHelper.Windows;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemFPS : OSDItem
    {
        public OSDItemFPS() : base("FPS", "FPS", Color.Red)
        {
        }

        protected override string GetModernOSDString(int osdLevel)
        {
            // "FPS 118  8.5ms": big number, dim smaller frametime. With frame generation:
            // "FPS 60 / 120 FG". Numbers sit in fixed-width fields so the bar doesn't jitter.
            string value = MValueColor();
            string dim = MDimColor();
            string text = MLabel("FPS", "7AFFCA");

            var pm = Program.PresentMonMetrics;
            if (pm != null && pm.IsLive() && pm.RenderedFps > 0)
            {
                int rendered = pm.RenderedFps;
                int displayed = pm.DisplayedFps;
                bool frameGen = pm.AfmfFps > 0 && displayed > rendered;

                if (frameGen)
                {
                    text += MNum(rendered, 2, value) + $"<C={dim}> / " + MNum(displayed, 3, value) + MUnit(" FG");
                }
                else
                {
                    text += MNum(rendered, 3, value);
                }

                // Whole milliseconds, same color as the other numbers.
                double frametime = pm.FrametimeAvgMs;
                if (frametime > 0)
                {
                    text += $"<C={value}>  " + MNum(frametime, 2, value) + MUnit("ms");
                }
                return text;
            }

            // PresentMon isn't supplying frame stats (not attached yet, or no application frames in
            // the last second): use RTSS's own per-game framerate, and derive the frametime from it,
            // so the line looks the same - whole milliseconds, never RTSS's decimal "<FT>" text.
            if (TryGetRtssFps(out int rtssFps))
            {
                return text + MNum(rtssFps, 3, value) + $"<C={value}>  " + MNum(1000.0 / rtssFps, 2, value) + MUnit("ms");
            }

            return text + $"<C={value}>--";
        }

        /// <summary>
        /// The framerate RTSS measures for the game being drawn on: the foreground app if RTSS has
        /// it, otherwise the busiest hooked app. False when RTSS isn't running or has no frames.
        /// </summary>
        private static bool TryGetRtssFps(out int fps)
        {
            fps = 0;
            try
            {
                var entries = OSD.GetAppEntries(AppFlags.MASK);
                if (entries == null || entries.Length == 0) return false;

                int foregroundPid = User32.GetForegroundProcessId();
                AppEntry best = null;
                foreach (var entry in entries)
                {
                    if (entry.InstantaneousFrames == 0) continue;
                    if (entry.ProcessId == foregroundPid) { best = entry; break; }
                    if (best == null || entry.InstantaneousFrames > best.InstantaneousFrames) best = entry;
                }

                if (best == null) return false;
                fps = (int)best.InstantaneousFrames;
                return fps > 0;
            }
            catch
            {
                // RTSS not running / shared memory not ready: no data this tick.
                return false;
            }
        }

        public override string GetOSDString(int osdLevel)
        {
            // FPS and frametime - uses text color, yellow for frametime then back to text color
            // Apply opacity to all colors for OLED protection
            var tc = GetTextColorWithOpacity();
            var yellow = ApplyOpacity("FFFF00");

            // #66: when PresentMon is supplying live frame stats AND it sees a
            // frame-generated source (AMD AFMF / Intel XeSS-FG / Lossless
            // Scaling), render a "rendered / displayed" pair using the literal
            // values. RTSS prints whatever string we hand it. Otherwise fall
            // back to the RTSS &lt;FR&gt; substitution, which still works for
            // non-AFMF games and as a safety net when PresentMon is
            // unavailable.
            var pm = Program.PresentMonMetrics;
            if (pm != null && pm.IsLive())
            {
                int rendered = pm.RenderedFps;
                int displayed = pm.DisplayedFps;
                int afmf = pm.AfmfFps;
                // Both numbers are FPS, not panel Hz — rendered is what the
                // game submits, displayed is the rate at which unique
                // buffer changes hit the swap chain (including FG-generated
                // frames). Using "Hz" here confuses users into thinking it
                // means the panel refresh rate.
                if (afmf > 0 && displayed > rendered)
                {
                    // [FG] badge tells the user the displayed number is
                    // frame-generated (AMD AFMF / Intel XeSS-FG / Lossless
                    // Scaling re-tagged here), not a real render rate.
                    return $"<C={tc}>{rendered} / {displayed} fps <C={yellow}>[FG] <FT> ms<C={tc}>";
                }
                if (rendered > 0)
                {
                    return $"<C={tc}>{rendered} fps <C={yellow}><FT> ms<C={tc}>";
                }
            }
            return $"<C={tc}><FR> FPS <C={yellow}><FT> ms<C={tc}>";
        }
    }
}
