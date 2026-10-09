using XboxGamingBarHelper.Core;

namespace XboxGamingBarHelper.Settings
{
    internal class SettingsManager : Manager
    {
        private static SettingsManager instance;
        public static SettingsManager CreateInstance()
        {
            if (instance == null)
            {
                instance = new SettingsManager();
            }
            return instance;
        }

        public static SettingsManager GetInstance()
        {
            return instance;
        }

        private readonly AutoStartRTSSProperty autoStartRTSS;
        public AutoStartRTSSProperty AutoStartRTSS
        {
            get { return autoStartRTSS; }
        }


        private readonly IsForegroundProperty isForeground;
        public IsForegroundProperty IsForeground
        {
            get { return isForeground; }
        }

        private readonly UseManufacturerWMIProperty useManufacturerWMI;
        /// <summary>
        /// DEPRECATED: Lenovo WMI is now the only TDP control method.
        /// </summary>
        public UseManufacturerWMIProperty UseManufacturerWMI
        {
            get { return useManufacturerWMI; }
        }

        private readonly GoTweaksLightingConfigProperty goTweaksLightingConfig;
        public GoTweaksLightingConfigProperty GoTweaksLightingConfig
        {
            get { return goTweaksLightingConfig; }
        }

        private readonly GoTweaksHapticsConfigProperty goTweaksHapticsConfig;
        public GoTweaksHapticsConfigProperty GoTweaksHapticsConfig
        {
            get { return goTweaksHapticsConfig; }
        }

        private readonly LegionControllerSleepMinutesProperty legionControllerSleepMinutes;
        public LegionControllerSleepMinutesProperty LegionControllerSleepMinutes
        {
            get { return legionControllerSleepMinutes; }
        }

        // Profile Detection Settings
        private readonly ProfileMatchByExeProperty profileMatchByExe;
        public ProfileMatchByExeProperty ProfileMatchByExe
        {
            get { return profileMatchByExe; }
        }

        private readonly ProfileCustomGamePathProperty profileCustomGamePath;
        public ProfileCustomGamePathProperty ProfileCustomGamePath
        {
            get { return profileCustomGamePath; }
        }

        private readonly ProfileGamesOnlyProperty profileGamesOnly;
        public ProfileGamesOnlyProperty ProfileGamesOnly
        {
            get { return profileGamesOnly; }
        }

        private readonly ProfileBlacklistPathsProperty profileBlacklistPaths;
        public ProfileBlacklistPathsProperty ProfileBlacklistPaths
        {
            get { return profileBlacklistPaths; }
        }

        protected SettingsManager() : base()
        {
            autoStartRTSS = new AutoStartRTSSProperty(this);
            isForeground = new IsForegroundProperty(this);
            useManufacturerWMI = new UseManufacturerWMIProperty(this);
            goTweaksLightingConfig = new GoTweaksLightingConfigProperty(this);
            goTweaksHapticsConfig = new GoTweaksHapticsConfigProperty(this);
            legionControllerSleepMinutes = new LegionControllerSleepMinutesProperty(this);
            // Profile Detection Settings
            profileMatchByExe = new ProfileMatchByExeProperty(this);
            profileCustomGamePath = new ProfileCustomGamePathProperty(this);
            profileGamesOnly = new ProfileGamesOnlyProperty(this);
            profileBlacklistPaths = new ProfileBlacklistPathsProperty(this);
        }
    }
}
