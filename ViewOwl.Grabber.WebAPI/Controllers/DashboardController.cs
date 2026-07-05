using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Security.Claims;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// API endpoints for dashboard data: devices, templates, and connections.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public sealed class DashboardController : ControllerBase
    {
        private readonly IDeviceRepository _deviceRepo;
        private readonly IConnectionRepository _connectionRepo;
        private readonly ITemplateRepository _templateRepo;
        private readonly IPipelineEventRepository _pipelineRepo;
        private readonly ILogger<DashboardController> _logger;

        // Grabber-lane event types, newest of which drives the per-device grab-health seed.
        private static readonly string[] GrabOutcomeTypes =
        [
            PipelineEventTypes.GrabCompleted,
            PipelineEventTypes.GrabFailed,
        ];

        /// <summary>
        /// Initialises the controller with its repositories.
        /// </summary>
        public DashboardController(
            IDeviceRepository deviceRepo,
            IConnectionRepository connectionRepo,
            ITemplateRepository templateRepo,
            IPipelineEventRepository pipelineRepo,
            ILogger<DashboardController> logger)
        {
            ArgumentNullException.ThrowIfNull(deviceRepo);
            ArgumentNullException.ThrowIfNull(connectionRepo);
            ArgumentNullException.ThrowIfNull(templateRepo);
            ArgumentNullException.ThrowIfNull(pipelineRepo);
            ArgumentNullException.ThrowIfNull(logger);

            _deviceRepo     = deviceRepo;
            _connectionRepo = connectionRepo;
            _templateRepo   = templateRepo;
            _pipelineRepo   = pipelineRepo;
            _logger         = logger;
        }

        /// <summary>
        /// Returns the complete dashboard state for the authenticated user:
        /// all devices, templates, and device-to-template connections.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("state")]
        [ProducesResponseType(typeof(DashboardStateDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetDashboardState(CancellationToken ct = default)
        {
            int userId = GetCurrentUserId();

            var devices     = await _deviceRepo.GetByUserAsync(userId, ct).ConfigureAwait(false);
            var templates   = await _templateRepo.GetByUserAsync(userId, ct).ConfigureAwait(false);
            var connections = await _connectionRepo.GetByUserAsync(userId, ct).ConfigureAwait(false);

            // Build a name-lookup dictionary so each device mapping is O(1)
            var templateNameById = templates.ToDictionary(t => t.Id, t => t.Name);

            // Seed the grabber-lane health for every active template so a device whose
            // content is frozen by a failing grab reads GRAB FAIL immediately on load,
            // instead of a misleading RENDERING (the UDP lane keeps returning not_modified
            // on the stale frame). The refresh worker cycles every 5 minutes, so the latest
            // grab_completed/grab_failed per template is always recent — a bounded query
            // ordered newest-first reliably contains it.
            var latestGrabByTemplate = await LoadLatestGrabStatusAsync(devices, ct).ConfigureAwait(false);

            var state = new DashboardStateDto(
                Devices:     devices.Select(d =>
                             {
                                 GrabStatus? grab =
                                     d.ActiveTemplateId.HasValue
                                         ? latestGrabByTemplate.GetValueOrDefault(d.ActiveTemplateId.Value)
                                         : null;
                                 return DeviceNodeDto.From(
                                     d,
                                     d.ActiveTemplateId.HasValue
                                         ? templateNameById.GetValueOrDefault(d.ActiveTemplateId.Value)
                                         : null,
                                     grab?.Success,
                                     grab?.Error,
                                     grab?.Ts);
                             }).ToList(),
                Templates:   templates.Select(TemplateNodeDto.From).ToList(),
                Connections: connections.Select(ConnectionDto.From).ToList());

            return Ok(state);
        }

        private int GetCurrentUserId()
        {
            string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");
            return int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                ? id
                : 0;
        }

        /// <summary>
        /// Returns the most recent grab outcome (success + timestamp + error) for each active
        /// template referenced by <paramref name="devices"/>, in a single query. Devices with no
        /// active template, and templates with no grab history, are simply absent from the map.
        /// </summary>
        private async Task<Dictionary<int, GrabStatus>> LoadLatestGrabStatusAsync(
            IReadOnlyList<Device> devices, CancellationToken ct)
        {
            int[] activeTemplateIds = devices
                .Where(d => d.ActiveTemplateId.HasValue)
                .Select(d => d.ActiveTemplateId!.Value)
                .Distinct()
                .ToArray();

            var result = new Dictionary<int, GrabStatus>();
            if (activeTemplateIds.Length == 0)
                return result;

            IReadOnlyList<PipelineEvent> events = await _pipelineRepo
                .GetForEntitiesAsync(activeTemplateIds, Array.Empty<int>(), limit: 1000, ct)
                .ConfigureAwait(false);

            // Events arrive newest-first: the first grab_completed/grab_failed seen per
            // template is its latest outcome.
            foreach (PipelineEvent ev in events)
            {
                if (!GrabOutcomeTypes.Contains(ev.EventType) || ev.TemplateId is not { } tid)
                    continue;
                if (result.ContainsKey(tid))
                    continue;
                result[tid] = new GrabStatus(
                    ev.EventType == PipelineEventTypes.GrabCompleted,
                    ev.Ts,
                    ev.ErrorMessage);
            }

            return result;
        }
    }

    /// <summary>Latest grabber-lane outcome for a template.</summary>
    /// <param name="Success">Whether the most recent grab succeeded.</param>
    /// <param name="Ts">Timestamp of that grab (UTC).</param>
    /// <param name="Error">Failure message when unsuccessful; otherwise <c>null</c>.</param>
    internal sealed record GrabStatus(bool Success, DateTime Ts, string? Error);

    // ── Response DTOs ─────────────────────────────────────────────────────────

    /// <summary>Snapshot of all dashboard data for the current user.</summary>
    public sealed record DashboardStateDto(
        IReadOnlyList<DeviceNodeDto> Devices,
        IReadOnlyList<TemplateNodeDto> Templates,
        IReadOnlyList<ConnectionDto> Connections);

    /// <summary>Device summary for the dashboard device list.</summary>
    public sealed record DeviceNodeDto(
        int Id,
        string Name,
        string Token,
        string? DisplayType,
        int? DisplayWidth,
        int? DisplayHeight,
        string? FirmwareVersion,
        string Status,
        DateTime? LastSeenAt,
        string? IpAddress,
        int? ActiveTemplateId,
        string? ActiveTemplateName,
        int? WifiRssi,
        bool? GrabOk,
        string? GrabError,
        DateTime? GrabAt)
    {
        /// <summary>Maps a <see cref="Device"/> entity to this DTO.</summary>
        /// <param name="d">The device entity to map.</param>
        /// <param name="activeTemplateName">Optional resolved name of the active template.</param>
        /// <param name="grabOk">Latest grab outcome for the active template (null = no grab history).</param>
        /// <param name="grabError">Latest grab failure message for the active template, if any.</param>
        /// <param name="grabAt">Timestamp of the latest grab for the active template (UTC).</param>
        public static DeviceNodeDto From(
            Device d,
            string? activeTemplateName = null,
            bool? grabOk = null,
            string? grabError = null,
            DateTime? grabAt = null)
        {
            ArgumentNullException.ThrowIfNull(d);
            return new(
                d.Id,
                d.Name,
                d.Token,
                d.DisplayType,
                d.DisplayWidth,
                d.DisplayHeight,
                d.FirmwareVersion,
                d.Status.ToString(),
                d.LastSeenAt,
                d.IpAddress,
                d.ActiveTemplateId,
                activeTemplateName,
                // RSSI is always negative — treat stored 0 (from failed esp_wifi_sta_get_ap_info) as no data.
                d.WifiRssi is { } rssi && rssi < 0 ? rssi : null,
                grabOk,
                grabError,
                grabAt);
        }
    }

    /// <summary>Template summary for the dashboard template list.</summary>
    public sealed record TemplateNodeDto(
        int Id,
        string Name,
        DateTime CreatedAt)
    {
        /// <summary>Maps a <see cref="Template"/> entity to this DTO.</summary>
        public static TemplateNodeDto From(Template t)
        {
            ArgumentNullException.ThrowIfNull(t);
            return new(t.Id, t.Name, t.CreatedAt);
        }
    }

    /// <summary>Device-to-template connection for the dashboard.</summary>
    public sealed record ConnectionDto(
        int Id,
        int TemplateId,
        int DeviceId,
        DateTime CreatedAt)
    {
        /// <summary>Maps a <see cref="Connection"/> entity to this DTO.</summary>
        public static ConnectionDto From(Connection c)
        {
            ArgumentNullException.ThrowIfNull(c);
            return new(c.Id, c.TemplateId, c.DeviceId, c.CreatedAt);
        }
    }
}
