using Shared.Constants;
using Shared.Enums;
using System.Collections.Generic;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// The refresh rates the display supports, pushed by the helper. Headless since the Display
    /// tab was removed: the FPS Limit uses the highest one as its slider maximum.
    /// </summary>
    internal class RefreshRatesProperty : WidgetControlProperty<List<int>, ComboBox>
    {
        public RefreshRatesProperty(ComboBox inUI, Page inOwner) : base(new List<int>() { SystemConstants.DEFAULT_REFRESH_RATE }, Function.RefreshRates, inUI, inOwner)
        {
        }

        protected override void SetControlEnabled(bool isEnabled)
        {
        }
    }
}
