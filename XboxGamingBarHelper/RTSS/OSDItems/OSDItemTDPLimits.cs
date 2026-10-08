using System.Collections.Generic;
using System.Drawing;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    /// <summary>
    /// OSD item for displaying current TDP limits (SPL/SPPT/FPPT) or mode name
    /// </summary>
    internal class OSDItemTDPLimits : OSDItem
    {
        private PerformanceManager performanceManager;
        private LegionManager legionManager;

        public OSDItemTDPLimits() : base("Limits", "TDPLimits", Color.Orange)
        {
        }

        /// <summary>
        /// Sets the Performance Manager reference to read TDP limits from.
        /// Must be called after PerformanceManager is initialized.
        /// </summary>
        public void SetPerformanceManager(PerformanceManager manager)
        {
            performanceManager = manager;
        }

        /// <summary>
        /// Sets the Legion Manager reference to read performance mode from.
        /// Must be called after LegionManager is initialized.
        /// </summary>
        public void SetLegionManager(LegionManager manager)
        {
            legionManager = manager;
        }

        protected override string GetModernOSDString(int osdLevel)
        {
            if (performanceManager == null)
            {
                return string.Empty;
            }

            // "TDP Balanced" in a preset mode, "TDP 25/26/28W" in Custom (SPL/SPPT/FPPT).
            string value = MValueColor();
            string text = MLabel("TDP", "FCD34D");

            if (legionManager != null && legionManager.LegionGoDetected?.Value == true)
            {
                int mode = legionManager.CurrentPerformanceMode;
                if (mode != 255)
                {
                    // "Balanced Mode" -> a smaller "Balanced" (the word at full size looked bigger than
                    // the numbers around it). 75% like the labels and units: measured on screenshots,
                    // 75% and 100% sit exactly on the baseline, whereas 88% landed one pixel high.
                    string modeName = LegionManager.GetPerformanceModeName(mode).Replace(" Mode", "");
                    return text + $"<C={value}>" + MSmall(modeName, 75);
                }
            }

            int spl = performanceManager.CurrentSPL;
            int sppt = performanceManager.CurrentSPPT;
            int fppt = performanceManager.CurrentFPPT;
            if (spl == 0 && sppt == 0 && fppt == 0)
            {
                return string.Empty;
            }

            return text + $"<C={value}>{spl}/{sppt}/{fppt}" + MUnit("W");
        }

        public override string GetOSDString(int osdLevel)
        {
            if (performanceManager == null)
            {
                return string.Empty;
            }

            // Apply opacity to label and text colors for OLED protection
            var labelColor = ApplyOpacity(colorCode);
            var tc = GetTextColorWithOpacity();

            // If Legion Go is detected and not in Custom mode, show mode name instead of limits
            if (legionManager != null && legionManager.LegionGoDetected?.Value == true)
            {
                int mode = legionManager.CurrentPerformanceMode;
                if (mode != 255) // Not Custom mode
                {
                    string modeName = LegionManager.GetPerformanceModeName(mode);
                    return $"<C={labelColor}>Mode<C={tc}> <C={tc}>{modeName}<C={tc}>";
                }
            }

            int spl = performanceManager.CurrentSPL;
            int sppt = performanceManager.CurrentSPPT;
            int fppt = performanceManager.CurrentFPPT;

            // Don't show if no TDP has been set yet
            if (spl == 0 && sppt == 0 && fppt == 0)
            {
                return string.Empty;
            }

            // Format: "Limits: SPL/SPPT/FPPT" e.g. "Limits: 25/26/28"
            return $"<C={labelColor}>Limits<C={tc}> <C={tc}>{spl}/{sppt}/{fppt}W<C={tc}>";
        }
    }
}
