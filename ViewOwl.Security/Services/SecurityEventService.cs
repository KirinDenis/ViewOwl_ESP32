using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ViewOwl.Security.Models;

namespace ViewOwl.Security.Services
{
    /// <summary>
    /// EF Core–backed implementation of <see cref="ISecurityEventService"/>.
    /// </summary>
    public sealed class SecurityEventService : ISecurityEventService
    {
        private readonly SecurityDbContext _db;

        /// <summary>
        /// Initialises the service with the security database context.
        /// </summary>
        /// <param name="db">Scoped security database context.</param>
        public SecurityEventService(SecurityDbContext db)
        {
            _db = db;
        }

        /// <inheritdoc />
        public async Task LogAsync(HttpContext ctx, string eventType, string? details = null)
        {
            ArgumentNullException.ThrowIfNull(ctx);

            string ip = ExtractIp(ctx);

            // Truncate request body to 500 characters to avoid storing large payloads.
            string? body = null;
            if (ctx.Request.Method is "POST" or "PUT" or "PATCH" &&
                ctx.Request.ContentLength is > 0)
            {
                // Body may have already been read by other middleware; only read if buffered.
                if (ctx.Request.Body.CanSeek)
                {
                    ctx.Request.Body.Position = 0;
                    using var reader = new System.IO.StreamReader(
                        ctx.Request.Body,
                        leaveOpen: true);
                    string raw = await reader.ReadToEndAsync().ConfigureAwait(false);
                    ctx.Request.Body.Position = 0;
                    body = raw.Length > 500 ? raw[..500] : raw;
                }
            }

            // Extract userId and sessionId from JWT claims if the user is authenticated.
            string? userId    = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                             ?? ctx.User.FindFirst("sub")?.Value;
            string? sessionId = ctx.User.FindFirst("jti")?.Value;

            var ev = new SecurityEvent
            {
                IpAddress   = ip,
                UserAgent   = ctx.Request.Headers["User-Agent"].ToString(),
                Endpoint    = ctx.Request.Path + ctx.Request.QueryString,
                Method      = ctx.Request.Method,
                EventType   = eventType,
                StatusCode  = ctx.Response.StatusCode,
                RequestBody = body,
                CreatedAt   = DateTime.UtcNow,
                UserId      = userId,
                SessionId   = sessionId,
                Details     = details
            };

            _db.SecurityEvents.Add(ev);
            await _db.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<List<SecurityEvent>> GetRecentAsync(int count = 100)
        {
            return _db.SecurityEvents
                      .OrderByDescending(e => e.CreatedAt)
                      .Take(count)
                      .ToListAsync();
        }

        /// <inheritdoc />
        public Task<List<SecurityEvent>> GetByIpAsync(string ip, int hours = 24)
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-hours);
            return _db.SecurityEvents
                      .Where(e => e.IpAddress == ip && e.CreatedAt >= cutoff)
                      .OrderByDescending(e => e.CreatedAt)
                      .ToListAsync();
        }

        /// <inheritdoc />
        public Task<int> CountRecentByIpAsync(string ip, string? eventType = null, int minutes = 10)
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-minutes);
            IQueryable<SecurityEvent> q = _db.SecurityEvents
                .Where(e => e.IpAddress == ip && e.CreatedAt >= cutoff);

            if (eventType is not null)
                q = q.Where(e => e.EventType == eventType);

            return q.CountAsync();
        }

        /// <inheritdoc />
        public Task<int> CountTotalByIpAsync(string ip, string? eventType = null)
        {
            IQueryable<SecurityEvent> q = _db.SecurityEvents.Where(e => e.IpAddress == ip);

            if (eventType is not null)
                q = q.Where(e => e.EventType == eventType);

            return q.CountAsync();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the real client IP from proxy headers, falling back to the TCP remote address.
        /// X-Forwarded-For may contain a comma-separated chain; the first entry is the original client.
        /// </summary>
        private static string ExtractIp(HttpContext ctx)
        {
            string? forwarded = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                // Take the first (leftmost) address — that is the original client.
                string first = forwarded.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(first))
                    return first;
            }

            string? realIp = ctx.Request.Headers["X-Real-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(realIp))
                return realIp.Trim();

            return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
    }
}
