using System.Collections.Generic;
using System.Drawing;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemCPU : OSDItem
    {
        private HardwareSensor cpuUsageSensor;
        private HardwareSensor cpuClockSensor;
        private HardwareSensor cpuWattageSensor;
        private HardwareSensor cpuTemperatureSensor;

        private bool showClock = false;

        public OSDItemCPU(HardwareSensor cpuUsageSensor, HardwareSensor cpuClockSensor, HardwareSensor cpuWattageSensor, HardwareSensor cpuTemperatureSensor) : base("CPU", "CPU", Color.Turquoise)
        {
            this.cpuWattageSensor = cpuWattageSensor;
            this.cpuUsageSensor = cpuUsageSensor;
            this.cpuClockSensor = cpuClockSensor;
            this.cpuTemperatureSensor = cpuTemperatureSensor;
        }

        public void SetShowClock(bool show)
        {
            showClock = show;
        }

        protected override string GetModernOSDString(int osdLevel)
        {
            // "CPU 45%" - usage only. Wattage and temperature are no longer shown (the SoC
            // temperature has its own item); the clock still appears if that item is enabled.
            string value = MValueColor();
            string text = MLabel("CPU", "7DD3FC");

            float usage = cpuUsageSensor.Value;
            text += usage < 0 ? $"<C={value}>--" : MNum(usage, 2, value) + MUnit("%");

            if (showClock && cpuClockSensor.Value > 0)
            {
                text += $"<C={value}>  " + MNum(cpuClockSensor.Value / 1000.0, 1, value, oneDecimal: true) + MUnit("GHz");
            }
            return text;
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = base.GetValues(osdLevel);

            // Show CPU usage, wattage and temperature when enabled
            osdItems.Add(new OSDItemValue(cpuUsageSensor.Value, "%", OSDValueType.Percentage));
            osdItems.Add(new OSDItemValue(cpuWattageSensor.Value, "W", OSDValueType.Wattage));
            osdItems.Add(new OSDItemValue(cpuTemperatureSensor.Value, "C", OSDValueType.Temperature));

            // Show clock speed if enabled separately
            if (showClock)
            {
                osdItems.Add(new OSDItemValue(cpuClockSensor.Value, "MHz", OSDValueType.Speed));
            }

            return osdItems;
        }
    }
}
