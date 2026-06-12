using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Data access contract for <see cref="Device"/> entities.
    /// </summary>
    public interface IDeviceRepository
    {
        /// <summary>Returns all devices owned by <paramref name="userId"/> (no-tracking).</summary>
        Task<IReadOnlyList<Device>> GetByUserAsync(int userId, CancellationToken ct = default);

        /// <summary>Returns the device with the given <paramref name="id"/>, or <c>null</c>.</summary>
        Task<Device?> GetByIdAsync(int id, CancellationToken ct = default);

        /// <summary>Returns the device with the given permanent <paramref name="token"/>, or <c>null</c>.</summary>
        Task<Device?> GetByTokenAsync(string token, CancellationToken ct = default);

        /// <summary>
        /// Returns <c>true</c> when <paramref name="userId"/> already owns a device named
        /// <paramref name="name"/> (case-insensitive), optionally excluding <paramref name="excludeId"/>.
        /// </summary>
        Task<bool> NameExistsForUserAsync(int userId, string name, int? excludeId = null, CancellationToken ct = default);

        /// <summary>Persists a new device and returns the saved entity.</summary>
        Task<Device> CreateAsync(Device device, CancellationToken ct = default);

        /// <summary>Saves changes to an existing device.</summary>
        Task UpdateAsync(Device device, CancellationToken ct = default);

        /// <summary>Deletes the device with the given <paramref name="id"/>. No-op if not found.</summary>
        Task DeleteAsync(int id, CancellationToken ct = default);

        /// <summary>Associates <paramref name="templateId"/> with <paramref name="deviceId"/> (many-to-many).</summary>
        Task AssignTemplateAsync(int deviceId, int templateId, CancellationToken ct = default);

        /// <summary>
        /// Sets <see cref="Device.ActiveTemplateId"/> for <paramref name="deviceId"/>.
        /// Pass <c>null</c> to clear the active template. No-op if the device is not found.
        /// </summary>
        Task SetActiveTemplateAsync(int deviceId, int? templateId, CancellationToken ct = default);

        /// <summary>
        /// Updates <see cref="Device.LastSeenAt"/>, <see cref="Device.Status"/> (→ Online),
        /// and <see cref="Device.IpAddress"/> when a heartbeat packet is received.
        /// No-op if the device is not found.
        /// </summary>
        Task UpdateHeartbeatAsync(int deviceId, DateTime lastSeenAt, string ipAddress, CancellationToken ct = default);

        /// <summary>Returns all devices whose <see cref="Device.LastSeenAt"/> is before <paramref name="since"/> (or null).</summary>
        Task<IReadOnlyList<Device>> GetNotSeenSinceAsync(DateTime since, CancellationToken ct = default);

        /// <summary>Returns all devices whose <see cref="Device.Status"/> is <see cref="DeviceStatus.Online"/>.</summary>
        Task<IReadOnlyList<Device>> GetOnlineAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns all devices whose <see cref="Device.ActiveTemplateId"/> equals
        /// <paramref name="templateId"/>. Used to schedule frame refreshes after a grab completes.
        /// </summary>
        Task<IReadOnlyList<Device>> GetByActiveTemplateIdAsync(int templateId, CancellationToken ct = default);

        /// <summary>
        /// Updates hardware capability fields (<see cref="Device.DisplayType"/>,
        /// <see cref="Device.DisplayWidth"/>, <see cref="Device.DisplayHeight"/>,
        /// <see cref="Device.FirmwareVersion"/>, <see cref="Device.MacAddress"/>)
        /// using a direct SQL UPDATE that bypasses the EF change tracker.
        /// Only non-null arguments overwrite the stored value.
        /// No-op if the device is not found.
        /// </summary>
        Task UpdateHardwareInfoAsync(
            int deviceId,
            string? displayType,
            int? displayWidth,
            int? displayHeight,
            string? firmwareVersion,
            string? macAddress,
            uint? lastFrameCrc = null,
            CancellationToken ct = default);

        /// <summary>
        /// Persists live telemetry received from a PING packet:
        /// <see cref="Device.WifiRssi"/>, <see cref="Device.FreeHeap"/>, <see cref="Device.UptimeSeconds"/>.
        /// Uses a direct SQL UPDATE; only non-null arguments overwrite the stored value.
        /// No-op if the device is not found.
        /// </summary>
        Task UpdateLiveMetricsAsync(
            int deviceId,
            int? wifiRssi,
            int? freeHeap,
            int? uptimeSeconds,
            CancellationToken ct = default);

        /// <summary>
        /// Atomically increments <see cref="Device.FramesSentCount"/> by one.
        /// Called after each successful UDP frame delivery.
        /// </summary>
        Task IncrementFramesSentAsync(int deviceId, CancellationToken ct = default);

        /// <summary>
        /// Returns the device owned by <paramref name="userId"/> whose
        /// <see cref="Device.MacAddress"/> equals <paramref name="macAddress"/>,
        /// excluding the device identified by <paramref name="excludeDeviceId"/>.
        /// Returns <c>null</c> when no such device exists.
        /// Used to detect a re-flashed physical device and remove the stale record.
        /// </summary>
        Task<Device?> GetByMacAndUserAsync(
            string macAddress,
            int userId,
            int excludeDeviceId,
            CancellationToken ct = default);
    }
}
