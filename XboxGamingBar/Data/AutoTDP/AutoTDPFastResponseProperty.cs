using Shared.Enums;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// AutoTDP "Fast response": jump straight to an estimated wattage when FPS is far from the
    /// target. Global setting, saved by the helper.
    /// </summary>
    internal class AutoTDPFastResponseProperty : WidgetToggleProperty
    {
        public AutoTDPFastResponseProperty(ToggleSwitch inUI, Page inOwner) : base(true, Function.AutoTDPFastResponse, inUI, inOwner)
        {
        }
    }
}
