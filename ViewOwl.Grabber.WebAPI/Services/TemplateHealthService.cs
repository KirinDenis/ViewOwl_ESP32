using Microsoft.EntityFrameworkCore;
using ViewOwl.Data;
using ViewOwl.Grabber.WebAPI.DTOs;

namespace ViewOwl.Grabber.WebAPI.Services
{
    /// <summary>
    /// EF Core implementation of <see cref="ITemplateHealthService"/>.
    /// Computes template health in a single correlated subquery — no N+1.
    /// </summary>
    public sealed class TemplateHealthService : ITemplateHealthService
    {
        /// <summary>
        /// A grab older than this threshold is considered <c>overdue</c>.
        /// 2× the 5-minute grabber cycle gives one missed cycle of slack before alerting.
        /// </summary>
        private static readonly TimeSpan OverdueThreshold = TimeSpan.FromMinutes(10);

        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the service with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public TemplateHealthService(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<TemplateHealthDto>> GetAllAsync(
            int userId, CancellationToken ct = default)
        {
            DateTime overdueThreshold = DateTime.UtcNow - OverdueThreshold;

            // Single correlated-subquery pass: for every template owned by the user,
            // find the latest grab log entry and count active device assignments.
            // EF Core translates the inner Select + FirstOrDefault into a LEFT JOIN LATERAL.
            var rows = await _db.Templates
                .AsNoTracking()
                .Where(t => t.UserId == userId)
                .Select(t => new
                {
                    t.Id,
                    t.Name,
                    LastGrab = _db.GrabLogs
                        .Where(g => g.TemplateId == t.Id)
                        .OrderByDescending(g => g.RequestedAt)
                        .Select(g => new { g.RequestedAt, g.Success, g.Message })
                        .FirstOrDefault(),
                    AssignedCount = _db.Devices
                        .Count(d => d.ActiveTemplateId == t.Id),
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return rows
                .Select(r =>
                {
                    string health;

                    if (r.AssignedCount == 0)
                    {
                        // Template exists but no device is showing it — alert at P4/Info level.
                        health = "unassigned";
                    }
                    else if (r.LastGrab is null)
                    {
                        // Template was never grabbed — operator needs to trigger first grab.
                        health = "never";
                    }
                    else if (r.LastGrab.Success == false)
                    {
                        // Last grab attempt failed — screenshotter or converter error.
                        health = "failed";
                    }
                    else if (r.LastGrab.RequestedAt < overdueThreshold)
                    {
                        // Successful grab exists but it's stale — grabber may have stalled.
                        health = "overdue";
                    }
                    else
                    {
                        health = "healthy";
                    }

                    return new TemplateHealthDto(
                        Id:                 r.Id,
                        Name:               r.Name,
                        Health:             health,
                        LastGrabAt:         r.LastGrab?.RequestedAt,
                        LastGrabOk:         r.LastGrab?.Success,
                        LastErrorMessage:   r.LastGrab?.Success == false ? r.LastGrab.Message : null,
                        AssignedDeviceCount: r.AssignedCount);
                })
                .ToList();
        }
    }
}
