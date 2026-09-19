using Shared.Enums;

namespace XboxGamingBar.Data
{
    internal class AutoTDPEnabledProperty : WidgetProperty<bool>
    {
        public AutoTDPEnabledProperty(bool inValue) : base(inValue, null, Function.AutoTDPEnabled)
        {
        }
    }

    internal class AutoTDPTargetFPSProperty : WidgetProperty<int>
    {
        public AutoTDPTargetFPSProperty(int inValue) : base(inValue, null, Function.AutoTDPTargetFPS)
        {
        }
    }

    internal class AutoTDPCurrentFPSProperty : WidgetProperty<int>
    {
        public AutoTDPCurrentFPSProperty(int inValue) : base(inValue, null, Function.AutoTDPCurrentFPS)
        {
        }
    }

    internal class AutoTDPMinTDPProperty : WidgetProperty<int>
    {
        public AutoTDPMinTDPProperty(int inValue) : base(inValue, null, Function.AutoTDPMinTDP)
        {
        }
    }

    internal class AutoTDPMaxTDPProperty : WidgetProperty<int>
    {
        public AutoTDPMaxTDPProperty(int inValue) : base(inValue, null, Function.AutoTDPMaxTDP)
        {
        }
    }

    internal class AutoTDPPauseWhenUnfocusedProperty : WidgetProperty<bool>
    {
        public AutoTDPPauseWhenUnfocusedProperty(bool inValue) : base(inValue, null, Function.AutoTDPPauseWhenUnfocused)
        {
        }
    }
}
