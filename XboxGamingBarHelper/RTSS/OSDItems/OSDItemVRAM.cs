using System.Collections.Generic;
using System.Drawing;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemVRAM : OSDItem
    {
        private HardwareSensor gpuMemoryUsedSensor;
        private HardwareSensor gpuMemoryFreeSensor;
        private HardwareSensor gpuMemoryClockSensor;

        public OSDItemVRAM(HardwareSensor gpuMemoryUsedSensor, HardwareSensor gpuMemoryFreeSensor, HardwareSensor gpuMemoryClockSensor) : base("VRAM", "VRAM", Color.Cyan)
        {
            this.gpuMemoryUsedSensor = gpuMemoryUsedSensor;
            this.gpuMemoryFreeSensor = gpuMemoryFreeSensor;
            this.gpuMemoryClockSensor = gpuMemoryClockSensor;
        }

        protected override string GetModernOSDString(int osdLevel)
        {
            // "VRAM 35%" - percentage only (no sizes, no memory clock).
            string text = MLabel("VRAM", "69EEFF");
            float used = gpuMemoryUsedSensor.Value;
            float total = used + gpuMemoryFreeSensor.Value;
            if (used < 0 || total <= 0) return text + $"<C={MValueColor()}>--";
            double percent = used / total * 100.0;
            return text + MNum(percent, 2, MHighIsBad(percent, 85, 95)) + MUnit("%");
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = base.GetValues(osdLevel);

            // Show VRAM used, total and clock with 1 decimal place for memory
            osdItems.Add(new OSDItemValue(gpuMemoryUsedSensor.Value / 1024f, "/", OSDValueType.None, 1)); // Convert MB to GB
            osdItems.Add(new OSDItemValue((gpuMemoryUsedSensor.Value + gpuMemoryFreeSensor.Value) / 1024f, "GB ", OSDValueType.None, 1));
            osdItems.Add(new OSDItemValue(gpuMemoryClockSensor.Value, "MHz", OSDValueType.Speed));

            return osdItems;
        }
    }
}
