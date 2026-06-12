namespace ViewOwl.Grabber.WebAPI.DTOs
{
    /// <summary>
    /// ISA-101 health row for one template — returned by <c>GET /api/health/templates</c>.
    /// </summary>
    /// <param name="Id">Template db id.</param>
    /// <param name="Name">Human-readable template name.</param>
    /// <param name="Health">
    /// One of: <c>healthy</c> | <c>overdue</c> | <c>failed</c> | <c>never</c> | <c>unassigned</c>.
    /// </param>
    /// <param name="LastGrabAt">UTC timestamp of the most recent grab attempt (null when never grabbed).</param>
    /// <param name="LastGrabOk">Whether the most recent grab succeeded (null when never grabbed).</param>
    /// <param name="LastErrorMessage">Error from the most recent failed grab (null on success or never).</param>
    /// <param name="AssignedDeviceCount">Number of devices that have this template assigned as their active template.</param>
    public sealed record TemplateHealthDto(
        int       Id,
        string    Name,
        string    Health,
        DateTime? LastGrabAt,
        bool?     LastGrabOk,
        string?   LastErrorMessage,
        int       AssignedDeviceCount
    );


    /// <summary>
    /// Request body for <c>POST /api/pipeline/event</c>.
    /// Called by the UDP Server to log every pipeline state-transition.
    /// </summary>
    public sealed record LogPipelineEventDto(
        /// <summary>Event type constant — one of <c>PipelineEventTypes.*</c>.</summary>
        string EventType,

        /// <summary>Device DB id involved in this event (null for grab-only events).</summary>
        int? DeviceId = null,

        /// <summary>Template DB id involved in this event (null for device-only events).</summary>
        int? TemplateId = null,

        /// <summary>UDP session identifier assigned at AUTH time (null for non-UDP events).</summary>
        string? SessionId = null,

        /// <summary>Whether the operation succeeded.</summary>
        bool? Success = null,

        /// <summary>Operation duration in milliseconds (null when not applicable).</summary>
        int? DurationMs = null,

        /// <summary>JSON-serialized event-specific detail fields (null when not needed).</summary>
        string? Payload = null,

        /// <summary>Human-readable error description (null on success).</summary>
        string? ErrorMessage = null
    );

    /// <summary>
    /// A single pipeline event row returned by <c>GET /api/pipeline/events</c>.
    /// </summary>
    public sealed record PipelineEventDto(
        int      Id,
        DateTime Ts,
        string   EventType,
        int?     DeviceId,
        int?     TemplateId,
        string?  SessionId,
        bool?    Success,
        int?     DurationMs,
        string?  Payload,
        string?  ErrorMessage
    );

    // ── Pipeline summary (4-lane view) ────────────────────────────────────────

    /// <summary>
    /// The latest event cell for one lane in the 4-lane pipeline summary view.
    /// </summary>
    public sealed record PipelineEventCellDto(
        int      Id,
        string   EventType,
        DateTime Ts,
        bool?    Success,
        int?     DurationMs,
        string?  ErrorMessage
    );

    /// <summary>
    /// Device row inside a <see cref="PipelineSummaryRowDto"/> — covers the UDP and Device lanes.
    /// </summary>
    public sealed record PipelineSummaryDeviceDto(
        int                   Id,
        string                Name,
        string                Status,
        PipelineEventCellDto? LastUdpEvent,
        PipelineEventCellDto? LastDeviceEvent
    );

    /// <summary>
    /// One row in the 4-lane pipeline summary: one template and all devices assigned to it.
    /// Returned by <c>GET /api/pipeline/summary</c>.
    /// </summary>
    public sealed record PipelineSummaryRowDto(
        int                           TemplateId,
        string                        TemplateName,
        PipelineEventCellDto?         LastGrabEvent,
        IReadOnlyList<PipelineSummaryDeviceDto> Devices
    );
}
