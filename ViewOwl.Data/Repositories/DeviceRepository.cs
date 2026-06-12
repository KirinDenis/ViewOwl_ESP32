using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IDeviceRepository"/>.
    /// </summary>
    public sealed class DeviceRepository : IDeviceRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public DeviceRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<bool> NameExistsForUserAsync(int userId, string name, int? excludeId = null, CancellationToken ct = default)
            => await _db.Devices
                        .AsNoTracking()
                        .Where(d => d.UserId == userId
                                 && EF.Functions.Like(d.Name, name)
                                 && (excludeId == null || d.Id != excludeId))
                        .AnyAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Device>> GetByUserAsync(int userId, CancellationToken ct = default)
            => await _db.Devices
                        .AsNoTracking()
                        .Where(d => d.UserId == userId)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<Device?> GetByIdAsync(int id, CancellationToken ct = default)
            => await _db.Devices
                        .AsNoTracking()
                        .Include(d => d.Templates)
                        .FirstOrDefaultAsync(d => d.Id == id, ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<Device?> GetByTokenAsync(string token, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(token);
            return await _db.Devices
                            .AsNoTracking()
                            .FirstOrDefaultAsync(d => d.Token == token, ct)
                            .ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<Device> CreateAsync(Device device, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            _db.Devices.Add(device);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return device;
        }

        /// <inheritdoc/>
        public async Task UpdateAsync(Device device, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            _db.Devices.Update(device);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            Device? device = await _db.Devices.FindAsync([id], ct).ConfigureAwait(false);
            if (device is not null)
            {
                _db.Devices.Remove(device);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public async Task AssignTemplateAsync(int deviceId, int templateId, CancellationToken ct = default)
        {
            // Load with tracking so EF can manage the join table
            Device? device = await _db.Devices
                                      .Include(d => d.Templates)
                                      .FirstOrDefaultAsync(d => d.Id == deviceId, ct)
                                      .ConfigureAwait(false);

            Template? template = await _db.Templates
                                          .FindAsync([templateId], ct)
                                          .ConfigureAwait(false);

            if (device is null || template is null)
                return;

            // Avoid duplicate entries in the join table
            if (!device.Templates.Any(t => t.Id == templateId))
                device.Templates.Add(template);

            // Keep ActiveTemplateId in sync with the newly assigned template
            device.ActiveTemplateId = templateId;

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task SetActiveTemplateAsync(int deviceId, int? templateId, CancellationToken ct = default)
        {
            Device? device = await _db.Devices.FindAsync([deviceId], ct).ConfigureAwait(false);
            if (device is null)
                return;

            device.ActiveTemplateId = templateId;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task UpdateHeartbeatAsync(int deviceId, DateTime lastSeenAt, string ipAddress, CancellationToken ct = default)
        {
            // ExecuteUpdateAsync issues a direct SQL UPDATE without loading the entity into
            // the change tracker, so subsequent calls within the same DbContext scope can
            // attach or update the same row without a "key already tracked" conflict.
            await _db.Devices
                     .Where(d => d.Id == deviceId)
                     .ExecuteUpdateAsync(
                         s => s.SetProperty(d => d.LastSeenAt, lastSeenAt)
                               .SetProperty(d => d.Status,     DeviceStatus.Online)
                               .SetProperty(d => d.IpAddress,  ipAddress),
                         ct)
                     .ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task UpdateHardwareInfoAsync(
            int deviceId,
            string? displayType,
            int? displayWidth,
            int? displayHeight,
            string? firmwareVersion,
            string? macAddress,
            uint? lastFrameCrc = null,
            CancellationToken ct = default)
        {
            // Each SetProperty uses a two-argument lambda so EF translates null parameters
            // to COALESCE(@p, column) — existing values are preserved when the argument is null.
            await _db.Devices
                     .Where(d => d.Id == deviceId)
                     .ExecuteUpdateAsync(
                         s => s.SetProperty(d => d.DisplayType,    d => displayType     ?? d.DisplayType)
                               .SetProperty(d => d.DisplayWidth,   d => displayWidth    ?? d.DisplayWidth)
                               .SetProperty(d => d.DisplayHeight,  d => displayHeight   ?? d.DisplayHeight)
                               .SetProperty(d => d.FirmwareVersion,d => firmwareVersion ?? d.FirmwareVersion)
                               .SetProperty(d => d.MacAddress,     d => macAddress      ?? d.MacAddress)
                               .SetProperty(d => d.LastFrameCrc,   d => lastFrameCrc    ?? d.LastFrameCrc),
                         ct)
                     .ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task UpdateLiveMetricsAsync(
            int deviceId,
            int? wifiRssi,
            int? freeHeap,
            int? uptimeSeconds,
            CancellationToken ct = default)
        {
            // Derive boot time from uptime: bootAt = now − uptimeSeconds.
            // If the new bootAt differs from the stored one by more than 60 s, the device
            // rebooted — update LastBootAt so the dashboard shows accurate live uptime.
            DateTime? newBootAt = uptimeSeconds.HasValue
                ? DateTime.UtcNow - TimeSpan.FromSeconds(uptimeSeconds.Value)
                : (DateTime?)null;

            await _db.Devices
                     .Where(d => d.Id == deviceId)
                     .ExecuteUpdateAsync(
                         s => s.SetProperty(d => d.WifiRssi,      d => wifiRssi      ?? d.WifiRssi)
                               .SetProperty(d => d.FreeHeap,      d => freeHeap      ?? d.FreeHeap)
                               .SetProperty(d => d.UptimeSeconds, d => uptimeSeconds ?? d.UptimeSeconds)
                               .SetProperty(d => d.LastBootAt,
                                   d => newBootAt == null              ? d.LastBootAt :   // no uptime in this PING — keep existing
                                        d.LastBootAt == null           ? newBootAt    :   // first PING ever
                                        Math.Abs((newBootAt.Value - d.LastBootAt.Value).TotalSeconds) > 60
                                            ? newBootAt : d.LastBootAt),                  // reboot detected — update
                         ct)
                     .ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task IncrementFramesSentAsync(int deviceId, CancellationToken ct = default)
        {
            await _db.Devices
                     .Where(d => d.Id == deviceId)
                     .ExecuteUpdateAsync(
                         s => s.SetProperty(d => d.FramesSentCount, d => d.FramesSentCount + 1),
                         ct)
                     .ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<Device?> GetByMacAndUserAsync(
            string macAddress,
            int userId,
            int excludeDeviceId,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(macAddress);
            return await _db.Devices
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                d => d.MacAddress == macAddress
                                  && d.UserId     == userId
                                  && d.Id         != excludeDeviceId,
                                ct)
                            .ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Device>> GetNotSeenSinceAsync(DateTime since, CancellationToken ct = default)
            => await _db.Devices
                        .AsNoTracking()
                        .Where(d => d.LastSeenAt == null || d.LastSeenAt < since)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Device>> GetOnlineAsync(CancellationToken ct = default)
            => await _db.Devices
                        .AsNoTracking()
                        .Where(d => d.Status == DeviceStatus.Online)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Device>> GetByActiveTemplateIdAsync(int templateId, CancellationToken ct = default)
            => await _db.Devices
                        .AsNoTracking()
                        .Where(d => d.ActiveTemplateId == templateId)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
    }
}
