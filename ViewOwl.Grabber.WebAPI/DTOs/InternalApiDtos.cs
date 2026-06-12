namespace ViewOwl.Grabber.WebAPI.DTOs
{
    /// <summary>
    /// Heartbeat payload sent by the UDP Server when a device sends a HELLO or PING packet.
    /// </summary>
    public sealed record DeviceHeartbeatDto(
        string Token,
        string? DisplayType,
        int? DisplayWidth,
        int? DisplayHeight,
        string? FirmwareVersion,
        string IpAddress,
        int? Uptime,
        int? FreeHeap,
        int? WifiRssi,
        /// <summary>
        /// WiFi MAC address in "XX:XX:XX:XX:XX:XX" format, extracted from an extended HELLO
        /// with payload ≥ 31 bytes. <c>null</c> for legacy firmware or PING packets.
        /// </summary>
        string? MacAddress = null,
        /// <summary>
        /// CRC32 of the last frame the device rendered, from HELLO header.Seq.
        /// <c>null</c> when the device has no frame yet (value 0 from firmware).
        /// </summary>
        uint? ClientFrameCrc = null
    );

    /// <summary>
    /// Notification sent by the UDP Server when it begins streaming a frame to a device.
    /// </summary>
    public sealed record DeviceTransferStartedDto(
        string Token,
        int TemplateId,
        int ImageSizeBytes,
        int? FileFlag = null   // 0x00=Raw, 0x03=LZ4+palette, 0x04=Delta; null = unknown
    );

    /// <summary>
    /// Notification sent by the UDP Server during an active transfer (every W=8 chunks ACKed).
    /// </summary>
    public sealed record DeviceTransferProgressDto(
        string Token,
        int ChunksSent,
        int TotalChunks,
        int ElapsedMs,
        int Retries
    );

    /// <summary>
    /// Notification sent by the UDP Server when an image transfer to a device completes.
    /// </summary>
    public sealed record DeviceGrabCompleteDto(
        string Token,
        int TemplateId,
        bool Success,
        string? ErrorMessage,
        int? ImageSizeBytes,
        int? DurationMs,
        string? Outcome = null,       // "success" | "not_modified" | "fail"
        int? Retries = null,
        int? ThroughputKBps = null
    );

    /// <summary>
    /// Standard response envelope for all internal API endpoints.
    /// </summary>
    public sealed record InternalApiResponse(
        bool Success,
        string? Message = null
    );

    /// <summary>
    /// Payload sent by the UDP Server when a device HELLO cannot be authenticated.
    /// </summary>
    public sealed record DeviceKnockingDto(
        string Token,
        string IpAddress,
        string Reason
    );
}
