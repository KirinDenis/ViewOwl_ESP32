namespace ViewOwl.Data.Models
{
    /// <summary>
    /// String constants for <see cref="PipelineEvent.EventType"/>.
    /// Using constants prevents typos at call sites and makes grep/search trivial.
    /// </summary>
    public static class PipelineEventTypes
    {
        // ── Grabber ───────────────────────────────────────────────────────────

        /// <summary>Template grab has been added to the queue.</summary>
        public const string GrabQueued    = "grab_queued";

        /// <summary>Chromium has started loading the template page.</summary>
        public const string GrabStarted   = "grab_started";

        /// <summary>Screenshot succeeded; .bin written to ExchangeFolder.</summary>
        public const string GrabCompleted = "grab_completed";

        /// <summary>Screenshot or conversion failed.</summary>
        public const string GrabFailed    = "grab_failed";

        // ── UDP server ────────────────────────────────────────────────────────

        /// <summary>Client sent HELLO; server replied with AUTH (session opened).</summary>
        public const string UdpSessionOpened = "udp_session_opened";

        /// <summary>DATA chunk sent to the device.</summary>
        public const string UdpFrameSent     = "udp_frame_sent";

        /// <summary>Device CRC matched current .bin; no DATA transfer needed.</summary>
        public const string UdpNotModified   = "udp_not_modified";

        /// <summary>DONE packet sent; device acknowledged end of transfer.</summary>
        public const string UdpDone          = "udp_done";

        /// <summary>UDP session failed (timeout, bad ACK, or device rejected).</summary>
        public const string UdpFailed        = "udp_failed";

        /// <summary>Device sent HELLO but no .bin exists for its assigned template yet.</summary>
        public const string UdpNoFrame       = "udp_no_frame";

        /// <summary>Batch animation frame set sent successfully.</summary>
        public const string BatchSent        = "batch_sent";

        /// <summary>Batch transfer committed (device acknowledged all frames).</summary>
        public const string BatchCommitted   = "batch_committed";

        // ── Device / internal controller ──────────────────────────────────────

        /// <summary>Device CRC value changed (new frame rendered on display).</summary>
        public const string DeviceCrcChanged = "device_crc_changed";

        /// <summary>Device came online (HELLO received after being offline).</summary>
        public const string DeviceOnline     = "device_online";

        /// <summary>Device went offline (HELLO timeout exceeded threshold).</summary>
        public const string DeviceOffline    = "device_offline";

        // ── Health worker ─────────────────────────────────────────────────────

        /// <summary>Periodic health assessment result for any pipeline entity.</summary>
        public const string HealthCheck      = "health_check";
    }
}
