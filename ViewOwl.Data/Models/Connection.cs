namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Represents a link between a Template (HTML grabber) and a Device.
    /// When connected, the device will receive rendered images from this template.
    /// </summary>
    public sealed class Connection
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>FK — owner of this connection.</summary>
        public int UserId { get; set; }

        /// <summary>FK — source template (grabber).</summary>
        public int TemplateId { get; set; }

        /// <summary>FK — target device.</summary>
        public int DeviceId { get; set; }

        /// <summary>UTC timestamp when this connection was created.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>Owner.</summary>
        public User User { get; set; } = null!;

        /// <summary>Source template.</summary>
        public Template Template { get; set; } = null!;

        /// <summary>Target device.</summary>
        public Device Device { get; set; } = null!;
    }
}
