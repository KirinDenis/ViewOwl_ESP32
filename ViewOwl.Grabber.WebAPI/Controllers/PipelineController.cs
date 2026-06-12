using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Grabber.WebAPI.DTOs;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Pipeline observability endpoints.
    /// <list type="bullet">
    ///   <item><c>POST /api/pipeline/event</c> — internal, called by UDP Server to log state transitions.</item>
    ///   <item><c>GET /api/pipeline/events</c> — authenticated, called by the dashboard to query event history.</item>
    /// </list>
    /// </summary>
    [ApiController]
    [Route("api/pipeline")]
    public sealed class PipelineController : ControllerBase
    {
        private readonly IPipelineEventRepository _events;
        private readonly ITemplateRepository _templates;
        private readonly IDeviceRepository _devices;
        private readonly ILogger<PipelineController> _logger;

        // Event types that represent the grabber stage.
        private static readonly string[] GrabberEventTypes =
        [
            PipelineEventTypes.GrabStarted,
            PipelineEventTypes.GrabCompleted,
            PipelineEventTypes.GrabFailed,
        ];

        // Event types that represent the UDP stage.
        private static readonly string[] UdpEventTypes =
        [
            PipelineEventTypes.UdpSessionOpened,
            PipelineEventTypes.UdpDone,
            PipelineEventTypes.UdpNotModified,
            PipelineEventTypes.UdpFailed,
            PipelineEventTypes.UdpNoFrame,
        ];

        // Event types that represent the device stage.
        private static readonly string[] DeviceEventTypes =
        [
            PipelineEventTypes.DeviceOnline,
            PipelineEventTypes.DeviceOffline,
        ];

        /// <summary>
        /// Initialises the controller.
        /// </summary>
        /// <param name="events">Pipeline event repository.</param>
        /// <param name="templates">Template repository.</param>
        /// <param name="devices">Device repository.</param>
        /// <param name="logger">Logger.</param>
        public PipelineController(
            IPipelineEventRepository events,
            ITemplateRepository templates,
            IDeviceRepository devices,
            ILogger<PipelineController> logger)
        {
            ArgumentNullException.ThrowIfNull(events);
            ArgumentNullException.ThrowIfNull(templates);
            ArgumentNullException.ThrowIfNull(devices);
            ArgumentNullException.ThrowIfNull(logger);
            _events    = events;
            _templates = templates;
            _devices   = devices;
            _logger    = logger;
        }

        /// <summary>
        /// Appends a pipeline state-transition event to the database.
        /// Called by the UDP Server and the Grabber Worker to make every pipeline action observable.
        /// No JWT is required — this route is only reachable from localhost / private network.
        /// </summary>
        /// <param name="dto">Event data.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("event")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> LogEvent(
            [FromBody] LogPipelineEventDto dto,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(dto.EventType))
                return BadRequest("EventType is required.");

            // Validate FK references before inserting — ghost devices (deleted from DB
            // but still sending UDP packets) would cause FK constraint violations if we
            // store their stale IDs. Null out any ID that no longer exists; the event
            // itself is still recorded so observability data is never lost.
            int? resolvedDeviceId = null;
            if (dto.DeviceId.HasValue)
            {
                var device = await _devices.GetByIdAsync(dto.DeviceId.Value, ct).ConfigureAwait(false);
                if (device is null)
                    _logger.LogWarning(
                        "Pipeline event: device id {DeviceId} not found — event recorded without device reference.",
                        dto.DeviceId.Value);
                else
                    resolvedDeviceId = device.Id;
            }

            int? resolvedTemplateId = null;
            if (dto.TemplateId.HasValue)
            {
                var template = await _templates.GetByIdAsync(dto.TemplateId.Value, ct).ConfigureAwait(false);
                if (template is null)
                    _logger.LogWarning(
                        "Pipeline event: template id {TemplateId} not found — event recorded without template reference.",
                        dto.TemplateId.Value);
                else
                    resolvedTemplateId = template.Id;
            }

            var ev = new PipelineEvent
            {
                Ts           = DateTime.UtcNow,
                EventType    = dto.EventType,
                DeviceId     = resolvedDeviceId,
                TemplateId   = resolvedTemplateId,
                SessionId    = dto.SessionId,
                Success      = dto.Success,
                DurationMs   = dto.DurationMs,
                Payload      = dto.Payload,
                ErrorMessage = dto.ErrorMessage,
            };

            await _events.AddAsync(ev, ct).ConfigureAwait(false);

            _logger.LogDebug(
                "Pipeline event logged: type={Type} device={Device} template={Template} success={Success}",
                dto.EventType, resolvedDeviceId, resolvedTemplateId, dto.Success);

            return Ok();
        }

        /// <summary>
        /// Returns recent pipeline events, filtered by device, template, event type, or timestamp.
        /// Intended for the ISA-101 dashboard drill-down views (L2 alert list, L3 entity history, L4 diagnostics).
        /// </summary>
        /// <param name="deviceId">Filter to events for this device id.</param>
        /// <param name="templateId">Filter to events for this template id.</param>
        /// <param name="type">Filter to events of this type (e.g. "udp_done", "grab_failed").</param>
        /// <param name="since">ISO-8601 UTC timestamp — return only events after this point.</param>
        /// <param name="limit">Maximum rows to return (default 200, max 1000).</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("events")]
        [Authorize]
        [ProducesResponseType(typeof(IReadOnlyList<PipelineEventDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetEvents(
            [FromQuery] int?     deviceId   = null,
            [FromQuery] int?     templateId = null,
            [FromQuery] string?  type       = null,
            [FromQuery] DateTime? since     = null,
            [FromQuery] int      limit      = 200,
            CancellationToken ct = default)
        {
            // Cap to prevent runaway queries.
            int safeLimit = Math.Clamp(limit, 1, 1000);

            IReadOnlyList<PipelineEvent> rows = await _events
                .GetRecentAsync(deviceId, templateId, type, since, safeLimit, ct)
                .ConfigureAwait(false);

            IReadOnlyList<PipelineEventDto> dtos = rows
                .Select(e => new PipelineEventDto(
                    e.Id, e.Ts, e.EventType,
                    e.DeviceId, e.TemplateId,
                    e.SessionId, e.Success,
                    e.DurationMs, e.Payload, e.ErrorMessage))
                .ToList();

            return Ok(dtos);
        }

        /// <summary>
        /// Returns a 4-lane pipeline summary for the authenticated user.
        /// Each row represents a template and its assigned devices, with the latest
        /// grabber, UDP, and device-status event for each lane.
        /// Designed for the ISA-101 pipeline topology view in the dashboard.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("summary")]
        [Authorize]
        [ProducesResponseType(typeof(IReadOnlyList<PipelineSummaryRowDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSummary(CancellationToken ct = default)
        {
            int userId = GetUserId();
            if (userId <= 0)
                return Unauthorized();

            try
            {
                return await BuildSummaryAsync(userId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSummary failed for userId={UserId}", userId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private async Task<IActionResult> BuildSummaryAsync(int userId, CancellationToken ct)
        {

            // Load the user's templates and all their devices in parallel.
            var templatesTask = _templates.GetByUserAsync(userId, ct);
            var devicesTask   = _devices.GetByUserAsync(userId, ct);
            await Task.WhenAll(templatesTask, devicesTask).ConfigureAwait(false);

            IReadOnlyList<Template> templates = templatesTask.Result;
            IReadOnlyList<Device>   devices   = devicesTask.Result;

            if (templates.Count == 0)
                return Ok(Array.Empty<PipelineSummaryRowDto>());

            int[] templateIds = templates.Select(t => t.Id).ToArray();
            int[] deviceIds   = devices.Select(d => d.Id).ToArray();

            // Single round-trip: fetch recent events for all user entities.
            IReadOnlyList<PipelineEvent> allEvents =
                await _events.GetForEntitiesAsync(templateIds, deviceIds, limit: 1000, ct)
                             .ConfigureAwait(false);

            // Index events for O(1) lookup: latest per (key, eventType)
            // where key = templateId for grab events, deviceId for device/UDP events.
            var latestGrab   = LatestByKey(allEvents, e => e.TemplateId, GrabberEventTypes);
            var latestUdp    = LatestByKey(allEvents, e => e.DeviceId,   UdpEventTypes);
            var latestDevice = LatestByKey(allEvents, e => e.DeviceId,   DeviceEventTypes);

            // Build a device-lookup by templateId (many-to-many via Template.Devices).
            // Template.Devices is not included by GetByUserAsync, so use the devices list
            // and match by ActiveTemplateId (covers the active assignment visible in the dashboard).
            var devicesByTemplate = devices
                .Where(d => d.ActiveTemplateId.HasValue)
                .GroupBy(d => d.ActiveTemplateId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            var rows = templates
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t =>
                {
                    var grabEvent = latestGrab.GetValueOrDefault(t.Id);

                    var deviceRows = devicesByTemplate.TryGetValue(t.Id, out var devs)
                        ? devs.Select(d => new PipelineSummaryDeviceDto(
                            d.Id,
                            d.Name,
                            d.Status.ToString(),
                            ToEventCell(latestUdp.GetValueOrDefault(d.Id)),
                            ToEventCell(latestDevice.GetValueOrDefault(d.Id)))).ToList()
                        : [];

                    return new PipelineSummaryRowDto(
                        t.Id,
                        t.Name,
                        ToEventCell(grabEvent),
                        deviceRows);
                })
                .ToList();

            return Ok(rows);
        } // end BuildSummaryAsync

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Returns userId from the JWT claim.</summary>
        private int GetUserId()
        {
            string? claim = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue("sub");
            return int.TryParse(claim, out int id) ? id : 0;
        }

        /// <summary>
        /// Builds a dictionary mapping the int key (templateId or deviceId) to the most
        /// recent event whose type is in <paramref name="allowedTypes"/>.
        /// Only events with a non-null key are considered.
        /// </summary>
        private static Dictionary<int, PipelineEvent> LatestByKey(
            IReadOnlyList<PipelineEvent> events,
            Func<PipelineEvent, int?> keySelector,
            string[] allowedTypes)
        {
            var result = new Dictionary<int, PipelineEvent>();
            foreach (var ev in events) // already ordered newest-first
            {
                if (!allowedTypes.Contains(ev.EventType))
                    continue;

                int? key = keySelector(ev);
                if (key is null)
                    continue;

                // First occurrence = most recent (list is newest-first)
                result.TryAdd(key.Value, ev);
            }
            return result;
        }

        /// <summary>Converts a nullable pipeline event into a cell DTO.</summary>
        private static PipelineEventCellDto? ToEventCell(PipelineEvent? ev)
        {
            if (ev is null) return null;
            return new PipelineEventCellDto(ev.Id, ev.EventType, ev.Ts, ev.Success, ev.DurationMs, ev.ErrorMessage);
        }
    }
}
