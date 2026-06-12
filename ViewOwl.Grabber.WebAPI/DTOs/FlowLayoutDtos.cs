namespace ViewOwl.Grabber.WebAPI.DTOs
{
    /// <summary>
    /// A single node's canvas position.
    /// </summary>
    public sealed record NodePositionDto(double X, double Y);

    /// <summary>
    /// Request body for <c>PUT /api/flow-layout</c>.
    /// </summary>
    /// <param name="Positions">Map of node ID → position.</param>
    public sealed record FlowLayoutRequestDto(
        IReadOnlyDictionary<string, NodePositionDto> Positions);

    /// <summary>
    /// Response body for <c>GET /api/flow-layout</c>.
    /// </summary>
    /// <param name="Positions">Map of node ID → position.</param>
    public sealed record FlowLayoutResponseDto(
        IReadOnlyDictionary<string, NodePositionDto> Positions);
}
