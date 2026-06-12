namespace ViewOwl.Config
{
    /// <summary>
    /// Configuration for device monitoring background service.
    /// </summary>
    public sealed class DeviceMonitorConfig
    {
        /// <summary>
        /// How often to check for offline devices (in seconds).
        /// Default: 30 seconds.
        /// </summary>
        public int CheckIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Device is considered offline if no heartbeat for this many seconds.
        /// Default: 120 seconds (2 minutes).
        /// </summary>
        public int OfflineThresholdSeconds { get; set; } = 120;
    }
}
