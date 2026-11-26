namespace XboxGamingBar
{
    // Fan card (Legion tab): live fan speed and CPU temperature. Since 1.9.7c the fan always
    // follows the firmware curve of the TDP mode (Lenovo's stock curve in Custom, written by the
    // helper); the fan curve editor, its graph and the per-mode curves were removed.
    public sealed partial class GamingWidget
    {
        // The true CPU temperature (Tctl/Tdie), not the EC's fan-control sensor (0x01, which
        // reads a chipset/board area and was being mistaken for the CPU temp).
        private void OnCPUTempUpdated(int tempC)
        {
            if (FanHeaderTempLabel != null) FanHeaderTempLabel.Text = $"{tempC}°C";
        }

        // The EC fan-control sensor isn't shown.
        private void OnFanSensorTempUpdated(int tempC)
        {
        }

        private void OnFanRPMUpdated(int rpm)
        {
            if (FanRPMLabel != null) FanRPMLabel.Text = $"{rpm} RPM";
        }
    }
}
