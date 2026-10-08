using System;
using System.Drawing;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    /// <summary>
    /// OSD item for displaying the current time in 12-hour format (h:mm AM/PM).
    /// Separate, independently toggleable item from OSDItemTime (24-hour) - see
    /// Function.OSDConfig / GamingWidget.OSDCustomization.cs "Time12H".
    /// </summary>
    internal class OSDItemTime12H : OSDItem
    {
        public OSDItemTime12H() : base("TIME", "Time12H", Color.White)
        {
        }

        protected override string GetModernOSDString(int osdLevel)
        {
            // No label; same color as the other values.
            return $"<C={MValueColor()}>{DateTime.Now.ToString("h:mm tt")}";
        }

        public override string GetOSDString(int osdLevel)
        {
            var now = DateTime.Now;
            var timeString = now.ToString("h:mm tt"); // e.g., "2:30 PM"
            return $"<C={GetTextColorWithOpacity()}>{timeString}";
        }
    }
}
