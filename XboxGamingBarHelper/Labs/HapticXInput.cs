using System.Runtime.InteropServices;

namespace XboxGamingBarHelper.Labs
{
    /// <summary>
    /// The two XInput 1.4 calls GoTweaks Haptics needs: find which XInput slot belongs to the
    /// Legion controller (by USB vendor id) and drive that slot's rumble motors.
    /// </summary>
    internal static class HapticXInput
    {
        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        public static extern uint SetState(uint dwUserIndex, ref HapticXInputVibration pVibration);

        // Undocumented but stable export at ordinal 108 in xinput1_4.dll. Same export
        // is used by DS4Windows / Steam / SDL2 to map an XInput slot back to its USB
        // VID:PID. Reserved=1, Flags=1 (XINPUT_FLAG_GAMEPAD).
        [DllImport("xinput1_4.dll", EntryPoint = "#108")]
        public static extern uint GetCapabilitiesEx(
            uint dwReserved,
            uint dwUserIndex,
            uint dwFlags,
            ref HapticXInputCapabilitiesEx pCapabilitiesEx);

        public const uint ErrorSuccess = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HapticXInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HapticXInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    // XINPUT_CAPABILITIES (20 bytes) + EX trailer (12 bytes) = 32 bytes total.
    [StructLayout(LayoutKind.Sequential)]
    internal struct HapticXInputCapabilitiesEx
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public HapticXInputGamepad Gamepad;       // 12 bytes
        public HapticXInputVibration Vibration;   // 4 bytes
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
        public ushort Unk1;
        public uint Unk2;
    }
}
