using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IDevicePingRepository"/>.
    /// </summary>
    public sealed class DevicePingRepository : IDevicePingRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public DevicePingRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<DevicePing>> GetRecentAsync(
            int deviceId, int count = 100, CancellationToken ct = default)
            => await _db.DevicePings
                        .AsNoTracking()
                        .Where(p => p.DeviceId == deviceId)
                        .OrderByDescending(p => p.Timestamp)
                        .Take(count)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task RecordAsync(DevicePing ping, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(ping);
            _db.DevicePings.Add(ping);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<double?> GetUptimeRatioAsync(
            int deviceId, int hours = 24, CancellationToken ct = default)
        {
            DateTime since = DateTime.UtcNow.AddHours(-hours);

            int total = await _db.DevicePings
                                 .AsNoTracking()
                                 .CountAsync(p => p.DeviceId == deviceId && p.Timestamp >= since, ct)
                                 .ConfigureAwait(false);

            if (total == 0)
                return null;

            int successful = await _db.DevicePings
                                      .AsNoTracking()
                                      .CountAsync(p => p.DeviceId == deviceId
                                                    && p.Timestamp >= since
                                                    && p.Success, ct)
                                      .ConfigureAwait(false);

            return (double)successful / total;
        }

        /// <inheritdoc/>
        public async Task<int> GetTodayCountAsync(int deviceId, CancellationToken ct = default)
        {
            DateTime startOfDay = DateTime.UtcNow.Date;
            return await _db.DevicePings
                            .AsNoTracking()
                            .CountAsync(p => p.DeviceId == deviceId && p.Timestamp >= startOfDay, ct)
                            .ConfigureAwait(false);
        }
    }
}
