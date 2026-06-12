using Microsoft.EntityFrameworkCore;
using ViewOwl.Security.Models;

namespace ViewOwl.Security.Services
{
    /// <summary>
    /// EF Core–backed implementation of <see cref="IIpBlockService"/>.
    /// </summary>
    public sealed class IpBlockService : IIpBlockService
    {
        private readonly SecurityDbContext _db;

        /// <summary>
        /// Initialises the service with the security database context.
        /// </summary>
        /// <param name="db">Scoped security database context.</param>
        public IpBlockService(SecurityDbContext db)
        {
            _db = db;
        }

        /// <inheritdoc />
        public Task<bool> IsBlockedAsync(string ip)
        {
            DateTime now = DateTime.UtcNow;
            return _db.BlockedIps.AnyAsync(b =>
                b.IpAddress == ip &&
                b.UnblockedAt == null &&
                (b.BlockedUntil == null || b.BlockedUntil > now));
        }

        /// <inheritdoc />
        public async Task BlockAsync(
            string ip,
            string reason,
            TimeSpan? duration = null,
            bool isManual = false)
        {
            // Refresh an existing active block rather than inserting a duplicate row.
            BlockedIp? existing = await _db.BlockedIps
                .FirstOrDefaultAsync(b => b.IpAddress == ip && b.UnblockedAt == null)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                existing.Reason       = reason;
                existing.BlockedAt    = DateTime.UtcNow;
                existing.BlockedUntil = duration.HasValue ? DateTime.UtcNow.Add(duration.Value) : null;
                existing.IsManual     = isManual;
            }
            else
            {
                _db.BlockedIps.Add(new BlockedIp
                {
                    IpAddress    = ip,
                    Reason       = reason,
                    BlockedAt    = DateTime.UtcNow,
                    BlockedUntil = duration.HasValue ? DateTime.UtcNow.Add(duration.Value) : null,
                    IsManual     = isManual
                });
            }

            await _db.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task UnblockAsync(string ip)
        {
            DateTime now = DateTime.UtcNow;

            List<BlockedIp> active = await _db.BlockedIps
                .Where(b => b.IpAddress == ip && b.UnblockedAt == null)
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (BlockedIp block in active)
            {
                block.UnblockedAt = now;
                // Move expiry to the past so IsBlockedAsync returns false immediately.
                block.BlockedUntil = now.AddSeconds(-1);
            }

            await _db.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<List<BlockedIp>> GetActiveBlocksAsync()
        {
            DateTime now = DateTime.UtcNow;
            return _db.BlockedIps
                      .Where(b =>
                          b.UnblockedAt == null &&
                          (b.BlockedUntil == null || b.BlockedUntil > now))
                      .OrderByDescending(b => b.BlockedAt)
                      .ToListAsync();
        }

        /// <inheritdoc />
        public async Task AutoBlockIfNeededAsync(string ip, ISecurityEventService eventService)
        {
            // Skip if already blocked — no point re-evaluating.
            if (await IsBlockedAsync(ip).ConfigureAwait(false))
                return;

            // Threshold 1: high volume of *hostile* events — 50+ scanner/brute-force/suspicious
            // events in last 10 minutes.  RateLimit and InvalidToken are excluded intentionally:
            // they fire during normal dashboard usage (token refresh races, burst page loads) and
            // must not trigger an auto-block for legitimate users.
            int recentHostile =
                await eventService.CountRecentByIpAsync(ip, SecurityEvent.EventScannerProbe,   minutes: 10).ConfigureAwait(false)
              + await eventService.CountRecentByIpAsync(ip, SecurityEvent.EventBruteForce,      minutes: 10).ConfigureAwait(false)
              + await eventService.CountRecentByIpAsync(ip, SecurityEvent.EventSuspiciousActivity, minutes: 10).ConfigureAwait(false);

            if (recentHostile >= 50)
            {
                await BlockAsync(ip, "AutoBlock:HighVolume", TimeSpan.FromHours(24))
                    .ConfigureAwait(false);
                return;
            }

            // Threshold 2: brute-force login attempts — 5+ BruteForce events in last 5 minutes.
            int bruteForce = await eventService
                .CountRecentByIpAsync(ip, SecurityEvent.EventBruteForce, minutes: 5)
                .ConfigureAwait(false);

            if (bruteForce >= 5)
            {
                await BlockAsync(ip, "AutoBlock:BruteForce", TimeSpan.FromDays(7))
                    .ConfigureAwait(false);
                return;
            }

            // Threshold 3: scanner — 3+ ScannerProbe events ever (they indicate active tool use).
            int scannerTotal = await eventService
                .CountTotalByIpAsync(ip, SecurityEvent.EventScannerProbe)
                .ConfigureAwait(false);

            if (scannerTotal >= 3)
            {
                await BlockAsync(ip, "AutoBlock:Scanner", TimeSpan.FromDays(30))
                    .ConfigureAwait(false);
            }
        }
    }
}
