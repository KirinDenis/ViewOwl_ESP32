using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Data access contract for Template ↔ Device connections.
    /// </summary>
    public interface IConnectionRepository
    {
        /// <summary>Returns all connections for a specific user, including Template and Device.</summary>
        Task<IReadOnlyList<Connection>> GetByUserAsync(int userId, CancellationToken ct = default);

        /// <summary>Returns the connection with the given <paramref name="id"/>, or <c>null</c>.</summary>
        Task<Connection?> GetByIdAsync(int id, CancellationToken ct = default);

        /// <summary>Returns all connections for a specific device, including Template.</summary>
        Task<IReadOnlyList<Connection>> GetByDeviceAsync(int deviceId, CancellationToken ct = default);

        /// <summary>Returns the connection between a specific template and device, or <c>null</c>.</summary>
        Task<Connection?> GetByTemplateAndDeviceAsync(int templateId, int deviceId, CancellationToken ct = default);

        /// <summary>Persists a new connection and returns the saved entity.</summary>
        Task<Connection> CreateAsync(Connection connection, CancellationToken ct = default);

        /// <summary>Deletes the connection with the given <paramref name="id"/>. No-op if not found.</summary>
        Task DeleteAsync(int id, CancellationToken ct = default);

        /// <summary>Returns <c>true</c> if a connection between the given template and device already exists.</summary>
        Task<bool> ExistsAsync(int templateId, int deviceId, CancellationToken ct = default);
    }
}
