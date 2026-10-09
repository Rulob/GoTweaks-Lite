using System.Collections.Generic;
using System.Drawing;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    /// <summary>
    /// OSD item for the SoC temperature. The CPU and GPU share one chip (an APU), so a single
    /// temperature is more useful than two near-identical ones. It reads the CPU package
    /// temperature (Tctl) and falls back to the GPU sensor if the CPU one isn't available.
    /// </summary>
    internal class OSDItemSoCTemp : OSDItem
    {
        private readonly HardwareSensor cpuTemperatureSensor;
        private readonly HardwareSensor gpuTemperatureSensor;

        public OSDItemSoCTemp(HardwareSensor cpuTemperatureSensor, HardwareSensor gpuTemperatureSensor) : base("TEMP", "SoCTemp", Color.Orange)
        {
            this.cpuTemperatureSensor = cpuTemperatureSensor;
            this.gpuTemperatureSensor = gpuTemperatureSensor;
        }

        /// <summary>The SoC temperature in °C, or -1 when no sensor has a reading.</summary>
        private float ReadTemperature()
        {
            float cpu = cpuTemperatureSensor != null ? cpuTemperatureSensor.Value : -1;
            if (cpu > 0) return cpu;
            float gpu = gpuTemperatureSensor != null ? gpuTemperatureSensor.Value : -1;
            return gpu > 0 ? gpu : -1;
        }

        protected override string GetModernOSDString(int osdLevel)
        {
            // "TEMP 62°C": neutral, amber from 80°C, soft red from 90°C.
            string text = MLabel("TEMP", "FFBB75");
            float temp = ReadTemperature();
            if (temp < 0) return text + $"<C={MValueColor()}>--";
            return text + MNum(temp, 2, MHighIsBad(temp, 80, 90)) + MUnit("°C");
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = base.GetValues(osdLevel);
            osdItems.Add(new OSDItemValue(ReadTemperature(), "C", OSDValueType.Temperature));
            return osdItems;
        }
    }
}
