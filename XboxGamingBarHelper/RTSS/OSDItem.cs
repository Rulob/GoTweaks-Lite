using System;
using System.Collections.Generic;
using System.Drawing;

namespace XboxGamingBarHelper.RTSS
{
    internal abstract class OSDItem
    {
        protected string name;
        protected string id;
        protected string colorCode;
        protected string defaultColorCode;  // Store the original color
        protected string textColor = "FFFFFF";
        protected bool useDynamicColor = false;
        protected int opacity = 100;  // OLED protection opacity (10-100)

        public string Id => id;

        public void SetLabelColor(string color)
        {
            if (!string.IsNullOrEmpty(color) && color != "DEFAULT")
            {
                colorCode = color;
            }
            else
            {
                // Reset to default color
                colorCode = defaultColorCode;
            }
        }

        public void SetTextColor(string color)
        {
            if (color == "DYNAMIC")
            {
                useDynamicColor = true;
                textColor = "FFFFFF"; // Default fallback
            }
            else
            {
                useDynamicColor = false;
                textColor = color;
            }
        }

        public void SetOpacity(int opacityValue)
        {
            opacity = Math.Max(10, Math.Min(100, opacityValue));
        }

        /// <summary>
        /// Gets the text color with opacity applied.
        /// Use this in custom GetOSDString implementations instead of textColor directly.
        /// </summary>
        protected string GetTextColorWithOpacity()
        {
            // When using dynamic color, textColor is just "FFFFFF" without opacity
            // When not using dynamic color, textColor already has opacity from RTSSManager
            return useDynamicColor ? ApplyOpacity(textColor) : textColor;
        }

        /// <summary>
        /// Applies opacity to a hex color for OLED protection.
        /// </summary>
        protected string ApplyOpacity(string hexColor)
        {
            if (opacity >= 100 || string.IsNullOrEmpty(hexColor) || hexColor.Length < 6)
                return hexColor;

            try
            {
                float factor = opacity / 100f;
                byte r = (byte)(Convert.ToByte(hexColor.Substring(0, 2), 16) * factor);
                byte g = (byte)(Convert.ToByte(hexColor.Substring(2, 2), 16) * factor);
                byte b = (byte)(Convert.ToByte(hexColor.Substring(4, 2), 16) * factor);
                return $"{r:X2}{g:X2}{b:X2}";
            }
            catch
            {
                return hexColor;
            }
        }

        protected OSDItem()
        {
            name = "OSD Item";
            id = "Unknown";
            colorCode = "FFFFFF";
            defaultColorCode = "FFFFFF";
        }

        protected OSDItem(string name, Color color) : this(name, name, color)
        {
        }

        protected OSDItem(string name, string id, Color color)
        {
            this.name = name;
            this.id = id;
            this.colorCode = $"{color.R:X2}{color.G:X2}{color.B:X2}";
            this.defaultColorCode = this.colorCode;  // Store the default
        }

        // ===== Modern style =====
        //
        // A calmer, more readable layout than the classic one:
        //  - small, baseline-aligned labels and units, full-size numbers (RTSS "<S=-75>" style
        //    sizes, the same tag RTSS's own horizontal layout uses for its units);
        //  - soft accent colours for labels and a neutral value colour that only changes when a
        //    value crosses a real warning threshold (instead of a rainbow of every reading);
        //  - roughly fixed-width number fields (padding spaces) so the bar doesn't jitter as
        //    digit counts change.
        // Items that don't implement GetModernOSDString simply keep their classic output.
        protected bool modernStyle = false;
        protected int baseTextSize = 100;   // percent, the global OSD text size

        private const string ModernNeutralColor = "F1F5F9";
        private const string ModernDimColor = "94A3B8";
        private const string ModernWarnColor = "FCD34D";
        private const string ModernHotColor = "FCA5A5";

        public void SetModernStyle(bool enabled)
        {
            modernStyle = enabled;
        }

        public void SetBaseTextSize(int size)
        {
            baseTextSize = Math.Max(10, size);
        }

        /// <summary>What the manager calls: Modern output when it's on and the item has one.</summary>
        public string Render(int osdLevel)
        {
            if (modernStyle)
            {
                var modern = GetModernOSDString(osdLevel);
                if (modern != null) return modern;
            }
            return GetOSDString(osdLevel);
        }

