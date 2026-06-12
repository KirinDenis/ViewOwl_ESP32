using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IGrabLogRepository"/>.
    /// </summary>
    public sealed class GrabLogRepository : IGrabLogRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public GrabLogRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<GrabLog> CreateAsync(
            int templateId, Guid tokenGuid,
            int width = 480, int height = 320,
            CancellationToken ct = default)
        {
            var log = new GrabLog
            {
                TemplateId  = templateId,
                TokenGuid   = tokenGuid,
                RequestedAt = DateTime.UtcNow,
                Width       = width,
                Height      = height,
                Message     = "Pending"
            };
            _db.GrabLogs.Add(log);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return log;
        }

        /// <inheritdoc/>
        public async Task MarkCompletedAsync(
            int id, bool success,
            string? message = null,
            CancellationToken ct = default)
        {
            GrabLog? log = await _db.GrabLogs.FindAsync([id], ct).ConfigureAwait(false);
            if (log is null) return;

            log.CompletedAt = DateTime.UtcNow;
            log.Success     = success;
            log.Message     = message ?? (success ? "OK" : "Unknown error");
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<GrabLog>> GetRecentAsync(
            int templateId, int count = 20,
            CancellationToken ct = default)
            => await _db.GrabLogs
                        .AsNoTracking()
                        .Where(g => g.TemplateId == templateId)
                        .OrderByDescending(g => g.RequestedAt)
                        .Take(count)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<GrabLog?> GetLastSuccessfulAsync(
            int templateId,
            CancellationToken ct = default)
            => await _db.GrabLogs
                        .AsNoTracking()
                        .Where(g => g.TemplateId == templateId && g.Success == true)
                        .OrderByDescending(g => g.CompletedAt)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<GrabLog?> GetLastSuccessfulAsync(
            int templateId,
            int width,
            int height,
            CancellationToken ct = default)
        {
            // Try an exact dimension match first so devices always receive a frame
            // that was rendered at their native resolution.
            GrabLog? exact = await _db.GrabLogs
                .AsNoTracking()
                .Where(g => g.TemplateId == templateId
                         && g.Success == true
                         && g.Width    == width
                         && g.Height   == height)
                .OrderByDescending(g => g.CompletedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            // Fall back to any successful log so the device is never left without a frame
            // during the first grab cycle at the new resolution.
            return exact ?? await GetLastSuccessfulAsync(templateId, ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<int> GetSuccessfulCountAsync(int templateId, CancellationToken ct = default)
            => await _db.GrabLogs
                        .AsNoTracking()
                        .CountAsync(g => g.TemplateId == templateId && g.Success == true, ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<(int Total, int Successful)> GetTodayStatsAsync(
            int templateId, CancellationToken ct = default)
        {
            DateTime startOfDay = DateTime.UtcNow.Date;
            var todayLogs = await _db.GrabLogs
                                     .AsNoTracking()
                                     .Where(g => g.TemplateId == templateId
                                              && g.RequestedAt >= startOfDay)
                                     .Select(g => g.Success)
                                     .ToListAsync(ct)
                                     .ConfigureAwait(false);

            int total      = todayLogs.Count;
            int successful = todayLogs.Count(s => s == true);
            return (total, successful);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<GrabLog>> GetStaleSuccessfulAsync(
            int templateId, int width, int height,
            int keepCount,
            CancellationToken ct = default)
            => await _db.GrabLogs
                        .AsNoTracking()
                        .Where(g => g.TemplateId == templateId
                                 && g.Success    == true
                                 && g.Width      == width
                                 && g.Height     == height)
                        .OrderByDescending(g => g.CompletedAt)
                        .Skip(keepCount)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            GrabLog? log = await _db.GrabLogs.FindAsync([id], ct).ConfigureAwait(false);
            if (log is null) return;
            _db.GrabLogs.Remove(log);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
