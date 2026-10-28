using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.LosslessScaling.Properties
{
    internal class LosslessScalingInstalledProperty : HelperProperty<bool, LosslessScalingManager>
    {
        public LosslessScalingInstalledProperty(bool inValue, LosslessScalingManager inManager)
            : base(inValue, null, Function.LosslessScalingInstalled, inManager)
        {
        }
    }

    internal class LosslessScalingRunningProperty : HelperProperty<bool, LosslessScalingManager>
    {
        public LosslessScalingRunningProperty(bool inValue, LosslessScalingManager inManager)
            : base(inValue, null, Function.LosslessScalingRunning, inManager)
        {
        }
    }

    internal class LosslessScalingEnabledProperty : HelperProperty<bool, LosslessScalingManager>
    {
        public LosslessScalingEnabledProperty(bool inValue, LosslessScalingManager inManager)
            : base(inValue, null, Function.LosslessScalingEnabled, inManager)
        {
        }
    }
}
