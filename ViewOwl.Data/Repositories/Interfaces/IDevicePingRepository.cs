using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Data access contract for <see cref="DevicePing"/> records.
    /// </summary>
    public interface IDevicePingRepository
    {
        /// <summary>
        /// Returns the most recent <paramref name="count"/> pings for <paramref name="deviceId"/>
        /// ordered newest-first (no-tracking).
        /// </summary>
        Task<IReadOnlyList<DevicePing>> GetRecentAsync(int deviceId, int count = 100, CancellationToken ct = default);

        /// <summary>Persists a new ping record.</summary>
        Task RecordAsync(DevicePing ping, CancellationToken ct = default);

        /// <summary>
        /// Returns the ratio of successful pings in the last <paramref name="hours"/> hours
        /// as a value between 0.0 and 1.0. Returns <c>null</c> when no pings exist.
        /// </summary>
        Task<double?> GetUptimeRatioAsync(int deviceId, int hours = 24, CancellationToken ct = default);

        /// <summary>
        /// Returns the count of ping records for <paramref name="deviceId"/> since UTC midnight today.
        /// Each ping record represents one UDP session (transfer or NOT_MODIFIED).
        /// </summary>
        Task<int> GetTodayCountAsync(int deviceId, CancellationToken ct = default);
    }
}
