using System;
using Windows.UI.Core;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // Lossless Scaling is no longer configured from the widget (the Scale tab was removed):
        // only the Quick Settings tile / its hotkey turn scaling on and off. What's left here keeps
        // that tile's state in step with Lossless Scaling's installed / running status.

        private async void UpdateLosslessScalingStatus()
        {
            try
            {
                bool isInstalled = losslessScalingInstalled?.Value ?? false;
                bool isRunning = losslessScalingRunning?.Value ?? false;

                Logger.Info($"UpdateLosslessScalingStatus called. Installed: {isInstalled}, Running: {isRunning}");

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        UpdateQuickSettingsTileStates();
                    }
                    catch (Exception innerEx)
                    {
                        Logger.Error($"Error updating Lossless Scaling tile state: {innerEx.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in UpdateLosslessScalingStatus: {ex.Message}");
            }
        }

        private void LosslessScalingStatus_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Update status when installed/running state changes
            UpdateLosslessScalingStatus();
        }

        /// <summary>
        /// Shows or hides the Lossless Scaling Quick Settings tile based on whether Lossless
        /// Scaling is installed.
        /// </summary>
        private void SetLosslessScalingTileVisibility(bool installed)
        {
            if (quickSettingsInitialized)
            {
                RebuildQuickSettingsTiles();
                BuildSortableGrid();
                Logger.Info($"Rebuilt Quick Settings tiles for Lossless Scaling visibility: {installed}");
            }
        }
    }
}
