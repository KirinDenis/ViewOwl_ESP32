namespace ViewOwl.Grabber.WebAPI.DTOs
{
    /// <summary>
    /// Request to create a connection between a template and a device.
    /// </summary>
    public sealed record CreateConnectionRequestDto(
        int TemplateId,
        int DeviceId);

    /// <summary>
    /// Response after creating or reading a connection.
    /// </summary>
    public sealed record ConnectionResponseDto(
        int Id,
        int TemplateId,
        int DeviceId,
        DateTime CreatedAt)
    {
        /// <summary>Maps a <see cref="Data.Models.Connection"/> entity to this DTO.</summary>
        public static ConnectionResponseDto From(Data.Models.Connection c)
        {
            ArgumentNullException.ThrowIfNull(c);
            return new(c.Id, c.TemplateId, c.DeviceId, c.CreatedAt);
        }
    }

    /// <summary>
    /// Error response for connection operations.
    /// </summary>
    public sealed record ConnectionErrorDto(
        string Error,
        string? Details = null);
}
