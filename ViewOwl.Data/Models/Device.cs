namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Represents a physical ESP32 display device registered to a user.
    /// </summary>
    public sealed class Device
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>
        /// Permanent unique token used by the device to identify itself over UDP.
        /// Generated once on registration; never changes.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>Human-readable display name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>FK — owner of this device.</summary>
        public int UserId { get; set; }

        /// <summary>
        /// FK — the template currently active on this device.
        /// <c>null</c> when no template has been assigned yet.
        /// </summary>
        public int? ActiveTemplateId { get; set; }

        /// <summary>UTC timestamp when the device was registered.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // ── Monitoring / hardware metadata ────────────────────────────────────

        /// <summary>Type of display controller (ST7789, ILI9341, ST7796, etc.).</summary>
        public string? DisplayType { get; set; }

        /// <summary>Display width in pixels.</summary>
        public int? DisplayWidth { get; set; }

        /// <summary>Display height in pixels.</summary>
        public int? DisplayHeight { get; set; }

        /// <summary>Firmware version reported by the ESP32 (e.g., "1.0.2").</summary>
        public string? FirmwareVersion { get; set; }

        /// <summary>UTC timestamp of the last PING/HELLO packet received from this device.</summary>
        public DateTime? LastSeenAt { get; set; }

        /// <summary>Current connection status, updated on each PING/HELLO and on heartbeat timeout.</summary>
        public DeviceStatus Status { get; set; } = DeviceStatus.Offline;

        /// <summary>Last known IP address of the device (IPv4 or IPv6).</summary>
        public string? IpAddress { get; set; }

        /// <summary>
        /// WiFi MAC address as reported in the extended HELLO packet (e.g., "AA:BB:CC:DD:EE:FF").
        /// Populated on first connection from firmware v1.2.17+. Used as a hardware fingerprint.
        /// </summary>
        public string? MacAddress { get; set; }

        /// <summary>Last known WiFi RSSI in dBm (from PING packet).</summary>
        public int? WifiRssi { get; set; }

        /// <summary>Last known free heap in bytes (from PING packet).</summary>
        public int? FreeHeap { get; set; }

        /// <summary>Last known device uptime in seconds (from PING packet).</summary>
        public int? UptimeSeconds { get; set; }

        /// <summary>
        /// CRC32 of the last frame the device reported rendering (from HELLO header.Seq).
        /// Formatted as "0xAABBCCDD" for display. <c>null</c> when no frame yet.
        /// </summary>
        public uint? LastFrameCrc { get; set; }

        /// <summary>
        /// Total number of frames successfully delivered to this device over UDP (lifetime counter).
        /// Incremented on each successful transfer session.
        /// </summary>
        public int FramesSentCount { get; set; }

        /// <summary>
        /// Estimated UTC boot time, derived from the first PING after each reboot:
        /// <c>LastBootAt = pingReceivedAt − uptimeSeconds</c>.
        /// Used to compute accurate live uptime without depending on PING frequency.
        /// </summary>
        public DateTime? LastBootAt { get; set; }

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>Owner.</summary>
        public User? User { get; set; }

        /// <summary>Currently active template (may be null).</summary>
        public Template? ActiveTemplate { get; set; }

        /// <summary>All templates associated with this device (many-to-many).</summary>
        public ICollection<Template> Templates { get; private set; } = [];

        /// <summary>Ping history for this device.</summary>
        public ICollection<DevicePing> Pings { get; private set; } = [];
    }
}
