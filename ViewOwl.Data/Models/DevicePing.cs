namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Records a single connectivity ping event from a device.
    /// Used to build uptime statistics exposed by <c>GET /api/devices/{id}/stats</c>.
    /// </summary>
    public sealed class DevicePing
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>FK — the device that sent this ping.</summary>
        public int DeviceId { get; set; }

        /// <summary>UTC timestamp of the ping.</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary><c>true</c> when the ping was acknowledged successfully.</summary>
        public bool Success { get; set; }

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>Device that generated this ping.</summary>
        public Device? Device { get; set; }
    }
}