        /// <summary>Modern-style text for this item, or null to use the classic output.</summary>
        protected virtual string GetModernOSDString(int osdLevel)
        {
            return null;
        }

        /// <summary>Value colour: a calm off-white, or the user's fixed text colour if they set one.</summary>
        protected string MValueColor()
        {
            return useDynamicColor ? ApplyOpacity(ModernNeutralColor) : textColor;
        }

        protected string MDimColor()
        {
            return ApplyOpacity(ModernDimColor);
        }

        /// <summary>Label colour: the item's modern accent unless the user picked a label colour.</summary>
        protected string MLabelColor(string accentHex)
        {
            return ApplyOpacity(colorCode != defaultColorCode ? colorCode : accentHex);
        }

        /// <summary>Neutral until <paramref name="warn"/>, amber from there, soft red from <paramref name="hot"/>.</summary>
        protected string MHighIsBad(double value, double warn, double hot)
        {
            if (!useDynamicColor) return textColor;
            if (value >= hot) return ApplyOpacity(ModernHotColor);
            if (value >= warn) return ApplyOpacity(ModernWarnColor);
            return MValueColor();
        }

        /// <summary>Same idea for values where LOW is the problem (battery).</summary>
        protected string MLowIsBad(double value, double warn, double hot)
        {
            if (!useDynamicColor) return textColor;
            if (value <= hot) return ApplyOpacity(ModernHotColor);
            if (value <= warn) return ApplyOpacity(ModernWarnColor);
            return MValueColor();
        }

        /// <summary>Returns the text size to the user's chosen size (RTSS "&lt;S&gt;" alone means 100%).</summary>
        protected string SizeReset()
        {
            return baseTextSize != 100 ? $"<S={baseTextSize}>" : "<S>";
        }

        /// <summary>Smaller text sitting on the same baseline as the numbers.</summary>
        protected string MSmall(string text, int percent = 75)
        {
            return $"<S=-{percent}>{text}{SizeReset()}";
        }

        /// <summary>Small accent label followed by a space, e.g. "CPU ".</summary>
        protected string MLabel(string text, string accentHex)
        {
            // 75%, not 72%: RTSS places small text on the baseline by rounding the scaled font
            // size, and at 72% the labels landed one pixel LOWER than the numbers (measured on a
            // screenshot), while 75% (the unit size) and 88% sit exactly on the baseline.
            return $"<C={MLabelColor(accentHex)}>{MSmall(text, 75)}<C={MValueColor()}> ";
        }

        /// <summary>Small dim unit, e.g. "%" or "W". Leaves the colour on the value colour.</summary>
        protected string MUnit(string text)
        {
            return $"<C={MDimColor()}>{MSmall(text)}<C={MValueColor()}>";
        }

        /// <summary>
        /// A number in a roughly fixed-width field: padded on the left with plain spaces up to
        /// <paramref name="minDigits"/> integer digits, so "9" and "45" take about the same room.
        /// Two spaces are about one digit wide in Segoe UI. (An earlier version padded with
        /// fully transparent digits, but RTSS draws those visibly, giving "09%" and "07.3".)
        /// </summary>
        protected string MNum(double value, int minDigits, string colorHex, bool oneDecimal = false)
        {
            double rounded = oneDecimal ? Math.Round(value, 1, MidpointRounding.AwayFromZero)
                                        : Math.Round(value, MidpointRounding.AwayFromZero);
            double abs = Math.Abs(Math.Floor(rounded));
            int digits = abs < 10 ? 1 : (abs < 100 ? 2 : (abs < 1000 ? 3 : 4));
            string pad = digits < minDigits ? new string(' ', 2 * (minDigits - digits)) : "";
            string text = rounded.ToString(oneDecimal ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);
            return $"<C={colorHex}>{pad}{text}";
        }

