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
        private readonly ILogger<DashboardController> _logger;

        /// <summary>
        /// Initialises the controller with its repositories.
        /// </summary>
        public DashboardController(
            IDeviceRepository deviceRepo,
            IConnectionRepository connectionRepo,
            ITemplateRepository templateRepo,
            ILogger<DashboardController> logger)
        {
            ArgumentNullException.ThrowIfNull(deviceRepo);
            ArgumentNullException.ThrowIfNull(connectionRepo);
            ArgumentNullException.ThrowIfNull(templateRepo);
            ArgumentNullException.ThrowIfNull(logger);

            _deviceRepo     = deviceRepo;
            _connectionRepo = connectionRepo;
            _templateRepo   = templateRepo;
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

            var state = new DashboardStateDto(
                Devices:     devices.Select(d => DeviceNodeDto.From(
                                 d,
                                 d.ActiveTemplateId.HasValue
                                     ? templateNameById.GetValueOrDefault(d.ActiveTemplateId.Value)
                                     : null)).ToList(),
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
    }

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
        int? WifiRssi)
    {
        /// <summary>Maps a <see cref="Device"/> entity to this DTO.</summary>
        /// <param name="d">The device entity to map.</param>
        /// <param name="activeTemplateName">Optional resolved name of the active template.</param>
        public static DeviceNodeDto From(Device d, string? activeTemplateName = null)
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
                d.WifiRssi is { } rssi && rssi < 0 ? rssi : null);
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
