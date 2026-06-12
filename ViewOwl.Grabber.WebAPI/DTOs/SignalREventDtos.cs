namespace ViewOwl.Grabber.WebAPI.DTOs
{
    /// <summary>
    /// Pushed to the user's SignalR group when a device comes online or goes offline.
    /// </summary>
    public sealed record DeviceStatusChangedEvent(
        int DeviceId,
        string Status,       // "Online" | "Offline"
        DateTime? LastSeenAt,
        string? IpAddress
    );

    /// <summary>
    /// Pushed to the user's SignalR group when a PING packet is received from a device.
    /// </summary>
    public sealed record DevicePingEvent(
        int DeviceId,
        DateTime Timestamp,
        int? Uptime,
        int? FreeHeap,
        int? WifiRssi
    );

    /// <summary>
    /// Pushed to the user's SignalR group when the grabber worker captures a template.
    /// </summary>
    public sealed record GrabberUpdateEvent(
        int TemplateId,
        bool Success,
        string? ErrorMessage,
        DateTime Timestamp
    );

    /// <summary>
    /// Pushed to the user's SignalR group when the UDP Server starts streaming
    /// an image frame to a device (AUTH sent, DATA loop beginning).
    /// </summary>
    public sealed record DeviceTransferStartedEvent(
        int DeviceId,
        int TemplateId,
        int ImageSizeBytes,
        DateTime Timestamp,
        int? FileFlag = null   // 0x00=Raw, 0x03=LZ4+palette, 0x04=Delta; null = unknown
    );

    /// <summary>
    /// Pushed during an active transfer every W=8 ACKed chunks so the dashboard
    /// can show a live progress indicator.
    /// </summary>
    public sealed record DeviceTransferProgressEvent(
        int DeviceId,
        int ChunksSent,
        int TotalChunks,
        int ElapsedMs,
        int Retries,
        DateTime Timestamp
    );

    /// <summary>
    /// Pushed to the user's SignalR group when the UDP Server finishes delivering
    /// an image frame to a device.
    /// </summary>
    public sealed record DeviceGrabCompletedEvent(
        int DeviceId,
        int TemplateId,
        bool Success,
        string? ErrorMessage,
        int? ImageSizeBytes,
        int? DurationMs,
        DateTime Timestamp,
        string? Outcome = null,       // "success" | "not_modified" | "fail"
        int? Retries = null,
        int? ThroughputKBps = null
    );

    /// <summary>
    /// Pushed to the user's SignalR group when any session saves new node positions.
    /// All other sessions apply these positions so the canvas stays in sync.
    /// </summary>
    public sealed record FlowLayoutChangedEvent(
        IReadOnlyDictionary<string, NodePositionPayload> Positions
    );

    /// <summary>
    /// A node's canvas coordinates, embedded in <see cref="FlowLayoutChangedEvent"/>.
    /// </summary>
    public sealed record NodePositionPayload(double X, double Y);

    /// <summary>
    /// Pushed to the user's SignalR group when a connection is created or deleted.
    /// All sessions rebuild their edges from the authoritative list.
    /// </summary>
    public sealed record ConnectionsChangedEvent(
        IReadOnlyList<ConnectionResponseDto> Connections
    );

    /// <summary>
    /// Pushed when a device sends a HELLO but cannot be authenticated.
    /// Lets the dashboard show which devices are trying to connect but failing.
    /// </summary>
    public sealed record DeviceKnockingEvent(
        int? DeviceId,       // null when the token is not in the database
        string? DeviceName,  // null when the device is unknown
        string IpAddress,
        string Token,        // raw token string from HELLO payload ("?" if unparseable)
        string Reason,       // "bad-token" | "no-frame"
        DateTime Timestamp
    );

    /// <summary>
    /// Broadcast to ALL connected dashboard clients when a CI/CD deploy starts or completes.
    /// Phase values: <c>"starting"</c> | <c>"complete"</c>.
    /// </summary>
    public sealed record DeployNotificationEvent(
        string Phase,        // "starting" | "complete"
        string? Commit,      // short commit SHA (first 7 chars of GITHUB_SHA)
        DateTime Timestamp
    );
}
