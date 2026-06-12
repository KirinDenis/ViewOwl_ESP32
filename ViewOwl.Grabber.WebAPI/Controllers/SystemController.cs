using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Grabber.Config;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Admin-only system status and diagnostics endpoints.
    /// Exposes exchange-folder stats, grab-source list, and recent grab activity.
    /// </summary>
    [ApiController]
    [Route("api/system")]
    [Authorize]
    public sealed class SystemController : ControllerBase
    {
        // Cached options instance — AllowTrailingCommas for GrabbedList.json compatibility.
        private static readonly JsonSerializerOptions GrabbedListJsonOptions =
            new() { AllowTrailingCommas = true };

        private readonly SharedConfig _sharedConfig;
        private readonly GrabberConfig _grabberConfig;
        private readonly ITemplateRepository _templates;
        private readonly IGrabLogRepository _grabLogs;

        /// <summary>Initialises the controller with its dependencies.</summary>
        public SystemController(
            IOptions<SharedConfig> sharedOptions,
            IOptions<GrabberConfig> grabberOptions,
            ITemplateRepository templates,
            IGrabLogRepository grabLogs)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            ArgumentNullException.ThrowIfNull(grabberOptions);
            _sharedConfig  = sharedOptions.Value;
            _grabberConfig = grabberOptions.Value;
            _templates     = templates;
            _grabLogs      = grabLogs;
        }

        /// <summary>
        /// Returns system status: exchange folder stats, auto-grab sources,
        /// and recent grab log entries across all templates visible to this user.
        /// Admins see all templates; regular users see only their own.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("status")]
        [ProducesResponseType(typeof(SystemStatusDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStatus(CancellationToken ct = default)
        {
            string? role   = User.FindFirstValue(ClaimTypes.Role)
                          ?? User.FindFirstValue("http://schemas.microsoft.com/ws/2008/06/identity/claims/role");
            bool isAdmin   = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
            int userId     = GetUserId();

            // ── Exchange folder stats ─────────────────────────────────────────
            string exchange    = _sharedConfig.ExchangeFolder;
            int binCount       = 0;
            long totalBytes    = 0;

            if (Directory.Exists(exchange))
            {
                var bins = Directory.GetFiles(exchange, "*.bin");
                binCount   = bins.Length;
                totalBytes = bins.Sum(f => new FileInfo(f).Length);
            }

            // ── Auto-grab sources from GrabbedList.json ───────────────────────
            var sources     = new List<GrabSourceDto>();
            string listPath = Path.Combine(exchange, _grabberConfig.GrabbedListFileName);
            if (System.IO.File.Exists(listPath))
            {
                try
                {
                    string json    = await System.IO.File.ReadAllTextAsync(listPath, ct).ConfigureAwait(false);
                    // GrabbedList.json sometimes has a trailing comma after the last element.
                    // AllowTrailingCommas prevents a silent deserialization failure that returned
                    // an empty sources list to the dashboard.
                    var rawSources = JsonSerializer.Deserialize<List<JsonElement>>(json, GrabbedListJsonOptions);
                    if (rawSources is not null)
                    {
                        foreach (var el in rawSources)
                        {
                            string src = el.TryGetProperty("Source", out var s) ? s.GetString() ?? "" : "";
                            int w = el.TryGetProperty("Width", out var wEl)  ? wEl.GetInt32() : 480;
                            int h = el.TryGetProperty("Height", out var hEl) ? hEl.GetInt32() : 320;
                            sources.Add(new GrabSourceDto(src, w, h));
                        }
                    }
                }
                catch
                {
                    // Malformed JSON — return empty sources list rather than crashing
                }
            }

            // ── Recent grab logs (user-scoped or admin-all) ───────────────────
            var templates = isAdmin
                ? await _templates.GetAllAsync(ct).ConfigureAwait(false)
                : await _templates.GetByUserAsync(userId, ct).ConfigureAwait(false);

            var recentGrabs = new List<RecentGrabDto>();

            foreach (var tpl in templates)
            {
                var logs = await _grabLogs.GetRecentAsync(tpl.Id, 3, ct).ConfigureAwait(false);
                foreach (var log in logs)
                {
                    recentGrabs.Add(new RecentGrabDto(
                        tpl.Id,
                        tpl.Name,
                        log.RequestedAt,
                        log.CompletedAt,
                        log.Success,
                        log.Message,
                        log.Width,
                        log.Height));
                }
            }

            // Sort newest first
            recentGrabs.Sort((a, b) => b.RequestedAt.CompareTo(a.RequestedAt));

            return Ok(new SystemStatusDto(
                ExchangeFolder  : exchange,
                BinFileCount    : binCount,
                TotalBinBytes   : totalBytes,
                Sources         : sources,
                RecentGrabs     : recentGrabs.Take(20).ToList()));
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private int GetUserId()
        {
            string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            return int.TryParse(sub, out int id) ? id : 0;
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    /// <summary>Top-level system status response.</summary>
    public sealed record SystemStatusDto(
        string ExchangeFolder,
        int BinFileCount,
        long TotalBinBytes,
        IReadOnlyList<GrabSourceDto> Sources,
        IReadOnlyList<RecentGrabDto> RecentGrabs);

    /// <summary>One auto-grab source entry from GrabbedList.json.</summary>
    public sealed record GrabSourceDto(string Source, int Width, int Height);

    /// <summary>Condensed grab log entry with template name for the system view.</summary>
    public sealed record RecentGrabDto(
        int TemplateId,
        string TemplateName,
        DateTime RequestedAt,
        DateTime? CompletedAt,
        bool? Success,
        string Message,
        int Width,
        int Height);
}
