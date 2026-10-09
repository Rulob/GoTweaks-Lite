using System.Collections.Generic;
using System.Drawing;
using XboxGamingBarHelper.Performance;

namespace XboxGamingBarHelper.RTSS.OSDItems
{
    internal class OSDItemMemory : OSDItem
    {
        private HardwareSensor memoryUsageSensor;
        private HardwareSensor memoryUsedSensor;
        private HardwareSensor memoryAvailableSensor;

        public OSDItemMemory(HardwareSensor memoryUsageSensor, HardwareSensor memoryUsedSensor, HardwareSensor memoryAvailableSensor) : base("RAM", "Memory", Color.Purple)
        {
            this.memoryUsageSensor = memoryUsageSensor;
            this.memoryUsedSensor = memoryUsedSensor;
            this.memoryAvailableSensor = memoryAvailableSensor;
        }

        protected override string GetModernOSDString(int osdLevel)
        {
            // "RAM 42%" - percentage only; amber from 85%, soft red from 95%.
            float percent = memoryUsageSensor.Value;
            string text = MLabel("RAM", "FFACD9");
            if (percent < 0) return text + $"<C={MValueColor()}>--";
            return text + MNum(percent, 2, MHighIsBad(percent, 85, 95)) + MUnit("%");
        }

        protected override List<OSDItemValue> GetValues(int osdLevel)
        {
            var osdItems = base.GetValues(osdLevel);

            // Show memory usage percentage, used and total with 1 decimal place
            osdItems.Add(new OSDItemValue(memoryUsageSensor.Value, "% ", OSDValueType.Percentage));
            osdItems.Add(new OSDItemValue(memoryUsedSensor.Value, "/", OSDValueType.None, 1));
            osdItems.Add(new OSDItemValue(memoryUsedSensor.Value + memoryAvailableSensor.Value, "GB", OSDValueType.None, 1));

            return osdItems;
        }
    }
}
