namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Records a single state-transition event across the entire grab → UDP → device pipeline.
    /// Every significant action (grab queued, frame sent, UDP session opened, device came online, …)
    /// writes one row so the system has end-to-end observability without relying on log files.
    /// </summary>
    /// <remarks>
    /// The <see cref="EventType"/> column is a free-form string constant defined in
    /// <c>PipelineEventTypes</c> to avoid magic strings at call sites.
    /// <see cref="DeviceId"/> and <see cref="TemplateId"/> are both nullable because some events
    /// are device-only (HELLO/PING) and some are template-only (grab_queued when no device is
    /// yet assigned).
    /// </remarks>
    public sealed class PipelineEvent
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>UTC timestamp of the event.</summary>
        public DateTime Ts { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Event type string — one of the constants in <c>PipelineEventTypes</c>.
        /// Examples: <c>grab_queued</c>, <c>udp_frame_sent</c>, <c>device_online</c>.
        /// </summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>FK — the device involved in this event (null for grab-only events).</summary>
        public int? DeviceId { get; set; }

        /// <summary>FK — the template involved in this event (null for device-only events).</summary>
        public int? TemplateId { get; set; }

        /// <summary>
        /// UDP session identifier assigned by the server at AUTH time.
        /// Null for non-UDP events.
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>Whether the operation represented by this event succeeded.</summary>
        public bool? Success { get; set; }

        /// <summary>Duration of the operation in milliseconds (null when not applicable).</summary>
        public int? DurationMs { get; set; }

        /// <summary>
        /// JSON blob with event-specific detail fields.
        /// Schema varies by <see cref="EventType"/>; see design system docs for per-type schemas.
        /// </summary>
        public string? Payload { get; set; }

        /// <summary>Human-readable error description (null on success).</summary>
        public string? ErrorMessage { get; set; }

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>The device involved in this event (null for grab-only events).</summary>
        public Device? Device { get; set; }

        /// <summary>The template involved in this event (null for device-only events).</summary>
        public Template? Template { get; set; }
    }
}
