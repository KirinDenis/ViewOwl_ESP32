using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="ITemplateRepository"/>.
    /// </summary>
    public sealed class TemplateRepository : ITemplateRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public TemplateRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<bool> NameExistsForUserAsync(int userId, string name, int? excludeId = null, CancellationToken ct = default)
            => await _db.Templates
                        .AsNoTracking()
                        .Where(t => t.UserId == userId
                                 && EF.Functions.Like(t.Name, name)
                                 && (excludeId == null || t.Id != excludeId))
                        .AnyAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Template>> GetAllAsync(CancellationToken ct = default)
            => await _db.Templates
                        .AsNoTracking()
                        .OrderBy(t => t.UserId)
                        .ThenBy(t => t.Name)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Template>> GetPublicAsync(CancellationToken ct = default)
            => await _db.Templates
                        .AsNoTracking()
                        .Where(t => t.IsPublic)
                        .OrderBy(t => t.Name)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Template>> GetByUserAsync(int userId, CancellationToken ct = default)
            => await _db.Templates
                        .AsNoTracking()
                        .Where(t => t.UserId == userId)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<Template?> GetByIdAsync(int id, CancellationToken ct = default)
            => await _db.Templates
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Id == id, ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<Template?> GetByNameAsync(string name, CancellationToken ct = default)
            => await _db.Templates
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => EF.Functions.Like(t.Name, name), ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<Template> CreateAsync(Template entity, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(entity);
            _db.Templates.Add(entity);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return entity;
        }

        /// <inheritdoc/>
        public async Task UpdateAsync(Template entity, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(entity);
            _db.Templates.Update(entity);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            Template? template = await _db.Templates.FindAsync([id], ct).ConfigureAwait(false);
            if (template is not null)
            {
                _db.Templates.Remove(template);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Template>> GetAssignedToDevicesAsync(CancellationToken ct = default)
            => await _db.Templates
                        .AsNoTracking()
                        .Where(t => _db.Devices.Any(d => d.ActiveTemplateId == t.Id))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
    }
}
