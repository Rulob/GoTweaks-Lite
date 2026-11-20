using Shared.Enums;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Shows the GoTweaks widget first in Game Bar (the device-maker slot left of Home, normally
    /// Legion Space). The helper writes the registry value and restarts Game Bar.
    /// </summary>
    internal class GameBarFirstSlotProperty : WidgetToggleProperty
    {
        public GameBarFirstSlotProperty(ToggleSwitch inUI, Page inOwner) : base(false, Function.GameBarFirstSlot, inUI, inOwner)
        {
        }
    }
}
