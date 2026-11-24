using NLog;
using Shared.Enums;
using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.AutoTDP
{
    internal class AutoTDPEnabledProperty : HelperProperty<bool, AutoTDPManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AutoTDPEnabledProperty(bool inValue, AutoTDPManager inManager) : base(inValue, null, Function.AutoTDPEnabled, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"AutoTDP enabled: {Value}");
        }
    }

    internal class AutoTDPTargetFPSProperty : HelperProperty<int, AutoTDPManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AutoTDPTargetFPSProperty(int inValue, AutoTDPManager inManager) : base(inValue, null, Function.AutoTDPTargetFPS, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"AutoTDP target FPS: {Value}");
        }
    }

    internal class AutoTDPCurrentFPSProperty : HelperProperty<int, AutoTDPManager>
    {
        public AutoTDPCurrentFPSProperty(int inValue, AutoTDPManager inManager) : base(inValue, null, Function.AutoTDPCurrentFPS, inManager)
        {
        }
    }

    internal class AutoTDPMinTDPProperty : HelperProperty<int, AutoTDPManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AutoTDPMinTDPProperty(int inValue, AutoTDPManager inManager) : base(inValue, null, Function.AutoTDPMinTDP, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"AutoTDP min TDP: {Value}W");
            Manager.UpdateTDPLimits(Value, Manager.MaxTDP.Value);
        }
    }

    internal class AutoTDPMaxTDPProperty : HelperProperty<int, AutoTDPManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AutoTDPMaxTDPProperty(int inValue, AutoTDPManager inManager) : base(inValue, null, Function.AutoTDPMaxTDP, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"AutoTDP max TDP: {Value}W");
            Manager.UpdateTDPLimits(Manager.MinTDP.Value, Value);
        }
    }

    internal class AutoTDPPauseWhenUnfocusedProperty : HelperProperty<bool, AutoTDPManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AutoTDPPauseWhenUnfocusedProperty(bool inValue, AutoTDPManager inManager) : base(inValue, null, Function.AutoTDPPauseWhenUnfocused, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"AutoTDP pause when unfocused: {Value}");
        }
    }

    internal class AutoTDPSpptBoostProperty : HelperProperty<int, AutoTDPManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AutoTDPSpptBoostProperty(int inValue, AutoTDPManager inManager) : base(inValue, null, Function.AutoTDPSpptBoost, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"AutoTDP SPPT boost: +{Value}W");
            Manager.OnBoostsChanged();
        }
    }

    internal class AutoTDPFpptBoostProperty : HelperProperty<int, AutoTDPManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AutoTDPFpptBoostProperty(int inValue, AutoTDPManager inManager) : base(inValue, null, Function.AutoTDPFpptBoost, inManager)
        {
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Logger.Info($"AutoTDP FPPT boost: +{Value}W");
            Manager.OnBoostsChanged();
        }
    }

    /// <summary>
    /// Fast response: when FPS is far from the target, AutoTDP estimates the wattage it needs
    /// and jumps there in one step instead of climbing 1-3 W at a time. Global setting, saved by
    /// the helper (default on).
    /// </summary>
    internal class AutoTDPFastResponseProperty : HelperProperty<bool, AutoTDPManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const string SettingsKey = "AutoTDPFastResponse";

        public AutoTDPFastResponseProperty(AutoTDPManager inManager) : base(LoadFromSettings(), null, Function.AutoTDPFastResponse, inManager)
        {
            Logger.Info($"AutoTDP fast response loaded: {Value}");
        }

        private static bool LoadFromSettings()
        {
            return Settings.LocalSettingsHelper.TryGetValue<bool>(SettingsKey, out var value) ? value : true;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Settings.LocalSettingsHelper.SetValue(SettingsKey, Value);
            Logger.Info($"AutoTDP fast response: {Value}");
        }
    }

    /// <summary>
    /// The Legion firmware mode (1-3) to return to while AutoTDP is on but no game runs, on the
    /// Global profile; 0 = stay in Custom. Sent by the widget when AutoTDP is turned on (the mode
    /// it switched away from). Persisted so it survives helper restarts.
    /// </summary>
    internal class AutoTDPIdleModeProperty : HelperProperty<int, AutoTDPManager>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const string SettingsKey = "AutoTDPIdleMode";

        public AutoTDPIdleModeProperty(AutoTDPManager inManager) : base(LoadFromSettings(), null, Function.AutoTDPIdleMode, inManager)
        {
            Logger.Info($"AutoTDP idle mode loaded: {Value}");
        }

        private static int LoadFromSettings()
        {
            return Settings.LocalSettingsHelper.TryGetValue<int>(SettingsKey, out var value) && value >= 1 && value <= 3 ? value : 0;
        }

        protected override void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);
            Settings.LocalSettingsHelper.SetValue(SettingsKey, Value);
            Logger.Info($"AutoTDP idle mode: {Value}");
        }
    }

}
