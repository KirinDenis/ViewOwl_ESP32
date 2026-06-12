using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Data access contract for device commands (reboot, config, etc.).
    /// </summary>
    public interface IDeviceCommandRepository
    {
        /// <summary>Returns all pending commands for a specific device, ordered oldest-first.</summary>
        Task<IReadOnlyList<DeviceCommand>> GetPendingByDeviceAsync(int deviceId, CancellationToken ct = default);

        /// <summary>Returns the command with the given <paramref name="id"/>, or <c>null</c>.</summary>
        Task<DeviceCommand?> GetByIdAsync(int id, CancellationToken ct = default);

        /// <summary>Persists a new command and returns the saved entity.</summary>
        Task<DeviceCommand> CreateAsync(DeviceCommand command, CancellationToken ct = default);

        /// <summary>Saves status changes to an existing command (e.g., pending → sent → ack).</summary>
        Task UpdateAsync(DeviceCommand command, CancellationToken ct = default);

        /// <summary>Deletes all commands created before <paramref name="cutoff"/> (cleanup job).</summary>
        Task DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
    }
}
