using System.Globalization;

namespace Shared.Data
{
    /// <summary>
    /// Legion Go / Go 2 controller input modes (the Legion Space X-Input / D-Input switch),
    /// and how to tell which one the controllers are in.
    ///
    /// The controllers re-enumerate on USB under a different product ID whenever the mode
    /// changes, so the PID is the source of truth for "what mode are the controllers in right
    /// now". The same numbers travel over the pipe as <c>Function.LegionControllerMode</c>.
    /// </summary>
    public static class LegionControllerModes
    {
        public const int Unknown = 0;
        public const int XInput = 1;
        public const int DInput = 2;
        /// <summary>Controllers detached from the handheld: both halves show up as one D-Input device.</summary>
        public const int DualDInput = 3;
        /// <summary>Right controller's FPS-mode switch is on.</summary>
        public const int Fps = 4;

        private const int LenovoVendorId = 0x17EF;

        /// <summary>
        /// Mode for a Lenovo controller product ID, or <see cref="Unknown"/>.
        /// Original Legion Go: 6182 X-Input, 6183 D-Input, 6184 dual D-Input, 6185 FPS.
        /// Legion Go 2 (and newer Legion Go firmware): 61EB / 61EC / 61ED / 61EE in the same order.
        /// </summary>
        public static int FromProductId(int pid)
        {
            switch (pid)
            {
                case 0x6182: case 0x61EB: return XInput;
                case 0x6183: case 0x61EC: return DInput;
                case 0x6184: case 0x61ED: return DualDInput;
                case 0x6185: case 0x61EE: return Fps;
                default: return Unknown;
            }
        }

        /// <summary>
        /// Parses "17EF:61EB" (VID:PID, hex) into a mode, or <see cref="Unknown"/> for anything
        /// that isn't a Lenovo Legion controller.
        /// </summary>
        public static int FromVidPid(string vidPid)
        {
            if (string.IsNullOrEmpty(vidPid)) return Unknown;
            int colon = vidPid.IndexOf(':');
            if (colon <= 0 || colon >= vidPid.Length - 1) return Unknown;
            if (!int.TryParse(vidPid.Substring(0, colon), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out int vid) || vid != LenovoVendorId)
                return Unknown;
            if (!int.TryParse(vidPid.Substring(colon + 1), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out int pid))
                return Unknown;
            return FromProductId(pid);
        }
    }
}
