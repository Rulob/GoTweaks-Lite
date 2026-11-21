using System;
using Windows.Foundation.Collections;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // Suppress outbound sends while we populate controls from the helper's current values on
        // load (otherwise every programmatic SelectedIndex/Value change echoes back).
        private bool _goTweaksSyncing;

        private static void SendIntToHelper(Shared.Enums.Function fn, int value)
        {
            if (!App.IsConnected) return;
            var msg = new ValueSet
            {
                { "Command", (int)Shared.Enums.Command.Set },
                { "Function", (int)fn },
                { "Content", value }
            };
            App.PipeClient?.SendValueSet(msg);
        }

        // --- Controller auto-sleep timeout --------------------------------------------------

        private void LegionControllerSleepComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_goTweaksSyncing) return;
            try
            {
                string tag = (LegionControllerSleepComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "15";
                if (int.TryParse(tag, out int minutes))
                {
                    SendIntToHelper(Shared.Enums.Function.LegionControllerSleepMinutes, minutes);
                }
            }
            catch (Exception ex) { Logger.Warn($"LegionControllerSleepComboBox_SelectionChanged failed: {ex.Message}"); }
        }

        /// <summary>
        /// Request the persisted controller sleep timeout from the helper and select it.
        /// Guards sends with _goTweaksSyncing during the programmatic fill.
        /// </summary>
        internal async void SyncGoTweaksFeaturesFromHelper()
        {
            try
            {
                if (!App.IsConnected) return;
                string sleep = await RequestStringAsync(Shared.Enums.Function.LegionControllerSleepMinutes);

                _goTweaksSyncing = true;
                try
                {
                    if (!string.IsNullOrEmpty(sleep)) SelectComboByTag(LegionControllerSleepComboBox, sleep.Trim());
                }
                finally { _goTweaksSyncing = false; }
            }
            catch (Exception ex) { Logger.Warn($"SyncGoTweaksFeaturesFromHelper failed: {ex.Message}"); }
        }

        private static async System.Threading.Tasks.Task<string> RequestStringAsync(Shared.Enums.Function fn)
        {
            try
            {
                var request = new ValueSet
                {
                    { "Command", (int)Shared.Enums.Command.Get },
                    { "Function", (int)fn }
                };
                var resp = await App.PipeClient.SendRequestAsync(request);
                if (resp != null && resp.TryGetValue("Content", out object content) && content != null)
                {
                    // Content may be a string or a boxed int (sleep minutes).
                    return content as string ?? content.ToString();
                }
            }
            catch (Exception ex) { Logger.Warn($"RequestStringAsync {fn} failed: {ex.Message}"); }
            return null;
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            if (combo == null) return;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && (item.Tag as string) == tag)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

    }
}
