namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Queued command to be sent to a device (reboot, config update, etc.)
    /// Commands are produced by the API and consumed by the UDP server session.
    /// </summary>
    public sealed class DeviceCommand
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>FK — target device.</summary>
        public int DeviceId { get; set; }

        /// <summary>Command type: "reboot", "config", "ban", etc.</summary>
        public string CommandType { get; set; } = string.Empty;

        /// <summary>Optional JSON payload for the command.</summary>
        public string? Payload { get; set; }

        /// <summary>Execution status: "pending", "sent", "ack", "failed".</summary>
        public string Status { get; set; } = "pending";

        /// <summary>UTC timestamp when the command was queued.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>UTC timestamp when the command was delivered or failed. Null while pending.</summary>
        public DateTime? ExecutedAt { get; set; }

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>Target device.</summary>
        public Device Device { get; set; } = null!;
    }
}