        public virtual string GetOSDString(int osdLevel)
        {
            var osdValues = GetValues(osdLevel);

            if (osdValues == null || osdValues.Count == 0)
            {
                return string.Empty;
            }

            var tc = GetTextColorWithOpacity();
            var osdString = $"{GetNameString()} ";

            if (osdValues == null || osdValues.Count == 0)
            {
                return osdString + " N/A";
            }

            for (int i = 0; i < osdValues.Count; i++)
            {
                var osdValue = osdValues[i];
                if (osdValue.Value < 0)
                {
                    osdString += $"<C={tc}>N/A";
                }
                else
                {
                    var valueColor = GetValueColor(osdValue);
                    osdString += $"<C={valueColor}>{osdValue.Prefix}{osdValue.FormattedValue}{osdValue.Unit}";
                }
                if (i < osdValues.Count - 1)
                {
                    osdString += " ";
                }
            }

            // Reset to text color at end
            osdString += $"<C={tc}>";

            return osdString;
        }

        protected virtual string GetNameString()
        {
            return $"<C={ApplyOpacity(colorCode)}>{name}<C={GetTextColorWithOpacity()}>";
        }

        protected virtual List<OSDItemValue> GetValues(int osdLevel)
        {
            return new List<OSDItemValue>();
        }

        /// <summary>
        /// Gets the color for a value based on its type and the dynamic color setting.
        /// Applies opacity for OLED protection.
        /// </summary>
        protected string GetValueColor(OSDItemValue value)
        {
            if (!useDynamicColor || value.ValueType == OSDValueType.None)
            {
                // When not using dynamic color, textColor already has opacity from RTSSManager
                // When using dynamic color but value type is None, apply opacity to default white
                return useDynamicColor ? ApplyOpacity(textColor) : textColor;
            }

            var dynamicColor = value.ValueType switch
            {
                OSDValueType.Temperature => GetTemperatureColor(value.Value),
                OSDValueType.Percentage => GetPercentageColor(value.Value),
                OSDValueType.PercentageInv => GetPercentageInvColor(value.Value),
                OSDValueType.Wattage => GetWattageColor(value.Value),
                OSDValueType.Speed => textColor, // Speed doesn't change color
                _ => textColor
            };

            return ApplyOpacity(dynamicColor);
        }

        /// <summary>
        /// Temperature color: Blue (cold) -> Green (normal) -> Yellow (warm) -> Red (hot)
        /// </summary>
        private string GetTemperatureColor(float temp)
        {
            if (temp < 45) return "0080FF";      // Blue - cold
            if (temp < 55) return "00FF80";      // Cyan-green - cool
            if (temp < 65) return "00FF00";      // Green - normal
            if (temp < 75) return "80FF00";      // Yellow-green - getting warm
            if (temp < 80) return "FFFF00";      // Yellow - warm
            if (temp < 85) return "FF8000";      // Orange - hot
            return "FF0000";                      // Red - very hot
        }

        /// <summary>
        /// Percentage color (for usage): Green (low) -> Yellow (mid) -> Red (high)
        /// </summary>
        private string GetPercentageColor(float percent)
        {
            if (percent < 30) return "00FF00";   // Green - low usage
            if (percent < 50) return "80FF00";   // Yellow-green
            if (percent < 70) return "FFFF00";   // Yellow - moderate
            if (percent < 85) return "FF8000";   // Orange - high
            return "FF0000";                      // Red - very high
        }

        /// <summary>
        /// Inverted percentage color (for battery): Red (low) -> Yellow (mid) -> Green (high)
        /// </summary>
        private string GetPercentageInvColor(float percent)
        {
            if (percent < 15) return "FF0000";   // Red - critical
            if (percent < 30) return "FF8000";   // Orange - low
            if (percent < 50) return "FFFF00";   // Yellow - mid
            if (percent < 70) return "80FF00";   // Yellow-green
            return "00FF00";                      // Green - good
        }

        /// <summary>
        /// Wattage color: Green (low) -> Yellow (mid) -> Red (high)
        /// Based on typical handheld TDP ranges (5-30W)
        /// </summary>
        private string GetWattageColor(float watts)
        {
            if (watts < 8) return "00FF00";      // Green - low power
            if (watts < 15) return "80FF00";     // Yellow-green
            if (watts < 20) return "FFFF00";     // Yellow - moderate
            if (watts < 25) return "FF8000";     // Orange - high
            return "FF0000";                      // Red - very high
        }
    }
}
