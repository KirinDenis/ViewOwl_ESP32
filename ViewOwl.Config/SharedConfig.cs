namespace ViewOwl.Config
{
    public class SharedConfig
    {
        public string ExchangeFolder { get; set; } = string.Empty;

        /// <summary>
        /// Base URL of the Grabber WebAPI server (e.g. http://localhost:5000).
        /// Used by Chrome engine to load HTML templates via HTTP instead of file://.
        /// </summary>
        public string WebApiBaseUrl { get; set; } = "http://localhost:5000";

        /// <summary>
        /// Secret token verified by <c>POST /api/internal/deploy-notify</c>.
        /// Set via the <c>DEPLOY_NOTIFY_SECRET</c> GitHub Actions secret — never commit a real value.
        /// </summary>
        public string DeployWebhookSecret { get; set; } = string.Empty;

        /// <summary>
        /// Minimum firmware MAJOR version that this server accepts.
        /// Devices reporting a different MAJOR are warned in the log — protocol is incompatible.
        /// </summary>
        public byte MinFirmwareMajor { get; set; } = 1;

        /// <summary>
        /// Minimum firmware MINOR version that this server accepts.
        /// Devices reporting a different MINOR are warned in the log — protocol may be incompatible.
        /// </summary>
        public byte MinFirmwareMinor { get; set; } = 2;

        /// <summary>
        /// Firmware version tested and recommended for use with this server release.
        /// Returned by <c>GET /api/version</c> so clients can check compatibility.
        /// </summary>
        public string RecommendedFirmware { get; set; } = "1.2.41";

        /// <summary>
        /// When <c>true</c>, creating a device auto-generates a per-device
        /// "STATUS — {name}" template, assigns it, and grabs it.
        /// Disabled by default: these auto-templates leaked into the public gallery
        /// and some crashed the headless grabber browser. Re-enable once the
        /// device-status template is hardened and kept out of the gallery.
        /// </summary>
        public bool AutoCreateDeviceStatusTemplate { get; set; }
    }
}
