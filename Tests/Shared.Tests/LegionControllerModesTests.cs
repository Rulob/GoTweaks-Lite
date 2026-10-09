using Shared.Data;
using Xunit;

namespace Shared.Tests
{
    /// <summary>
    /// X-Input / D-Input detection: the helper derives the controllers' current mode from the
    /// USB product ID they re-enumerate as, so this mapping is what makes the switch honest.
    /// </summary>
    public class LegionControllerModesTests
    {
        [Theory]
        [InlineData(0x6182, LegionControllerModes.XInput)]
        [InlineData(0x61EB, LegionControllerModes.XInput)]
        [InlineData(0x6183, LegionControllerModes.DInput)]
        [InlineData(0x61EC, LegionControllerModes.DInput)]
        [InlineData(0x6184, LegionControllerModes.DualDInput)]
        [InlineData(0x61ED, LegionControllerModes.DualDInput)]
        [InlineData(0x6185, LegionControllerModes.Fps)]
        [InlineData(0x61EE, LegionControllerModes.Fps)]
        public void KnownProductIds_MapToTheirMode(int pid, int expected)
        {
            Assert.Equal(expected, LegionControllerModes.FromProductId(pid));
        }

        [Theory]
        [InlineData(0x0000)]
        [InlineData(0x6180)]
        [InlineData(0x61EF)]
        [InlineData(0xE310)] // Legion Go S uses a different chip and protocol
        public void UnknownProductIds_AreUnknown(int pid)
        {
            Assert.Equal(LegionControllerModes.Unknown, LegionControllerModes.FromProductId(pid));
        }

        [Theory]
        [InlineData("17EF:61EB", LegionControllerModes.XInput)]
        [InlineData("17ef:61ec", LegionControllerModes.DInput)]   // hex is case-insensitive
        [InlineData("17EF:6184", LegionControllerModes.DualDInput)]
        [InlineData("17EF:61EE", LegionControllerModes.Fps)]
        public void VidPid_StringsParse(string vidPid, int expected)
        {
            Assert.Equal(expected, LegionControllerModes.FromVidPid(vidPid));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("--")]
        [InlineData("17EF")]
        [InlineData("17EF:")]
        [InlineData(":61EB")]
        [InlineData("17EF:ZZZZ")]
        [InlineData("1A86:E310")] // Go S: right chip family, wrong vendor for this protocol
        [InlineData("045E:028E")] // an unrelated Xbox 360 pad
        public void NotALegionController_IsUnknown(string? vidPid)
        {
            Assert.Equal(LegionControllerModes.Unknown, LegionControllerModes.FromVidPid(vidPid!));
        }
    }
}
