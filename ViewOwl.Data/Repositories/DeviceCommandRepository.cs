using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IDeviceCommandRepository"/>.
    /// </summary>
    public sealed class DeviceCommandRepository : IDeviceCommandRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public DeviceCommandRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<DeviceCommand>> GetPendingByDeviceAsync(int deviceId, CancellationToken ct = default)
            => await _db.DeviceCommands
                        .AsNoTracking()
                        .Where(dc => dc.DeviceId == deviceId && dc.Status == "pending")
                        .OrderBy(dc => dc.CreatedAt)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<DeviceCommand?> GetByIdAsync(int id, CancellationToken ct = default)
            => await _db.DeviceCommands
                        .AsNoTracking()
                        .FirstOrDefaultAsync(dc => dc.Id == id, ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<DeviceCommand> CreateAsync(DeviceCommand command, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            _db.DeviceCommands.Add(command);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return command;
        }

        /// <inheritdoc/>
        public async Task UpdateAsync(DeviceCommand command, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            _db.DeviceCommands.Update(command);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
        {
            var old = await _db.DeviceCommands
                               .Where(dc => dc.CreatedAt < cutoff)
                               .ToListAsync(ct)
                               .ConfigureAwait(false);

            if (old.Count > 0)
            {
                _db.DeviceCommands.RemoveRange(old);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
    }
}
