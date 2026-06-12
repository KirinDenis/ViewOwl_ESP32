namespace ViewOwl.UDP.Server.DTOs
{
    /// <summary>
    /// UDP Server status snapshot returned by <c>GET /status</c>.
    /// </summary>
    internal sealed record UdpServerStatusDto(
        long TotalClients,
        long TotalAuth,
        int MaxClients,
        int CurrentClients,
        long TotalMBSend,
        long TotalAuthErrors,
        long TotalWrongPackets,
        DateTime ServerStartedAt);

    /// <summary>
    /// Active device session info returned by <c>GET /devices</c>.
    /// </summary>
    internal sealed record DeviceSessionDto(
        string Token,
        string IpAddress,
        int Port,
        ushort SessionId,
        DateTime ConnectedAt,
        DateTime LastPacketAt);

    /// <summary>
    /// Request body for <c>POST /command/{token}</c>.
    /// </summary>
    internal sealed record SendCommandRequestDto(
        string CommandType,  // "reboot", "config"
        string? Payload);    // optional JSON payload

    /// <summary>
    /// Response for command operations.
    /// </summary>
    internal sealed record CommandResponseDto(
        bool Success,
        string? Message = null);
}
