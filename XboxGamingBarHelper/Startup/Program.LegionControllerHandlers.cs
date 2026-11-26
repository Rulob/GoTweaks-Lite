using NLog;
using Shared.Constants;
using Shared.Data;
using Shared.IPC;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using XboxGamingBarHelper.AMD;
using XboxGamingBarHelper.Core;
using XboxGamingBarHelper.Devices.Libraries.Legion;
using XboxGamingBarHelper.LosslessScaling;
using XboxGamingBarHelper.OnScreenDisplay;
using XboxGamingBarHelper.Performance;
using XboxGamingBarHelper.Power;
using XboxGamingBarHelper.Profile;
using XboxGamingBarHelper.RTSS;
using XboxGamingBarHelper.Settings;
using XboxGamingBarHelper.Systems;
using XboxGamingBarHelper.Labs;
using Shared.Enums;

namespace XboxGamingBarHelper
{
    internal partial class Program
    {

        private static void LegionControllerSetting_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Skip during profile application to prevent cross-contamination
            if (isApplyingProfile)
            {
                Logger.Debug("Skipping LegionControllerSetting_PropertyChanged - already applying profile");
                return;
            }

            // Skip stale widget messages during cooldown after profile switch
            if (IsInProfileSwitchCooldown())
            {
                Logger.Debug("Skipping LegionControllerSetting_PropertyChanged - in profile switch cooldown");
                return;
            }

            // Controller settings are device-wide (1.9.7a): buttons, gamepad mapping, vibration,
            // lighting, gyro, deadzones, triggers and joystick-as-mouse all go to the Global profile
            // (their save flags are false). Only the TDP mode below follows the active profile.
            var profileName = profileManager.CurrentProfile.GameId.Name;
            bool saveButtonsToProfile = ProfileSaveFlagsState.ButtonMappings;

