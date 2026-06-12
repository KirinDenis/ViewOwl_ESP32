namespace ViewOwl.Data.Models
{
    /// <summary>
    /// An HTML display template owned by a user and assignable to devices.
    /// </summary>
    public sealed class Template
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>Human-readable template name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>FK — user who created this template.</summary>
        public int UserId { get; set; }

        /// <summary>Full HTML content of the template.</summary>
        public string HtmlContent { get; set; } = string.Empty;

        /// <summary>UTC timestamp when the template was created.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When <c>true</c> the grabber injects a <c>filter: grayscale(1)</c> CSS rule
        /// before capture, producing a monochrome frame. Useful for dark/sci-fi display themes.
        /// </summary>
        public bool Monochrome { get; set; }

        /// <summary>
        /// When <c>true</c> this template is listed on the public landing page gallery.
        /// Defaults to <c>false</c> for new templates; existing templates were migrated with <c>true</c>.
        /// </summary>
        public bool IsPublic { get; set; }

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>Owner.</summary>
        public User? User { get; set; }

        /// <summary>Devices that have this template in their collection (many-to-many).</summary>
        public ICollection<Device> Devices { get; private set; } = [];

        /// <summary>Grab log entries for this template, newest first.</summary>
        public ICollection<GrabLog> GrabLogs { get; private set; } = [];
    }
}
