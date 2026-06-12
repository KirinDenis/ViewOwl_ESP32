using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IPipelineEventRepository"/>.
    /// </summary>
    public sealed class PipelineEventRepository : IPipelineEventRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public PipelineEventRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task AddAsync(PipelineEvent ev, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(ev);
            _db.PipelineEvents.Add(ev);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<PipelineEvent>> GetRecentAsync(
            int?      deviceId   = null,
            int?      templateId = null,
            string?   eventType  = null,
            DateTime? since      = null,
            int       limit      = 200,
            CancellationToken ct = default)
        {
            IQueryable<PipelineEvent> q = _db.PipelineEvents.AsNoTracking();

            if (deviceId.HasValue)
                q = q.Where(e => e.DeviceId == deviceId.Value);

            if (templateId.HasValue)
                q = q.Where(e => e.TemplateId == templateId.Value);

            if (eventType is not null)
                q = q.Where(e => e.EventType == eventType);

            if (since.HasValue)
                q = q.Where(e => e.Ts > since.Value);

            return await q
                .OrderByDescending(e => e.Ts)
                .Take(limit)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<PipelineEvent>> GetForEntitiesAsync(
            IReadOnlyList<int> templateIds,
            IReadOnlyList<int> deviceIds,
            int limit = 500,
            CancellationToken ct = default)
        {
            // EF Core requires List<T> (not IReadOnlyList) for IN-clause translation.
            // Guard against empty collections — SQLite rejects empty IN ().
            var tIds = templateIds.Count > 0 ? templateIds.ToList() : null;
            var dIds = deviceIds.Count   > 0 ? deviceIds.ToList()   : null;

            if (tIds is null && dIds is null)
                return [];

            IQueryable<PipelineEvent> q = _db.PipelineEvents.AsNoTracking();

            if (tIds is not null && dIds is not null)
                q = q.Where(e => (e.TemplateId.HasValue && tIds.Contains(e.TemplateId.Value))
                               || (e.DeviceId.HasValue   && dIds.Contains(e.DeviceId.Value)));
            else if (tIds is not null)
                q = q.Where(e => e.TemplateId.HasValue && tIds.Contains(e.TemplateId.Value));
            else
                q = q.Where(e => e.DeviceId.HasValue && dIds!.Contains(e.DeviceId.Value));

            return await q
                .OrderByDescending(e => e.Ts)
                .Take(limit)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
    }
}
