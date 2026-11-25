using System;
using System.Collections.Generic;
using System.ComponentModel;
using Windows.Storage;
using Windows.UI.Core;

namespace XboxGamingBar
{
    // Since 1.9.7a the controller settings (back-button remaps, gyro, deadzones, triggers,
    // joystick mouse, vibration, lighting) are device-wide and owned by the helper, which saves
    // them in its Global profile and applies them at startup. The widget keeps no copy: the bound
    // controls follow the helper's values on sync, and the back-button remap UI (not bound, it has
    // its own type / keys / combo controls) is rebuilt here from the helper's mapping JSON.
    public sealed partial class GamingWidget
    {
        private bool buttonMappingUiSyncQueued;

        /// <summary>Subscribes the eight button-mapping properties to the helper sync below.</summary>
        private void SubscribeButtonMappingHelperSync()
        {
            if (legionButtonY1 != null) legionButtonY1.PropertyChanged += LegionButtonMapping_PropertyChanged;
            if (legionButtonY2 != null) legionButtonY2.PropertyChanged += LegionButtonMapping_PropertyChanged;
            if (legionButtonY3 != null) legionButtonY3.PropertyChanged += LegionButtonMapping_PropertyChanged;
            if (legionButtonM1 != null) legionButtonM1.PropertyChanged += LegionButtonMapping_PropertyChanged;
            if (legionButtonM2 != null) legionButtonM2.PropertyChanged += LegionButtonMapping_PropertyChanged;
            if (legionButtonM3 != null) legionButtonM3.PropertyChanged += LegionButtonMapping_PropertyChanged;
            if (legionButtonDesktop != null) legionButtonDesktop.PropertyChanged += LegionButtonMapping_PropertyChanged;
            if (legionButtonPage != null) legionButtonPage.PropertyChanged += LegionButtonMapping_PropertyChanged;
        }

        private void LegionButtonMapping_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Only the helper's pushes; the widget's own sends already match the UI.
            if (!isApplyingHelperUpdate || buttonMappingUiSyncQueued) return;
            buttonMappingUiSyncQueued = true;
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                buttonMappingUiSyncQueued = false;
                try { ApplyHelperButtonMappingsToUI(); }
                catch (Exception ex) { Logger.Warn($"Button mapping UI sync failed: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Shows the helper's eight button mappings in the remap UI without sending them back,
        /// then refreshes the cached controller state the widget's sends are built from.
        /// </summary>
        private void ApplyHelperButtonMappingsToUI()
        {
            var mappings = new List<(string Name, string Json)>
            {
                ("Y1", legionButtonY1?.Value), ("Y2", legionButtonY2?.Value), ("Y3", legionButtonY3?.Value),
                ("M1", legionButtonM1?.Value), ("M2", legionButtonM2?.Value), ("M3", legionButtonM3?.Value),
                ("Desktop", legionButtonDesktop?.Value), ("Page", legionButtonPage?.Value),
            };

            bool wasLoading = isLoadingControllerProfile;
            isLoadingControllerProfile = true; // the remap controls' handlers must not send
            try
            {
                foreach (var (name, json) in mappings)
                {
                    if (string.IsNullOrEmpty(json)) continue; // the helper has nothing for it: leave the UI
                    var mapping = ButtonMapping.FromJson(json);
                    SetStoredKeyboardKeys(name, mapping.KeyboardKeys ?? new List<int>());
                    SetStoredGamepadComboActions(name, mapping.GamepadActions ?? new List<int>());
                    SetStoredButtonTurbo(name, mapping.Turbo);
                    SetStoredButtonGamepadMode(name, mapping.GamepadMode == 1 || (mapping.GamepadActions?.Count ?? 0) > 1 ? 1 : 0);
                    ApplyButtonMappingToUI(name, mapping);
                }
            }
            finally
            {
                isLoadingControllerProfile = wasLoading;
            }

            globalControllerProfile = GetCurrentControllerProfileFromUI();
            RefreshLegionEnhancedRemapUi();
            Logger.Info("Back-button remap UI follows the helper's mappings");
        }

        // ---- One-time hand-over of the widget's old controller copy ----------------------------

        private const string ControllerSettingsMigratedKey = "ControllerSettingsMigratedToHelper_197a";

        /// <summary>
        /// Before 1.9.7a the widget kept its own copy of the controller settings
        /// (ControllerProfile_Global) and pushed it to the helper on every connect, so that copy
        /// was what the device used. Push it once more so nothing changes on update; from then on
        /// the helper's values are the ones shown and kept.
        /// </summary>
        private void MigrateControllerSettingsToHelperOnce()
        {
            try
            {
                if (!App.IsConnected) return;
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.ContainsKey(ControllerSettingsMigratedKey)) return;

                if (settings.Containers.ContainsKey("ControllerProfile_Global"))
                {
                    var stored = new ControllerProfile();
                    LoadControllerProfileFromStorage("Global", stored);
                    SendButtonMappingsToHelper(stored, force: true);
                    SendControllerSettingsToHelper(stored);
                    SendLightingToHelper(stored);
                    // The sync before this already showed the helper's older copy: show these instead.
                    globalControllerProfile = stored;
                    ApplyControllerProfile(stored);
                    Logger.Info("Sent the widget's controller settings to the helper (one-time 1.9.7a migration)");
                }
                settings.Values[ControllerSettingsMigratedKey] = true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Controller settings migration failed: {ex.Message}");
            }
        }
    }
}
