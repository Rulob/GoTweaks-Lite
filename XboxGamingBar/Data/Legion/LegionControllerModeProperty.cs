using Shared.Enums;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Legion controller input mode, mirroring Legion Space's X-Input / D-Input switch.
    /// The helper keeps this equal to the mode the controllers are actually in (derived from
    /// their USB product ID): 0 = unknown, 1 = X-Input, 2 = D-Input, 3 = Dual D-Input
    /// (controllers detached), 4 = FPS mode. Setting 1 or 2 from the widget asks the helper
    /// to switch; the controllers re-enumerate and the helper then pushes the resulting mode.
    /// </summary>
    internal class LegionControllerModeProperty : WidgetProperty<int>
    {
        public LegionControllerModeProperty() : base(0, null, Function.LegionControllerMode)
        {
        }
    }
}
