using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ViewOwl.Security.Models;

namespace ViewOwl.Security.Services
{
    /// <summary>
    /// EF Core–backed implementation of <see cref="ISessionFingerprintService"/>.
    /// </summary>
    public sealed class SessionFingerprintService : ISessionFingerprintService
    {
        private readonly SecurityDbContext _db;
        private readonly ISecurityEventService _events;

        /// <summary>
        /// Initialises the service with the security database context and event logger.
        /// </summary>
        /// <param name="db">Scoped security database context.</param>
        /// <param name="events">Security event logger for suspicious-activity records.</param>
        public SessionFingerprintService(SecurityDbContext db, ISecurityEventService events)
        {
            _db     = db;
            _events = events;
        }

        /// <inheritdoc />
        public async Task RecordAsync(string userId, string sessionId, HttpContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);

            string ip      = ExtractIp(ctx);
            string ua      = ctx.Request.Headers["User-Agent"].ToString();
            string uaHash  = HashSha256(ua);
            DateTime now   = DateTime.UtcNow;

            SessionFingerprint? existing = await _db.SessionFingerprints
                .FirstOrDefaultAsync(f => f.UserId == userId && f.SessionId == sessionId)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                // Refresh the timestamp on an already-known session.
                existing.LastSeenAt = now;
            }
            else
            {
                _db.SessionFingerprints.Add(new SessionFingerprint
                {
                    UserId        = userId,
                    SessionId     = sessionId,
                    IpAddress     = ip,
                    UserAgent     = ua,
                    UserAgentHash = uaHash,
                    CreatedAt     = now,
                    LastSeenAt    = now,
                    IsActive      = true
                });
            }

            await _db.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> ValidateAsync(string userId, string sessionId, HttpContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);

            SessionFingerprint? fp = await _db.SessionFingerprints
                .FirstOrDefaultAsync(f =>
                    f.UserId    == userId    &&
                    f.SessionId == sessionId &&
                    f.IsActive)
                .ConfigureAwait(false);

            if (fp is null)
                return false;

            string currentIp     = ExtractIp(ctx);
            string currentUaHash = HashSha256(ctx.Request.Headers["User-Agent"].ToString());

            bool ipChanged = fp.IpAddress     != currentIp;
            bool uaChanged = fp.UserAgentHash != currentUaHash;

            if (ipChanged && uaChanged)
            {
                // Both changed simultaneously — high-confidence session hijacking indicator.
                await _events.LogAsync(ctx, SecurityEvent.EventSuspiciousActivity,
                    "Session fingerprint mismatch: IP and UA changed simultaneously")
                    .ConfigureAwait(false);
                return false;
            }

            // IP-only change is normal (mobile roaming, DHCP, VPN).  Update silently.
            if (ipChanged)
                fp.IpAddress = currentIp;

            fp.LastSeenAt = DateTime.UtcNow;
            await _db.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }

        /// <inheritdoc />
        public Task<List<SessionFingerprint>> GetActiveSessionsAsync(string userId)
        {
            return _db.SessionFingerprints
                      .Where(f => f.UserId == userId && f.IsActive)
                      .OrderByDescending(f => f.LastSeenAt)
                      .ToListAsync();
        }

        /// <inheritdoc />
        public async Task RevokeAsync(string userId, string sessionId)
        {
            SessionFingerprint? fp = await _db.SessionFingerprints
                .FirstOrDefaultAsync(f =>
                    f.UserId    == userId &&
                    f.SessionId == sessionId &&
                    f.IsActive)
                .ConfigureAwait(false);

            if (fp is null)
                return;

            fp.IsActive   = false;
            fp.RevokedAt  = DateTime.UtcNow;
            await _db.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task RevokeAllAsync(string userId, string exceptSessionId)
        {
            List<SessionFingerprint> others = await _db.SessionFingerprints
                .Where(f =>
                    f.UserId    == userId &&
                    f.SessionId != exceptSessionId &&
                    f.IsActive)
                .ToListAsync()
                .ConfigureAwait(false);

            DateTime now = DateTime.UtcNow;
            foreach (SessionFingerprint fp in others)
            {
                fp.IsActive  = false;
                fp.RevokedAt = now;
            }

            await _db.SaveChangesAsync().ConfigureAwait(false);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Extracts the real client IP, honouring reverse-proxy forwarding headers.
        /// </summary>
        private static string ExtractIp(HttpContext ctx)
        {
            string? forwarded = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                string first = forwarded.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(first))
                    return first;
            }

            string? realIp = ctx.Request.Headers["X-Real-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(realIp))
                return realIp.Trim();

            return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        /// <summary>
        /// Returns the lowercase hex SHA-256 digest of the input string (UTF-8 encoded).
        /// </summary>
        private static string HashSha256(string input)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            // ToUpperInvariant satisfies CA1308; hex digests are case-insensitive.
            return Convert.ToHexString(bytes).ToUpperInvariant();
        }
    }
}
