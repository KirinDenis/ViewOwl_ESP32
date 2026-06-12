using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ViewOwl.Security;
using ViewOwl.Security.Models;
using ViewOwl.Security.Services;

namespace ViewOwl.Grabber.WebAPI.Controllers.Admin
{
    /// <summary>
    /// REST API backing the admin security dashboard.
    /// All endpoints are protected by the X-Admin-Key header, whose value must match
    /// the "Security:AdminApiKey" configuration setting.
    /// </summary>
    [ApiController]
    [Route("admin/security")]
    public sealed class SecurityDashboardController : ControllerBase
    {
        private readonly ISecurityEventService _events;
        private readonly IIpBlockService _blocks;
        private readonly SecurityDbContext _db;
        private readonly IConfiguration _config;

        /// <summary>
        /// Initialises the controller with its dependencies.
        /// </summary>
        public SecurityDashboardController(
            ISecurityEventService events,
            IIpBlockService blocks,
            SecurityDbContext db,
            IConfiguration config)
        {
            _events = events;
            _blocks = blocks;
            _db     = db;
            _config = config;
        }

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a paginated list of security events, newest first.
        /// Optional filters: eventType and ip.
        /// </summary>
        [HttpGet("events")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetEvents(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? eventType = null,
            [FromQuery] string? ip = null)
        {
            if (!IsAuthorised())
                return Unauthorized();

            IQueryable<SecurityEvent> q = _db.SecurityEvents.OrderByDescending(e => e.CreatedAt);

            if (!string.IsNullOrWhiteSpace(eventType))
                q = q.Where(e => e.EventType == eventType);

            if (!string.IsNullOrWhiteSpace(ip))
                q = q.Where(e => e.IpAddress == ip);

            int total = await q.CountAsync().ConfigureAwait(false);
            List<SecurityEvent> items = await q
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync()
                .ConfigureAwait(false);

            return Ok(new { total, page, pageSize, items });
        }

        // ── Blocks ────────────────────────────────────────────────────────────

        /// <summary>Returns all currently active IP blocks.</summary>
        [HttpGet("blocks")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetBlocks()
        {
            if (!IsAuthorised())
                return Unauthorized();

            List<BlockedIp> blocks = await _blocks.GetActiveBlocksAsync().ConfigureAwait(false);
            return Ok(blocks);
        }

        /// <summary>Manually blocks an IP address.</summary>
        /// <param name="ip">IPv4 or IPv6 address to block.</param>
        /// <param name="request">Block parameters (reason, duration in hours; 0 = permanent).</param>
        [HttpPost("blocks/{ip}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> BlockIp(
            string ip,
            [FromBody] ManualBlockRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!IsAuthorised())
                return Unauthorized();

            TimeSpan? duration = request.Hours > 0
                ? TimeSpan.FromHours(request.Hours)
                : null;

            await _blocks.BlockAsync(ip, request.Reason, duration, isManual: true)
                .ConfigureAwait(false);

            return Ok(new { blocked = ip, reason = request.Reason, hours = request.Hours });
        }

        /// <summary>Lifts an active block on an IP address.</summary>
        /// <param name="ip">The IP address to unblock.</param>
        [HttpDelete("blocks/{ip}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> UnblockIp(string ip)
        {
            if (!IsAuthorised())
                return Unauthorized();

            await _blocks.UnblockAsync(ip).ConfigureAwait(false);
            return Ok(new { unblocked = ip });
        }

        // ── Stats ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns aggregated security statistics for the last 24 hours plus all-time active blocks.
        /// </summary>
        [HttpGet("stats")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetStats()
        {
            if (!IsAuthorised())
                return Unauthorized();

            DateTime now        = DateTime.UtcNow;
            DateTime startOfDay = now.Date;
            DateTime yesterday  = now.AddHours(-24);

            int totalToday = await _db.SecurityEvents
                .CountAsync(e => e.CreatedAt >= startOfDay)
                .ConfigureAwait(false);

            // Events grouped by type in the last 24 hours.
            Dictionary<string, int> byType = await _db.SecurityEvents
                .Where(e => e.CreatedAt >= yesterday)
                .GroupBy(e => e.EventType)
                .Select(g => new { Type = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Type, x => x.Count)
                .ConfigureAwait(false);

            // Top 10 IPs by event volume in the last 24 hours.
            List<object> topIps = await _db.SecurityEvents
                .Where(e => e.CreatedAt >= yesterday)
                .GroupBy(e => e.IpAddress)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => (object)new { ip = g.Key, count = g.Count() })
                .ToListAsync()
                .ConfigureAwait(false);

            int activeBlocks = await _db.BlockedIps
                .CountAsync(b =>
                    b.UnblockedAt == null &&
                    (b.BlockedUntil == null || b.BlockedUntil > now))
                .ConfigureAwait(false);

            // Events per hour for the last 24 hours (index 0 = 24 h ago, index 23 = current hour).
            // EF.Functions.DateDiffHour is SQL Server-specific; for SQLite we load timestamps
            // into memory and compute the offset in C# using integer division on ticks.
            int[] perHour = new int[24];
            List<DateTime> recentTimestamps = await _db.SecurityEvents
                .Where(e => e.CreatedAt >= yesterday)
                .Select(e => e.CreatedAt)
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (DateTime ts in recentTimestamps)
            {
                int offset = (int)(ts - yesterday).TotalHours;
                if (offset is >= 0 and < 24)
                    perHour[offset]++;
            }

            return Ok(new
            {
                totalToday,
                byType,
                topIps,
                activeBlocks,
                eventsPerHour = perHour
            });
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Validates the X-Admin-Key request header against the configured secret.
        /// </summary>
        private bool IsAuthorised()
        {
            string? configuredKey = _config["Security:AdminApiKey"];
            if (string.IsNullOrWhiteSpace(configuredKey) ||
                configuredKey == "CHANGE_THIS_BEFORE_DEPLOY")
            {
                // Deny access when the key has not been changed from the default.
                return false;
            }

            string? headerKey = Request.Headers["X-Admin-Key"].FirstOrDefault();
            return !string.IsNullOrEmpty(headerKey) &&
                   string.Equals(headerKey, configuredKey, StringComparison.Ordinal);
        }
    }

    // ── Request DTOs ──────────────────────────────────────────────────────────

    /// <summary>Body for the manual IP block endpoint.</summary>
    public sealed record ManualBlockRequest(string Reason, int Hours);
}
