using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.DTO;
using ViewOwl.Grabber.Engine;
using ViewOwl.Grabber.WebAPI.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// User-scoped device management endpoints.
    /// Each authenticated user can only see and manage their own devices.
    /// </summary>
    [ApiController]
    [Route("api/devices")]
    [Authorize]
    public sealed class DevicesController : ControllerBase
    {
        private readonly IDeviceRepository _devices;
        private readonly IDevicePingRepository _pings;
        private readonly IConnectionRepository _connections;
        private readonly ITemplateRepository _templates;
        private readonly IDeviceCommandRepository _commands;
        private readonly IGrabber _grabber;
        private readonly GrabChannel _channel;
        private readonly IGrabLogRepository _grabLogs;
        private readonly SharedConfig _sharedConfig;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DevicesController> _logger;

        /// <summary>
        /// Initialises the controller with its repositories.
        /// </summary>
        /// <param name="devices">Device repository.</param>
        /// <param name="pings">Ping repository for stats.</param>
        /// <param name="connections">Connection repository for device-template links.</param>
        /// <param name="templates">Template repository for active-template name resolution.</param>
        /// <param name="commands">Device command repository for queuing control commands.</param>
        /// <param name="grabber">Headless browser grabber for immediate frame capture.</param>
        /// <param name="channel">Screenshot request channel shared with <c>GrabWorker</c>.</param>
        /// <param name="grabLogs">Grab log repository for recording capture state.</param>
        /// <param name="sharedOptions">Shared configuration (ExchangeFolder path).</param>
        /// <param name="scopeFactory">Factory for creating DI scopes in background tasks.</param>
        /// <param name="logger">Logger.</param>
        public DevicesController(
            IDeviceRepository devices,
            IDevicePingRepository pings,
            IConnectionRepository connections,
            ITemplateRepository templates,
            IDeviceCommandRepository commands,
            IGrabber grabber,
            GrabChannel channel,
            IGrabLogRepository grabLogs,
            IOptions<SharedConfig> sharedOptions,
            IServiceScopeFactory scopeFactory,
            ILogger<DevicesController> logger)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            _devices     = devices;
            _pings       = pings;
            _connections = connections;
            _templates   = templates;
            _commands    = commands;
            _grabber     = grabber;
            _channel     = channel;
            _grabLogs    = grabLogs;
            _sharedConfig = sharedOptions.Value;
            _scopeFactory = scopeFactory;
            _logger      = logger;
        }

        /// <summary>Returns all devices owned by the authenticated user.</summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet]
        [ProducesResponseType(typeof(IReadOnlyList<DeviceDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAll(CancellationToken ct = default)
        {
            int userId = GetUserId();
            var devices = await _devices.GetByUserAsync(userId, ct).ConfigureAwait(false);
            return Ok(devices.Select(DeviceDto.From));
        }

        /// <summary>
        /// Registers a new device for the authenticated user.
        /// Automatically creates a device-status template pre-populated with the device token
        /// and triggers an immediate frame grab so the device receives its first frame on
        /// first connection rather than waiting for the next 5-minute refresh cycle.
        /// </summary>
        /// <param name="request">Device details.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost]
        [ProducesResponseType(typeof(DeviceDto), StatusCodes.Status201Created)]
        public async Task<IActionResult> Create(
            [FromBody] CreateDeviceRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            int userId = GetUserId();

            if (await _devices.NameExistsForUserAsync(userId, request.Name, ct: ct).ConfigureAwait(false))
                return Conflict(new { error = $"A device named '{request.Name}' already exists." });

            // Use the caller-supplied token (BURN wizard pre-registration) or generate a new one.
            string token = string.IsNullOrWhiteSpace(request.Token)
                ? Guid.NewGuid().ToString("N")
                : request.Token.Trim();

            var device = new Device
            {
                Token         = token,
                Name          = request.Name,
                UserId        = userId,
                DisplayWidth  = request.DisplayWidth,
                DisplayHeight = request.DisplayHeight,
                DisplayType   = request.DisplayType,
            };

            var created = await _devices.CreateAsync(device, ct).ConfigureAwait(false);

            // Auto-create a device-status template and trigger an immediate grab.
            // Disabled by default (Shared.AutoCreateDeviceStatusTemplate): these per-device
            // templates leaked into the public gallery and some crashed the headless grabber
            // browser. Gated behind config so the behaviour can be revived once hardened.
            // Runs in a new DI scope — the request scope (and its DbContext) will be
            // disposed before this task completes, so we must not reuse scoped services.
            if (_sharedConfig.AutoCreateDeviceStatusTemplate)
            {
                int capturedId       = created.Id;
                string capturedToken = created.Token;
                string capturedName  = created.Name;
                int capturedW        = created.DisplayWidth  ?? 480;
                int capturedH        = created.DisplayHeight ?? 320;
                _ = Task.Run(async () =>
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    try
                    {
                        await CreateDeviceStatusTemplateAsync(
                            scope.ServiceProvider, capturedId, capturedToken, capturedName, userId,
                            capturedW, capturedH, immediateGrab: false)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Failed to create device-status template for device {DeviceId} ({Token})",
                            capturedId, capturedToken);
                    }
                });
            }

            return CreatedAtAction(nameof(GetAll), DeviceDto.From(created));
        }

        /// <summary>
        /// Creates a device-status template for <paramref name="deviceId"/>, assigns it as
        /// the active template, and enqueues an immediate 480×320 grab so the first frame
        /// is ready before the device connects.
        /// Must be called with a fresh <paramref name="services"/> scope — never pass the
        /// request scope, which will be disposed before this method completes.
        /// </summary>
        private async Task CreateDeviceStatusTemplateAsync(
            IServiceProvider services, int deviceId, string deviceToken, string deviceName, int userId,
            int displayWidth = 480, int displayHeight = 320, bool immediateGrab = true)
        {
            var devices     = services.GetRequiredService<IDeviceRepository>();
            var templates   = services.GetRequiredService<ITemplateRepository>();
            var grabLogs    = services.GetRequiredService<IGrabLogRepository>();
            var connections = services.GetRequiredService<IConnectionRepository>();

            // Read the base template from the SitesTemplates folder and embed the device token.
            string sitesFolder = Path.Combine(_sharedConfig.ExchangeFolder, "SitesTemplates");
            string basePath    = Path.Combine(sitesFolder, "device_status.html");

            if (!System.IO.File.Exists(basePath))
            {
                _logger.LogWarning(
                    "device_status.html base template not found at '{Path}' — skipping auto-template creation.",
                    basePath);
                return;
            }

            string baseHtml = await System.IO.File.ReadAllTextAsync(basePath).ConfigureAwait(false);

            // Embed the device token so the template can call GET /api/device/{token}/status.
            string html = baseHtml.Replace("__DEVICE_TOKEN__", deviceToken,
                StringComparison.Ordinal);

            // Persist as a DB template record owned by the same user.
            var template = new Template
            {
                Name        = $"STATUS — {deviceName}",
                HtmlContent = html,
                UserId      = userId,
                IsPublic    = false,   // device-status templates never belong on the public gallery
            };

            var savedTemplate = await templates.CreateAsync(template).ConfigureAwait(false);

            // Assign as the device's active template (also adds the many-to-many join row).
            await devices.AssignTemplateAsync(deviceId, savedTemplate.Id).ConfigureAwait(false);

            // Create the flow-canvas Connection record so the edge appears automatically
            // without the user having to drag it manually.
            await connections.CreateAsync(new Connection
            {
                UserId     = userId,
                TemplateId = savedTemplate.Id,
                DeviceId   = deviceId,
                CreatedAt  = DateTime.UtcNow,
            }).ConfigureAwait(false);

            _logger.LogInformation(
                "Auto-created device-status template {TemplateId} for device {DeviceId}",
                savedTemplate.Id, deviceId);

            // Write HTML to disk so SiteTemplateController can serve it to Chromium.
            Directory.CreateDirectory(sitesFolder);
            string htmlFileName = $"t{savedTemplate.Id}.html";
            string htmlFilePath = Path.Combine(sitesFolder, htmlFileName);
            await System.IO.File.WriteAllTextAsync(htmlFilePath, html).ConfigureAwait(false);

            // Registration path skips the immediate grab: it opens a tab in the shared Chromium
            // (the historical grabber-crash reason this feature was disabled). The assignment above
            // already saved the device from "no template"; the first frame arrives on the normal
            // grab cycle, which also resolves the correct frame format per display type.
            if (!immediateGrab)
                return;

            // Create a GrabLog row and open an ephemeral browser tab.
            Guid tokenGuid = Guid.NewGuid();
            GrabLog log = await grabLogs
                .CreateAsync(savedTemplate.Id, tokenGuid, displayWidth, displayHeight)
                .ConfigureAwait(false);

            LoadPageResultDTO loadResult =
                await _grabber.AddUrlAsync($"SitesTemplates/{htmlFileName}").ConfigureAwait(false);

            if (!loadResult.Success)
            {
                await grabLogs
                    .MarkCompletedAsync(log.Id, false,
                        $"Browser failed to load template: {loadResult.ErrorMessage}")
                    .ConfigureAwait(false);
                _logger.LogWarning(
                    "Immediate grab for device {DeviceId} failed: {Error}",
                    deviceId, loadResult.ErrorMessage);
                return;
            }

            var param = new ScreenShotParamDTO
            {
                TokenGuid            = tokenGuid,
                TabName              = loadResult.TabName,
                Width                = displayWidth,
                Height               = displayHeight,
                GrabLogId            = log.Id,
                TemplateId           = savedTemplate.Id,
                UserId               = userId,
                CloseTabAfterCapture = true,
                Monochrome           = false,
            };

            await _channel.EnqueueAsync(param).ConfigureAwait(false);

            _logger.LogInformation(
                "Immediate grab enqueued for device {DeviceId}, template {TemplateId}",
                deviceId, savedTemplate.Id);
        }

        /// <summary>
        /// Creates (or re-creates) the device-status template for an existing device and
        /// triggers an immediate frame grab. Useful for devices registered before the
        /// auto-template feature was introduced, or after a token change.
        /// </summary>
        /// <param name="id">Device id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("{id:int}/create-status-template")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CreateStatusTemplate(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null)
                return NotFound();

            if (device.UserId != userId)
                return Forbid();

            int capturedId2      = device.Id;
            string capturedToken2 = device.Token;
            string capturedName2  = device.Name;
            int capturedW2       = device.DisplayWidth  ?? 480;
            int capturedH2       = device.DisplayHeight ?? 320;
            _ = Task.Run(async () =>
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                try
                {
                    await CreateDeviceStatusTemplateAsync(
                        scope.ServiceProvider, capturedId2, capturedToken2, capturedName2, userId,
                        capturedW2, capturedH2)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to create device-status template for device {DeviceId}",
                        capturedId2);
                }
            });

            return Accepted(new { message = "Status template creation started." });
        }

        /// <summary>Deletes a device by id. Only the owning user may delete.</summary>
        /// <param name="id">Device id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpDelete("{id:int}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Delete(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null)
                return NoContent(); // idempotent

            if (device.UserId != userId)
                return Forbid();

            await _devices.DeleteAsync(id, ct).ConfigureAwait(false);
            return NoContent();
        }

        /// <summary>Assigns a template to a device (adds to many-to-many collection).</summary>
        /// <param name="id">Device id.</param>
        /// <param name="templateId">Template id to assign.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("{id:int}/template/{templateId:int}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> AssignTemplate(
            int id, int templateId, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null)
                return NotFound();

            if (device.UserId != userId)
                return Forbid();

            await _devices.AssignTemplateAsync(id, templateId, ct).ConfigureAwait(false);
            return NoContent();
        }

        /// <summary>
        /// Sets the active template for a device.
        /// Pass <c>null</c> as <c>templateId</c> to clear the active template.
        /// </summary>
        /// <param name="id">Device id.</param>
        /// <param name="request">Active template request.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPatch("{id:int}/active-template")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> SetActiveTemplate(
            int id,
            [FromBody] SetActiveTemplateRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null)
                return NotFound();

            if (device.UserId != userId)
                return Forbid();

            await _devices.SetActiveTemplateAsync(id, request.TemplateId, ct).ConfigureAwait(false);
            return NoContent();
        }

        /// <summary>
        /// Returns uptime statistics for a device (last 24 hours by default).
        /// </summary>
        /// <param name="id">Device id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{id:int}/stats")]
        [ProducesResponseType(typeof(DeviceStatsDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetStats(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null)
                return NotFound();

            if (device.UserId != userId)
                return Forbid();

            var recent = await _pings.GetRecentAsync(id, 100, ct).ConfigureAwait(false);
            double? uptime = await _pings.GetUptimeRatioAsync(id, 24, ct).ConfigureAwait(false);

            return Ok(new DeviceStatsDto(
                DeviceId:     id,
                UptimeRatio:  uptime,
                RecentPings:  recent.Select(p => new PingDto(p.Timestamp, p.Success)).ToList()));
        }

        /// <summary>
        /// Returns the active template connection for a device (the first connection, if any).
        /// Used by the dashboard to show what image a device is currently displaying.
        /// </summary>
        /// <param name="id">Device ID.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{id:int}/active-connection")]
        [ProducesResponseType(typeof(ConnectionResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetActiveConnection(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null || device.UserId != userId)
                return NotFound();

            var connections = await _connections.GetByDeviceAsync(id, ct).ConfigureAwait(false);

            // Return the first connection — in future phases this may use priority ordering
            if (connections.Count == 0)
                return NotFound(new { message = "No active connection for this device." });

            var active = connections[0];

            return Ok(ConnectionResponseDto.From(active));
        }

        /// <summary>
        /// Returns full detail for a single device owned by the authenticated user.
        /// </summary>
        /// <param name="id">Device id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{id:int}")]
        [ProducesResponseType(typeof(DeviceDetailDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetById(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null)
                return NotFound();

            if (device.UserId != userId)
                return Forbid();

            string? activeTemplateName = null;
            if (device.ActiveTemplateId.HasValue)
            {
                var template = await _templates.GetByIdAsync(device.ActiveTemplateId.Value, ct).ConfigureAwait(false);
                activeTemplateName = template?.Name;
            }

            return Ok(new DeviceDetailDto(
                Id:                 device.Id,
                Name:               device.Name,
                Token:              device.Token,
                DisplayType:        device.DisplayType,
                DisplayWidth:       device.DisplayWidth,
                DisplayHeight:      device.DisplayHeight,
                FirmwareVersion:    device.FirmwareVersion,
                Status:             device.Status.ToString(),
                LastSeenAt:         device.LastSeenAt,
                IpAddress:          device.IpAddress,
                ActiveTemplateId:   device.ActiveTemplateId,
                ActiveTemplateName: activeTemplateName,
                CreatedAt:          device.CreatedAt));
        }

        /// <summary>
        /// Renames a device. Only the owning user may rename.
        /// </summary>
        /// <param name="id">Device id.</param>
        /// <param name="request">New name.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPatch("{id:int}/name")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Rename(
            int id,
            [FromBody] RenameDeviceRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null)
                return NotFound();

            if (device.UserId != userId)
                return Forbid();

            if (await _devices.NameExistsForUserAsync(userId, request.Name, excludeId: id, ct: ct).ConfigureAwait(false))
                return Conflict(new { error = $"A device named '{request.Name}' already exists." });

            device.Name = request.Name;
            await _devices.UpdateAsync(device, ct).ConfigureAwait(false);
            return NoContent();
        }

        /// <summary>
        /// Queues a restart (reboot) command for the device.
        /// The UDP server will deliver the command on the next session.
        /// </summary>
        /// <param name="id">Device id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("{id:int}/restart")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Restart(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null)
                return NotFound();

            if (device.UserId != userId)
                return Forbid();

            var command = new DeviceCommand
            {
                DeviceId    = id,
                CommandType = "reboot",
                Status      = "pending",
                CreatedAt   = DateTime.UtcNow
            };

            var created = await _commands.CreateAsync(command, ct).ConfigureAwait(false);
            return Accepted(new { message = "Restart command queued.", commandId = created.Id });
        }

        /// <summary>
        /// Updates the token for a device. Only the owning user may update.
        /// The new token must be a 32-character lowercase hex string (GUID N format, no dashes).
        /// </summary>
        /// <param name="id">Device id.</param>
        /// <param name="request">New token.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPatch("{id:int}/token")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateToken(
            int id,
            [FromBody] UpdateTokenRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Validate: must be a 32-char hex string (no dashes)
            if (string.IsNullOrWhiteSpace(request.Token)
                || request.Token.Length != 32
                || !request.Token.All(char.IsAsciiHexDigit))
            {
                return BadRequest(new { message = "Token must be a 32-character hex string (GUID N format, no dashes)." });
            }

            int userId = GetUserId();
            var device = await _devices.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (device is null)
                return NotFound();

            if (device.UserId != userId)
                return Forbid();

            device.Token = request.Token.ToLowerInvariant();
            await _devices.UpdateAsync(device, ct).ConfigureAwait(false);
            return NoContent();
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

    /// <summary>Device response shape.</summary>
    public sealed record DeviceDto(int Id, string Token, string Name, int? ActiveTemplateId, DateTime CreatedAt)
    {
        /// <summary>Maps a <see cref="Device"/> entity to a <see cref="DeviceDto"/>.</summary>
        public static DeviceDto From(Device d)
        {
            ArgumentNullException.ThrowIfNull(d);
            return new(d.Id, d.Token, d.Name, d.ActiveTemplateId, d.CreatedAt);
        }
    }

    /// <summary>Create-device request body.</summary>
    /// <param name="Name">Human-readable device name.</param>
    /// <param name="Token">
    /// Optional pre-generated token (32-char hex, "N" GUID format).
    /// When supplied the device is registered with this exact token — used by the
    /// BURN wizard to pre-register a device before flashing so it appears in the
    /// dashboard automatically on first connection.
    /// When omitted a new random token is generated.
    /// </param>
    /// <param name="DisplayWidth">Optional display width in pixels (e.g. 480 or 320).</param>
    /// <param name="DisplayHeight">Optional display height in pixels (e.g. 320 or 240).</param>
    /// <param name="DisplayType">Optional display driver name (e.g. ST7796, ILI9341).</param>
    public sealed record CreateDeviceRequestDto(
        string Name,
        string? Token = null,
        int? DisplayWidth = null,
        int? DisplayHeight = null,
        string? DisplayType = null);

    /// <summary>Set-active-template request body.</summary>
    public sealed record SetActiveTemplateRequestDto(int? TemplateId);

    /// <summary>Device statistics response.</summary>
    public sealed record DeviceStatsDto(
        int DeviceId,
        double? UptimeRatio,
        IReadOnlyList<PingDto> RecentPings);

    /// <summary>Single ping record in stats response.</summary>
    public sealed record PingDto(DateTime Timestamp, bool Success);

    /// <summary>Full device detail including active template and live stats fields.</summary>
    public sealed record DeviceDetailDto(
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
        DateTime CreatedAt);

    /// <summary>Rename-device request body.</summary>
    public sealed record RenameDeviceRequestDto(string Name);

    /// <summary>Update-token request body.</summary>
    /// <param name="Token">32-character lowercase hex string (GUID N format, no dashes).</param>
    public sealed record UpdateTokenRequestDto(string Token);
}
