using Shared.Enums;
using System;
using System.Linq;
using System.Threading;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Read-only property for current CPU temperature from the helper.
    /// Used to display temperature indicator on the fan curve graph.
    /// </summary>
    internal class LegionCPUTempProperty : WidgetProperty<int>
    {
        private readonly Page _owner;
        private Action<int> _tempUpdateCallback;

        public LegionCPUTempProperty(Page owner)
            : base(0, null, Function.LegionCPUCurrentTemp)
        {
            _owner = owner;
        }

        /// <summary>
        /// Sets a callback to update the temperature indicator on the graph
        /// </summary>
        public void SetTempUpdateCallback(Action<int> callback)
        {
            _tempUpdateCallback = callback;
        }

        public override bool SetValue(object newValue, long updatedTime = 0)
        {
            Logger.Info($"LegionCPUTempProperty.SetValue called with value: {newValue}");
            return base.SetValue(newValue, updatedTime);
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            Logger.Info($"LegionCPUTempProperty.NotifyPropertyChanged: Value={Value}, callback={_tempUpdateCallback != null}");

            if (_tempUpdateCallback != null && _owner != null)
            {
                var temp = Value;
                await _owner.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    Logger.Info($"Invoking temp callback with {temp}°C");
                    _tempUpdateCallback(temp);
                });
            }
        }
    }

    /// <summary>
    /// Read-only property for fan control sensor temperature (0x01 sensor) from the helper.
    /// This is the temperature the EC uses for fan curve lookup (typically 10-17°C lower than CPU temp).
    /// </summary>
    internal class LegionFanSensorTempProperty : WidgetProperty<int>
    {
        private readonly Page _owner;
        private Action<int> _tempUpdateCallback;

        public LegionFanSensorTempProperty(Page owner)
            : base(0, null, Function.LegionFanSensorTemp)
        {
            _owner = owner;
        }

        /// <summary>
        /// Sets a callback to update the fan sensor temperature indicator on the graph
        /// </summary>
        public void SetTempUpdateCallback(Action<int> callback)
        {
            _tempUpdateCallback = callback;
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (_tempUpdateCallback != null && _owner != null)
            {
                var temp = Value;
                await _owner.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    _tempUpdateCallback(temp);
                });
            }
        }
    }

    /// <summary>
    /// Read-only property for current CPU fan RPM from the helper.
    /// Used to display fan speed in the fan curve card header.
    /// </summary>
    internal class LegionCPUFanRPMProperty : WidgetProperty<int>
    {
        private readonly Page _owner;
        private Action<int> _rpmUpdateCallback;

        public LegionCPUFanRPMProperty(Page owner)
            : base(0, null, Function.LegionCPUFanRPM)
        {
            _owner = owner;
        }

        /// <summary>
        /// Sets a callback to update the fan RPM display
        /// </summary>
        public void SetRPMUpdateCallback(Action<int> callback)
        {
            _rpmUpdateCallback = callback;
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (_rpmUpdateCallback != null && _owner != null)
            {
                var rpm = Value;
                await _owner.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    _rpmUpdateCallback(rpm);
                });
            }
        }
    }

    /// <summary>
    /// Property to tell the helper when the fan curve graph is visible.
    /// The helper only pushes CPU temp and fan RPM updates when this is true.
    /// </summary>
    internal class LegionFanCurveVisibleProperty : WidgetProperty<bool>
    {
        public LegionFanCurveVisibleProperty()
            : base(false, null, Function.LegionFanCurveVisible)
        {
        }

        public void SetVisible(bool visible)
        {
            SetValue(visible);
        }
    }
}
