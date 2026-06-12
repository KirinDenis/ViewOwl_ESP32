using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IConnectionRepository"/>.
    /// </summary>
    public sealed class ConnectionRepository : IConnectionRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public ConnectionRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Connection>> GetByUserAsync(int userId, CancellationToken ct = default)
            => await _db.Connections
                        .AsNoTracking()
                        .Include(c => c.Template)
                        .Include(c => c.Device)
                        .Where(c => c.UserId == userId)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<Connection?> GetByIdAsync(int id, CancellationToken ct = default)
            => await _db.Connections
                        .AsNoTracking()
                        .Include(c => c.Template)
                        .Include(c => c.Device)
                        .FirstOrDefaultAsync(c => c.Id == id, ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Connection>> GetByDeviceAsync(int deviceId, CancellationToken ct = default)
            => await _db.Connections
                        .AsNoTracking()
                        .Include(c => c.Template)
                        .Where(c => c.DeviceId == deviceId)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<Connection?> GetByTemplateAndDeviceAsync(int templateId, int deviceId, CancellationToken ct = default)
            => await _db.Connections
                        .AsNoTracking()
                        .FirstOrDefaultAsync(c => c.TemplateId == templateId && c.DeviceId == deviceId, ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<Connection> CreateAsync(Connection connection, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            _db.Connections.Add(connection);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return connection;
        }

        /// <inheritdoc/>
        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            Connection? connection = await _db.Connections.FindAsync([id], ct).ConfigureAwait(false);
            if (connection is not null)
            {
                _db.Connections.Remove(connection);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(int templateId, int deviceId, CancellationToken ct = default)
            => await _db.Connections
                        .AnyAsync(c => c.TemplateId == templateId && c.DeviceId == deviceId, ct)
                        .ConfigureAwait(false);
    }
}
