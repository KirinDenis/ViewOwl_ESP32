namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Records a single on-demand grab (screenshot) request for an HTML template.
    /// Created by <c>POST /api/templates/{id}/grab</c>; updated by <c>GrabWorker</c>
    /// after the screenshot completes or fails.
    /// </summary>
    public sealed class GrabLog
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>FK — the template that was grabbed.</summary>
        public int TemplateId { get; set; }

        /// <summary>
        /// GUID that identifies the output files:
        /// <c>{ExchangeFolder}/{TokenGuid}.bin</c> (pixel data) and
        /// <c>{ExchangeFolder}/{TokenGuid}_TEST.png</c> (debug PNG).
        /// </summary>
        public Guid TokenGuid { get; set; }

        /// <summary>UTC timestamp when the grab was requested.</summary>
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

        /// <summary>UTC timestamp when the grab finished (<c>null</c> while pending).</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// <c>true</c> on success, <c>false</c> on failure, <c>null</c> while pending.
        /// </summary>
        public bool? Success { get; set; }

        /// <summary>Human-readable status message (error description on failure).</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Pixel width used for the screenshot.</summary>
        public int Width { get; set; } = 480;

        /// <summary>Pixel height used for the screenshot.</summary>
        public int Height { get; set; } = 320;

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>The template that was grabbed.</summary>
        public Template? Template { get; set; }
    }
}