            // Button mappings
            if (sender == legionManager?.LegionButtonY1)
            {
                RouteProfileSave(saveButtonsToProfile, "LegionButtonY1",
                    cur => cur.LegionButtonY1 = legionManager.LegionButtonY1.Value);
            }
            else if (sender == legionManager?.LegionButtonY2)
            {
                RouteProfileSave(saveButtonsToProfile, "LegionButtonY2",
                    cur => cur.LegionButtonY2 = legionManager.LegionButtonY2.Value);
            }
            else if (sender == legionManager?.LegionButtonY3)
            {
                RouteProfileSave(saveButtonsToProfile, "LegionButtonY3",
                    cur => cur.LegionButtonY3 = legionManager.LegionButtonY3.Value);
            }
            else if (sender == legionManager?.LegionButtonM1)
            {
                RouteProfileSave(saveButtonsToProfile, "LegionButtonM1",
                    cur => cur.LegionButtonM1 = legionManager.LegionButtonM1.Value);
            }
            else if (sender == legionManager?.LegionButtonM2)
            {
                RouteProfileSave(saveButtonsToProfile, "LegionButtonM2",
                    cur => cur.LegionButtonM2 = legionManager.LegionButtonM2.Value);
            }
            else if (sender == legionManager?.LegionButtonM3)
            {
                RouteProfileSave(saveButtonsToProfile, "LegionButtonM3",
                    cur => cur.LegionButtonM3 = legionManager.LegionButtonM3.Value);
            }
            else if (sender == legionManager?.LegionButtonDesktop)
            {
                RouteProfileSave(saveButtonsToProfile, "LegionButtonDesktop",
                    cur => cur.LegionButtonDesktop = legionManager.LegionButtonDesktop.Value);
            }
            else if (sender == legionManager?.LegionButtonPage)
            {
                RouteProfileSave(saveButtonsToProfile, "LegionButtonPage",
                    cur => cur.LegionButtonPage = legionManager.LegionButtonPage.Value);
            }
            // Gyro settings — share a single GyroSettings flag, same pattern as Vibration/Lighting
            else if (sender == legionManager?.LegionGyroActivationButton)
            {
                RouteProfileSave(ProfileSaveFlagsState.GyroSettings, "LegionGyroButton",
                    cur => cur.LegionGyroButton = legionManager.LegionGyroActivationButton.Value);
            }
            else if (sender == legionManager?.LegionGyroTarget)
            {
                RouteProfileSave(ProfileSaveFlagsState.GyroSettings, "LegionGyroTarget",
                    cur => cur.LegionGyroTarget = legionManager.LegionGyroTarget.Value);
            }
            else if (sender == legionManager?.LegionGyroSensitivityX)
            {
                RouteProfileSave(ProfileSaveFlagsState.GyroSettings, "LegionGyroSensitivityX",
                    cur => cur.LegionGyroSensitivityX = legionManager.LegionGyroSensitivityX.Value);
            }
            else if (sender == legionManager?.LegionGyroSensitivityY)
            {
                RouteProfileSave(ProfileSaveFlagsState.GyroSettings, "LegionGyroSensitivityY",
                    cur => cur.LegionGyroSensitivityY = legionManager.LegionGyroSensitivityY.Value);
            }
            else if (sender == legionManager?.LegionGyroInvertX)
            {
                RouteProfileSave(ProfileSaveFlagsState.GyroSettings, "LegionGyroInvertX",
                    cur => cur.LegionGyroInvertX = legionManager.LegionGyroInvertX.Value);
            }
            else if (sender == legionManager?.LegionGyroInvertY)
            {
                RouteProfileSave(ProfileSaveFlagsState.GyroSettings, "LegionGyroInvertY",
                    cur => cur.LegionGyroInvertY = legionManager.LegionGyroInvertY.Value);
            }
            else if (sender == legionManager?.LegionGyroMappingType)
            {
                RouteProfileSave(ProfileSaveFlagsState.GyroSettings, "LegionGyroMappingType",
                    cur => cur.LegionGyroMappingType = legionManager.LegionGyroMappingType.Value);
            }
            else if (sender == legionManager?.LegionGyroActivationMode)
            {
                RouteProfileSave(ProfileSaveFlagsState.GyroSettings, "LegionGyroActivationMode",
                    cur => cur.LegionGyroActivationMode = legionManager.LegionGyroActivationMode.Value);
            }
            else if (sender == legionManager?.LegionGyroDeadzone)
            {
                RouteProfileSave(ProfileSaveFlagsState.GyroSettings, "LegionGyroDeadzone",
                    cur => cur.LegionGyroDeadzone = legionManager.LegionGyroDeadzone.Value);
            }
            // Stick deadzones
            else if (sender == legionManager?.LegionLeftStickDeadzone)
            {
                RouteProfileSave(false, "LegionLeftStickDeadzone", cur => cur.LegionLeftStickDeadzone = legionManager.LegionLeftStickDeadzone.Value);
            }
            else if (sender == legionManager?.LegionRightStickDeadzone)
            {
                RouteProfileSave(false, "LegionRightStickDeadzone", cur => cur.LegionRightStickDeadzone = legionManager.LegionRightStickDeadzone.Value);
            }
            // Trigger travel
            else if (sender == legionManager?.LegionLeftTriggerStart)
            {
                RouteProfileSave(false, "LegionLeftTriggerStart", cur => cur.LegionLeftTriggerStart = legionManager.LegionLeftTriggerStart.Value);
            }
            else if (sender == legionManager?.LegionLeftTriggerEnd)
            {
                RouteProfileSave(false, "LegionLeftTriggerEnd", cur => cur.LegionLeftTriggerEnd = legionManager.LegionLeftTriggerEnd.Value);
            }
            else if (sender == legionManager?.LegionRightTriggerStart)
            {
                RouteProfileSave(false, "LegionRightTriggerStart", cur => cur.LegionRightTriggerStart = legionManager.LegionRightTriggerStart.Value);
            }
            else if (sender == legionManager?.LegionRightTriggerEnd)
            {
                RouteProfileSave(false, "LegionRightTriggerEnd", cur => cur.LegionRightTriggerEnd = legionManager.LegionRightTriggerEnd.Value);
            }
            else if (sender == legionManager?.LegionHairTriggers)
            {
                RouteProfileSave(false, "LegionHairTriggers", cur => cur.LegionHairTriggers = legionManager.LegionHairTriggers.Value);
            }
            // Gamepad mapping (covered by the ButtonMappings flag alongside the Y/M/Desktop/Page buttons)
            else if (sender == legionManager?.LegionGamepadMapping)
            {
                RouteProfileSave(saveButtonsToProfile, "LegionGamepadMapping",
                    cur => cur.LegionGamepadMapping = legionManager.LegionGamepadMapping.Value);
            }
            // Nintendo layout — device-wide by default so a per-game toggle doesn't sit on top of
            // a stale True in GlobalProfile that then reappears after every reboot.
            else if (sender == legionManager?.LegionNintendoLayout)
            {
                RouteProfileSave(ProfileSaveFlagsState.NintendoLayout, "LegionNintendoLayout",
                    cur => cur.LegionNintendoLayout = legionManager.LegionNintendoLayout.Value);
            }
            else if (sender == legionManager?.LegionVibration)
            {
                RouteProfileSave(ProfileSaveFlagsState.Vibration, "LegionVibration",
                    cur => cur.LegionVibration = legionManager.LegionVibration.Value);
            }
            else if (sender == legionManager?.LegionVibrationMode)
            {
                RouteProfileSave(ProfileSaveFlagsState.Vibration, "LegionVibrationMode",
                    cur => cur.LegionVibrationMode = legionManager.LegionVibrationMode.Value);
            }
            else if (sender == legionManager?.LegionControllerProfileEnabled)
            {
                RouteProfileSave(false, "LegionControllerProfileEnabled", cur => cur.LegionControllerProfileEnabled = legionManager.LegionControllerProfileEnabled.Value);
            }
            // Performance mode (for per-game TDP mode switching)
            else if (sender == legionManager?.LegionPerformanceMode)
            {
                Logger.Info($"Saving LegionPerformanceMode to profile {profileName}");
                profileManager.CurrentProfile.LegionPerformanceMode = legionManager.LegionPerformanceMode.Value;
                // Also save directly to GlobalProfile to ensure it's always in sync
                // This fixes an issue where the restore reads from GlobalProfile but save goes to CurrentProfile
                if (profileManager.CurrentProfile.IsGlobalProfile)
                {
                    profileManager.GlobalProfile.LegionPerformanceMode = legionManager.LegionPerformanceMode.Value;
                    Logger.Debug($"Also saved LegionPerformanceMode to GlobalProfile directly: {legionManager.LegionPerformanceMode.Value}");
                }
            }
            // Lighting settings — share a single Lighting flag
            else if (sender == legionManager?.LegionLightMode)
            {
                RouteProfileSave(ProfileSaveFlagsState.Lighting, "LegionLightMode",
                    cur => cur.LegionLightMode = legionManager.LegionLightMode.Value);
            }
            else if (sender == legionManager?.LegionLightColor)
            {
                RouteProfileSave(ProfileSaveFlagsState.Lighting, "LegionLightColor",
                    cur => cur.LegionLightColor = legionManager.LegionLightColor.Value);
            }
            else if (sender == legionManager?.LegionLightBrightness)
            {
                RouteProfileSave(ProfileSaveFlagsState.Lighting, "LegionLightBrightness",
                    cur => cur.LegionLightBrightness = legionManager.LegionLightBrightness.Value);
            }
            else if (sender == legionManager?.LegionLightSpeed)
            {
                RouteProfileSave(ProfileSaveFlagsState.Lighting, "LegionLightSpeed",
                    cur => cur.LegionLightSpeed = legionManager.LegionLightSpeed.Value);
            }
            else if (sender == legionManager?.LegionPowerLight)
            {
                RouteProfileSave(ProfileSaveFlagsState.Lighting, "LegionPowerLight",
                    cur => cur.LegionPowerLight = legionManager.LegionPowerLight.Value);
            }
        }

    }
}
