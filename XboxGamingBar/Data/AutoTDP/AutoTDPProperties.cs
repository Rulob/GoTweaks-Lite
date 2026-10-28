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

    // SPPT / FPPT offsets AutoTDP adds on top of the wattage it picks. The widget owns them (they
    // are the Custom card's boost sliders) and pushes them; the helper's values are never shown.
    internal class AutoTDPSpptBoostProperty : WidgetProperty<int>
    {
        public AutoTDPSpptBoostProperty(int inValue) : base(inValue, null, Function.AutoTDPSpptBoost)
        {
        }
    }

    internal class AutoTDPFpptBoostProperty : WidgetProperty<int>
    {
        public AutoTDPFpptBoostProperty(int inValue) : base(inValue, null, Function.AutoTDPFpptBoost)
        {
        }
    }
}
