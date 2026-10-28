namespace XboxGamingBar
{
    public sealed partial class GamingWidget
    {
        // Fail-open default (true = internal panel assumed active) so a query failure or a
        // pre-BatchGet render never blocks functionality that worked before this gate existed.
        private bool _internalPanelActive = true;

        /// <summary>
        /// True when the built-in panel is the active display right now (vs. docked with only
        /// an external monitor active). Read by the Rotation Quick tile, which only makes sense
        /// against the internal panel.
        /// </summary>
        internal bool IsInternalPanelActive => _internalPanelActive;

        /// <summary>
        /// Applies the internal-panel-active gate to the Quick tiles that only make sense against
        /// the built-in panel (Rotation). Called from InternalPanelActiveProperty whenever the
        /// helper pushes a new value (dock/undock) and once after the initial BatchGet completes.
        /// </summary>
        internal void ApplyInternalPanelActiveGate(bool internalActive)
        {
            _internalPanelActive = internalActive;
            UpdateQuickSettingsTileStates();
        }
    }
}
