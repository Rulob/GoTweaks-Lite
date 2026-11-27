using System;
using System.Collections.Generic;
using System.Drawing;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    /// <summary>
    /// OSD item for the TDP limit (SPL, or the preset name) and the SoC's live power draw
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

        /// <summary>Live SoC draw rounded to whole watts, or -1 when unknown.</summary>
        private int SoCDrawW()
        {
            float w = performanceManager?.SoCPowerW ?? -1f;
            return w > 0 ? (int)Math.Round(w, MidpointRounding.AwayFromZero) : -1;
        }

        protected override string GetModernOSDString(int osdLevel)
        {
            if (performanceManager == null)
            {
                return string.Empty;
            }

            // The TDP limit, then the SoC's live draw in brackets with its unit:
            // "TDP 25 (17W)" in Custom, "TDP Balanced (17W)" in a preset (the firmware doesn't
            // report a preset's own limit). SPPT / FPPT are no longer shown.
            string value = MValueColor();
            string text = MLabel("TDP", "FFD54E");
            int draw = SoCDrawW();
            string dim = MDimColor();
            string drawPart = draw > 0 ? $"<C={dim}> ({draw}{MSmall("W")}<C={dim}>)<C={value}>" : "";

            if (legionManager != null && legionManager.LegionGoDetected?.Value == true)
            {
                int mode = legionManager.CurrentPerformanceMode;
                if (mode != 255)
                {
                    // "Balanced Mode" -> "Balanced", at the same size as the other values.
                    string modeName = LegionManager.GetPerformanceModeName(mode).Replace(" Mode", "");
                    return text + $"<C={value}>{modeName}" + drawPart;
                }
            }

            int spl = performanceManager.CurrentSPL;
            if (spl <= 0 && draw <= 0)
            {
                return string.Empty;
            }

            string limit = spl > 0 ? spl.ToString() : "--";
            // No reading yet: "TDP 25W".
            return text + $"<C={value}>{limit}" + (draw > 0 ? drawPart : MUnit("W"));
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
            int draw = SoCDrawW();
            string drawPart = draw > 0 ? $" ({draw}W)" : "";

            // If Legion Go is detected and not in Custom mode, show mode name instead of limits
            if (legionManager != null && legionManager.LegionGoDetected?.Value == true)
            {
                int mode = legionManager.CurrentPerformanceMode;
                if (mode != 255) // Not Custom mode
                {
                    string modeName = LegionManager.GetPerformanceModeName(mode);
                    return $"<C={labelColor}>Mode<C={tc}> <C={tc}>{modeName}{drawPart}<C={tc}>";
                }
            }

            int spl = performanceManager.CurrentSPL;
            if (spl <= 0 && draw <= 0)
            {
                return string.Empty;
            }

            // Format: "TDP 25W (17W)" - the limit, then the SoC's live draw.
            string limit = spl > 0 ? $"{spl}W" : "--";
            return $"<C={labelColor}>TDP<C={tc}> <C={tc}>{limit}{drawPart}<C={tc}>";
        }    }
}
