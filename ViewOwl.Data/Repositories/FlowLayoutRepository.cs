using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IFlowLayoutRepository"/>.
    /// </summary>
    public sealed class FlowLayoutRepository : IFlowLayoutRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public FlowLayoutRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<FlowLayout?> GetByUserAsync(int userId, CancellationToken ct = default)
            => await _db.FlowLayouts
                        .AsNoTracking()
                        .FirstOrDefaultAsync(fl => fl.UserId == userId, ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task UpsertAsync(int userId, string positionsJson, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(positionsJson);

            FlowLayout? existing = await _db.FlowLayouts
                                            .FirstOrDefaultAsync(fl => fl.UserId == userId, ct)
                                            .ConfigureAwait(false);

            if (existing is null)
            {
                _db.FlowLayouts.Add(new FlowLayout
                {
                    UserId        = userId,
                    PositionsJson = positionsJson,
                    UpdatedAt     = DateTime.UtcNow,
                });
            }
            else
            {
                existing.PositionsJson = positionsJson;
                existing.UpdatedAt     = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
