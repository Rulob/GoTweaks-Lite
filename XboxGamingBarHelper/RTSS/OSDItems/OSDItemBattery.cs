using Shared.Constants;
using System;
using System.Collections.Generic;
using System.Drawing;
using Windows.System.Power;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemBattery : OSDItem
    {
        private HardwareSensor batteryPercentSensor;
        private HardwareSensor batteryDischargeRateSensor;
        private HardwareSensor batteryChargeRateSensor;
        private HardwareSensor batteryRemainTimeSensor;
        private Func<float> getTimeToFull;

        public OSDItemBattery(HardwareSensor batteryPercentSensor, HardwareSensor batteryDischargeRateSensor, HardwareSensor batteryChargeRateSensor, HardwareSensor batteryRemainTimeSensor, Func<float> getTimeToFull = null) : base("BATT", "Battery", Color.DarkCyan)
        {
            this.batteryPercentSensor = batteryPercentSensor;
            this.batteryDischargeRateSensor = batteryDischargeRateSensor;
            this.batteryChargeRateSensor = batteryChargeRateSensor;
            this.batteryRemainTimeSensor = batteryRemainTimeSensor;
            this.getTimeToFull = getTimeToFull;
        }

        protected override string GetModernOSDString(int osdLevel)
        {
            // "BAT 88%  -12.4W  1h 23m": neutral until the battery gets low (amber at 30%, soft
            // red at 15%). The rate and time left/to full use the same neutral color as every
            // other number; only their small units are dim.
            string value = MValueColor();
            string text = MLabel("BAT", "BEF264");

            float level = batteryPercentSensor.Value;
            text += level < 0 ? $"<C={value}>--" : MNum(level, 2, MLowIsBad(level, 30, 15)) + MUnit("%");

            bool discharging = batteryDischargeRateSensor.Value > 0;
            bool charging = batteryChargeRateSensor.Value > 0;

            if (discharging)
            {
                text += $"<C={value}>  -" + MNum(batteryDischargeRateSensor.Value, 1, value, oneDecimal: true) + MUnit("W");
            }
            else if (charging)
            {
                text += $"<C={value}>  +" + MNum(batteryChargeRateSensor.Value, 1, value, oneDecimal: true) + MUnit("W");
            }
            else
            {
                try
                {
                    var powerSupply = PowerManager.PowerSupplyStatus;
                    if (powerSupply == PowerSupplyStatus.Adequate || powerSupply == PowerSupplyStatus.Inadequate)
                    {
                        text += $"<C={value}>  AC";
                    }
                }
                catch
                {
                    // PowerManager unavailable - show nothing extra.
                }
            }

            // Time left (discharging) or time to full (charging).
            float seconds = 0;
            if (charging && getTimeToFull != null) seconds = getTimeToFull();
            else if (discharging) seconds = batteryRemainTimeSensor.Value;

            if (seconds > 0)
            {
                int totalMinutes = (int)Math.Round(seconds / MathConstants.SECONDS_PER_MINUTE);
                int hours = totalMinutes / 60;
                int minutes = totalMinutes % 60;
                text += $"<C={value}>  ";
                if (hours > 0)
                {
                    text += $"{hours}" + MUnit("h") + $"<C={value}> ";
                }
                text += $"{minutes}" + MUnit("m");
            }
            return text;
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = base.GetValues(osdLevel);

            // Always show battery info when enabled (inverted % - green when full, red when low)
            osdItems.Add(new OSDItemValue(batteryPercentSensor.Value, "%", OSDValueType.PercentageInv));

            bool isDischarging = batteryDischargeRateSensor.Value > 0;
            bool isCharging = batteryChargeRateSensor.Value > 0;

            if (isDischarging)
            {
                osdItems.Add(new OSDItemValue(batteryDischargeRateSensor.Value, "W/H", "-", OSDValueType.Wattage));
            }
            else if (isCharging)
            {
                osdItems.Add(new OSDItemValue(batteryChargeRateSensor.Value, "W/H", "+", OSDValueType.None));
            }
            else
            {
                // Not charging or discharging - check if on AC power
                try
                {
                    var powerSupply = PowerManager.PowerSupplyStatus;
                    if (powerSupply == PowerSupplyStatus.Adequate || powerSupply == PowerSupplyStatus.Inadequate)
                    {
                        // On AC power but not actively charging (battery full or trickle charging)
                        osdItems.Add(new OSDItemValue(0, "", "AC", OSDValueType.None));
                    }
                }
                catch
                {
                    // Fallback if PowerManager unavailable
                }
            }

            // Show time remaining (discharge) or time to full (charging)
            if (isCharging && getTimeToFull != null)
            {
                // When charging, show time to full
                float timeToFull = getTimeToFull();
                if (timeToFull > 0)
                {
                    var hours = Math.Floor(timeToFull / MathConstants.SECONDS_PER_HOUR);
                    var minutes = (timeToFull - hours * MathConstants.SECONDS_PER_HOUR) / MathConstants.SECONDS_PER_MINUTE;
                    if (hours > 0)
                    {
                        osdItems.Add(new OSDItemValue((float)hours, "H", OSDValueType.None));
                    }
                    osdItems.Add(new OSDItemValue((float)minutes, "M", "~", OSDValueType.None)); // ~ indicates estimate to full
                }
            }
            else if (isDischarging && batteryRemainTimeSensor.Value > 0)
            {
                // When discharging, show time remaining
                var hours = Math.Floor(batteryRemainTimeSensor.Value / MathConstants.SECONDS_PER_HOUR);
                var minutes = (batteryRemainTimeSensor.Value - hours * MathConstants.SECONDS_PER_HOUR) / MathConstants.SECONDS_PER_MINUTE;
                osdItems.Add(new OSDItemValue((float)hours, "H", OSDValueType.None));
                osdItems.Add(new OSDItemValue((float)minutes, "M", OSDValueType.None));
            }

            return osdItems;
        }
    }
}
