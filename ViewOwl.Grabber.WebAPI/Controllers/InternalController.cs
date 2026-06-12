using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.DTO;
using ViewOwl.Grabber.Engine;
using ViewOwl.Grabber.WebAPI.DTOs;
using ViewOwl.Grabber.WebAPI.Services;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Internal service-to-service endpoints — consumed exclusively by <c>ViewOwl.UDP.Server</c>.
    /// No JWT is required; these routes must only be reachable from localhost / the private network.
    /// </summary>
    [ApiController]
    [Route("api/internal")]
    public sealed class InternalController : ControllerBase
    {
        private readonly IDeviceRepository _devices;
        private readonly IGuestDeviceRepository _guestDevices;
        private readonly ITemplateRepository _templates;
        private readonly IGrabLogRepository _grabLogs;
        private readonly IDevicePingRepository _pings;
        private readonly IDeviceCommandRepository _commands;
        private readonly IPipelineEventRepository _pipelineEvents;
        private readonly SharedConfig _sharedConfig;
        private readonly IDashboardNotificationService _notifications;
        private readonly IFrameRefreshQueue _refreshQueue;
        private readonly IGrabber _grabber;
        private readonly GrabChannel _grabChannel;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<InternalController> _logger;

        // Tracks when an immediate re-grab was last triggered per template.
        // Debounces the trigger so a burst of HELLOs from the same device does not
        // flood the grab channel with redundant jobs.
        private static readonly ConcurrentDictionary<int, DateTime> _lastGrabTriggerUtc = new();

        /// <summary>
        /// Initialises the controller with its repositories, notification service, shared configuration,
        /// and the in-memory frame-refresh queue.
        /// </summary>
        public InternalController(
            IDeviceRepository devices,
            IGuestDeviceRepository guestDevices,
            ITemplateRepository templates,
            IGrabLogRepository grabLogs,
            IDevicePingRepository pings,
            IDeviceCommandRepository commands,
            IPipelineEventRepository pipelineEvents,
            IOptions<SharedConfig> sharedOptions,
            IDashboardNotificationService notifications,
            IFrameRefreshQueue refreshQueue,
            IGrabber grabber,
            GrabChannel grabChannel,
            IServiceScopeFactory scopeFactory,
            ILogger<InternalController> logger)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            ArgumentNullException.ThrowIfNull(notifications);
            ArgumentNullException.ThrowIfNull(refreshQueue);
            ArgumentNullException.ThrowIfNull(logger);
            _devices        = devices;
            _guestDevices   = guestDevices;
            _templates      = templates;
            _grabLogs       = grabLogs;
            _pings          = pings;
            _commands       = commands;
            _pipelineEvents = pipelineEvents;
            _sharedConfig   = sharedOptions.Value;
            _notifications  = notifications;
            _refreshQueue   = refreshQueue;
            _grabber        = grabber;
            _grabChannel    = grabChannel;
            _scopeFactory   = scopeFactory;
            _logger         = logger;
        }

        /// <summary>
        /// Resolves a device token to the binary frame file that should be streamed over UDP.
        /// Called by UDP Server on every HELLO packet before starting a session.
        /// </summary>
        /// <remarks>
        /// Resolution chain: <c>Device.Token</c> → <c>Device.ActiveTemplateId</c>
        /// → <c>GrabLog</c> (last successful) → <c>{ExchangeFolder}/{TokenGuid}.bin</c>.
        /// </remarks>
        /// <param name="token">The permanent device token stored in <c>Device.Token</c>.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("device/{token}/frame")]
        [ProducesResponseType(typeof(FrameInfoDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetFrame(string token, CancellationToken ct = default)
        {
            _logger.LogInformation("GetFrame: token={Token}", token);

            Device? device = await _devices.GetByTokenAsync(token, ct).ConfigureAwait(false);
            if (device is null)
            {
                // Guest devices send their GUID as a 32-char hex string in C# Guid binary layout
                // (Data1/2/3 little-endian, Data4 big-endian — the output of Guid.ToByteArray()).
                // Try to resolve the token as a guest device before returning ERR_UNKNOWN_DEVICE.
                if (token.Length == 32)
                {
                    try
                    {
                        // The UDP server calls Guid.ToString("N") before putting the token in the URL,
                        // so the 32-char string is already standard UUID "N" format — parse it directly.
                        // Using new Guid(Convert.FromHexString(...)) would reverse Data1/2/3 a second
                        // time and produce the wrong Guid.
                        Guid candidateGuid = Guid.ParseExact(token, "N");

                        _logger.LogInformation(
                            "GetFrame: token not in Devices — trying GuestDevices lookup for guid={Guid}",
                            candidateGuid);

                        GuestDevice? guestDevice =
                            await _guestDevices.GetByGuidAsync(candidateGuid, ct).ConfigureAwait(false);

                        if (guestDevice is not null)
                        {
                            string guestFilePath =
                                Path.Combine(_sharedConfig.ExchangeFolder, $"{candidateGuid}.bin");

                            // Class-C guest devices store animation frames as a batch manifest
                            // ({guid:N}_batch.json) rather than a single {guid}.bin file.
                            // Check the manifest first so animation is served correctly.
                            string guestBatchManifest =
                                Path.Combine(_sharedConfig.ExchangeFolder, $"{candidateGuid:N}_batch.json");
                            bool guestHasBatch = System.IO.File.Exists(guestBatchManifest);

                            if (!guestHasBatch && !System.IO.File.Exists(guestFilePath))
                            {
                                _logger.LogWarning(
                                    "GetFrame: guest device {Guid} recognised but frame not ready yet " +
                                    "(no .bin and no batch manifest)",
                                    candidateGuid);

                                // Trigger an immediate single-frame re-grab so the device is not
                                // blocked until the next 5-minute GuestDeviceRefreshWorker cycle.
                                _ = TriggerGuestGrabAsync(guestDevice);

                                return NotFound(new { rejectCode = 4 }); /* ERR_NO_FRAME */
                            }

                            _logger.LogInformation(
                                "GetFrame: guest device {Guid} → hasBatch={HasBatch}",
                                candidateGuid, guestHasBatch);

                            // DeviceId = 0, TemplateId = 0 — guest devices have no DB device record.
                            // The UDP server uses FilePath/BatchManifestPath to stream the frame; the
                            // other fields are used only for dashboard notifications (skipped for guests).
                            return Ok(new FrameInfoDto(
                                0, 0, candidateGuid, guestFilePath,
                                guestHasBatch ? guestBatchManifest : null));
                        }
                    }
                    catch (FormatException)
                    {
                        // Token is 32 chars but not a valid "N"-format GUID — fall through to ERR_UNKNOWN_DEVICE.
                    }
                }

                _logger.LogWarning("GetFrame: device not found for token={Token}", token);
                return NotFound(new { rejectCode = 2 }); /* ERR_UNKNOWN_DEVICE — token not in database */
            }

            _logger.LogInformation(
                "GetFrame: device found id={DeviceId} name='{Name}' activeTemplateId={TemplateId}",
                device.Id, device.Name, device.ActiveTemplateId);

            if (device.ActiveTemplateId is null)
            {
                _logger.LogWarning("GetFrame: device {DeviceId} has no active template", device.Id);
                return NotFound(new { rejectCode = 3 }); /* ERR_NO_TEMPLATE — device has no active template */
            }

            // Prefer a grab that matches the device's native display resolution so a
            // 320×240 device never receives a 480×320 frame (and vice versa).
            // Falls back to the dimension-agnostic overload when DisplayWidth/Height are
            // not yet reported (first boot before hardware-info heartbeat arrives).
            GrabLog? log = device.DisplayWidth.HasValue && device.DisplayHeight.HasValue
                ? await _grabLogs
                      .GetLastSuccessfulAsync(
                          device.ActiveTemplateId.Value,
                          device.DisplayWidth.Value,
                          device.DisplayHeight.Value,
                          ct)
                      .ConfigureAwait(false)
                : await _grabLogs
                      .GetLastSuccessfulAsync(device.ActiveTemplateId.Value, ct)
                      .ConfigureAwait(false);

            if (log is null)
            {
                _logger.LogWarning(
                    "GetFrame: no successful grab log for templateId={TemplateId} (device={DeviceId})",
                    device.ActiveTemplateId.Value, device.Id);

                // Proactively trigger a re-grab so the device doesn't have to wait up to 5 minutes
                // for the next DeviceTemplateRefreshWorker cycle.
                // Fire-and-forget — debounced to at most once per 60 s per template.
                _ = TriggerGrabIfIdleAsync(
                    device.ActiveTemplateId.Value,
                    device.UserId,
                    device.DisplayWidth  ?? 480,
                    device.DisplayHeight ?? 320);

                return NotFound(new { rejectCode = 4 }); /* ERR_NO_FRAME — no successful grab yet */
            }

            _logger.LogInformation(
                "GetFrame: grab log found logId={LogId} tokenGuid={TokenGuid} completedAt={CompletedAt}",
                log.Id, log.TokenGuid, log.CompletedAt);

            string filePath          = Path.Combine(_sharedConfig.ExchangeFolder, $"{log.TokenGuid}.bin");
            string batchManifestPath = Path.Combine(_sharedConfig.ExchangeFolder, $"{log.TokenGuid:N}_batch.json");
            bool   hasBatch          = System.IO.File.Exists(batchManifestPath);

            // The most-recent GrabLog might be a single-frame grab that temporarily overrides
            // a Class-C batch grab (e.g. a manual dashboard grab or TriggerResolutionRegrabAsync
            // completing AFTER the batch).  Scan the last 10 logs and prefer the newest one that
            // has a batch manifest so the device always receives animation frames for Class-C
            // templates even when a stale single-frame grab is the DB-level "most recent".
            if (!hasBatch)
            {
                IReadOnlyList<GrabLog> recent =
                    await _grabLogs
                        .GetRecentAsync(device.ActiveTemplateId.Value, count: 10, ct)
                        .ConfigureAwait(false);

                foreach (GrabLog candidate in recent)
                {
                    if (candidate.Id == log.Id || candidate.Success != true)
                        continue;

                    // Skip dimension mismatch so we don't serve a batch rendered at the
                    // wrong resolution.  When the device has not yet reported its display
                    // size (DisplayWidth/Height null) we accept any resolution as a fallback,
                    // mirroring the GetLastSuccessfulAsync(templateId) overload.
                    if (device.DisplayWidth.HasValue && device.DisplayHeight.HasValue
                        && (candidate.Width  != device.DisplayWidth.Value
                         || candidate.Height != device.DisplayHeight.Value))
                        continue;

                    string candidateBatch = Path.Combine(
                        _sharedConfig.ExchangeFolder, $"{candidate.TokenGuid:N}_batch.json");

                    if (!System.IO.File.Exists(candidateBatch))
                        continue;

                    // Found a batch GrabLog — prefer it so the device receives animation frames.
                    _logger.LogInformation(
                        "GetFrame: single-frame log {SingleId} (token={SingleToken}) superseded by " +
                        "batch log {BatchId} (token={BatchToken}) — Class-C recovery",
                        log.Id, log.TokenGuid, candidate.Id, candidate.TokenGuid);

                    log               = candidate;
                    filePath          = Path.Combine(_sharedConfig.ExchangeFolder, $"{log.TokenGuid}.bin");
                    batchManifestPath = candidateBatch;
                    hasBatch          = true;
                    break;
                }
            }

            _logger.LogInformation(
                "GetFrame: ExchangeFolder='{Folder}' → checking file='{FilePath}' hasBatch={HasBatch}",
                _sharedConfig.ExchangeFolder, filePath, hasBatch);

            // For batch (Class-C) frames the .bin file is not written — only the manifest
            // and the individual _batch_N.bin files exist.  Accept the result when a manifest
            // is present even if the single-frame .bin is absent.
            if (!hasBatch && !System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("GetFrame: .bin file not found on disk: '{FilePath}'", filePath);

                // The DB record exists but the file was lost (e.g. disk cleanup, crash).
                // Trigger an immediate re-grab so the device does not wait up to 5 minutes.
                // Fire-and-forget — debounced to at most once per 60 s per template.
                _ = TriggerGrabIfIdleAsync(
                    device.ActiveTemplateId.Value,
                    device.UserId,
                    device.DisplayWidth  ?? 480,
                    device.DisplayHeight ?? 320);

                return NotFound(new { rejectCode = 4 }); /* ERR_NO_FRAME — .bin missing, re-grab triggered */
            }

            _logger.LogInformation(
                "GetFrame: OK → filePath='{FilePath}' batchManifest={Manifest}",
                filePath, hasBatch ? batchManifestPath : "none");

            return Ok(new FrameInfoDto(
                device.Id,
                log.TemplateId,
                log.TokenGuid,
                filePath,
                hasBatch ? batchManifestPath : null));
        }

        /// <summary>
        /// Called by the UDP Server when a device sends a HELLO or PING packet.
        /// Updates online status, last-seen timestamp, IP address, and hardware metadata.
        /// Records a <see cref="DevicePing"/> for uptime statistics.
        /// </summary>
        /// <param name="dto">Heartbeat payload from the UDP packet.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("device-heartbeat")]
        [ProducesResponseType(typeof(InternalApiResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(InternalApiResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> DeviceHeartbeat(
            [FromBody] DeviceHeartbeatDto dto,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            if (string.IsNullOrWhiteSpace(dto.Token))
                return BadRequest(new InternalApiResponse(false, "Token is required."));

            Device? device = await _devices.GetByTokenAsync(dto.Token, ct).ConfigureAwait(false);
            if (device is null)
            {
                _logger.LogWarning("Heartbeat from unknown device token: {Token}", dto.Token);
                return BadRequest(new InternalApiResponse(false, "Device not found."));
            }

            // If the device reports a MAC address, check whether another record for the same
            // user already holds that MAC — this means the physical board was re-flashed and
            // the stale entry must be removed so the dashboard doesn't show a ghost device.
            if (!string.IsNullOrWhiteSpace(dto.MacAddress))
            {
                await RemoveStaleMacDeviceAsync(dto.MacAddress, device.UserId, device.Id, ct)
                    .ConfigureAwait(false);
            }

            // Log device_online transition — only when coming back from offline/unknown.
            bool wasOffline = device.Status != DeviceStatus.Online;
            if (wasOffline)
            {
                _ = _pipelineEvents.AddAsync(new PipelineEvent
                {
                    Ts        = DateTime.UtcNow,
                    EventType = PipelineEventTypes.DeviceOnline,
                    DeviceId  = device.Id,
                    Success   = true,
                }, ct);
            }

            // Update online status, last-seen, and IP atomically
            await _devices.UpdateHeartbeatAsync(device.Id, DateTime.UtcNow, dto.IpAddress, ct)
                          .ConfigureAwait(false);

            // Update hardware metadata when present (HELLO packets carry this; PINGs typically don't).
            // Uses a direct SQL UPDATE via ExecuteUpdateAsync to avoid EF change-tracker conflicts
            // that arise when UpdateHeartbeatAsync has already touched the same row in this scope.
            bool hasHardwareInfo = dto.DisplayType is not null
                || dto.FirmwareVersion is not null
                || dto.DisplayWidth.HasValue
                || dto.DisplayHeight.HasValue
                || dto.MacAddress is not null
                || dto.ClientFrameCrc.HasValue;

            if (hasHardwareInfo)
            {
                try
                {
                    await _devices.UpdateHardwareInfoAsync(
                        device.Id,
                        dto.DisplayType,
                        dto.DisplayWidth,
                        dto.DisplayHeight,
                        dto.FirmwareVersion,
                        dto.MacAddress,
                        dto.ClientFrameCrc,
                        ct).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Updated hardware info: DeviceId={DeviceId}, Display={DisplayType} {W}x{H}, FW={FW}, MAC={MAC}",
                        device.Id, dto.DisplayType, dto.DisplayWidth, dto.DisplayHeight,
                        dto.FirmwareVersion, dto.MacAddress ?? "n/a");

                    // If the device reported display dimensions that differ from the last
                    // grab, trigger an immediate re-grab at the correct resolution so the
                    // device does not receive a wrongly-sized frame for the next 5 minutes.
                    if (dto.DisplayWidth.HasValue && dto.DisplayHeight.HasValue
                        && device.ActiveTemplateId.HasValue)
                    {
                        await TriggerResolutionRegrabAsync(
                            device, dto.DisplayWidth.Value, dto.DisplayHeight.Value, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // Hardware-info update is non-critical — log and continue so the SignalR
                    // ping notification below is not blocked by a DB error here.
                    _logger.LogError(ex, "Failed to update hardware info for device {DeviceId}", device.Id);
                }
            }

            // Persist live telemetry (RSSI, heap, uptime) carried by PING packets.
            bool hasLiveMetrics = dto.WifiRssi.HasValue || dto.FreeHeap.HasValue || dto.Uptime.HasValue;
            if (hasLiveMetrics)
            {
                try
                {
                    await _devices.UpdateLiveMetricsAsync(
                        device.Id,
                        dto.WifiRssi,
                        dto.FreeHeap,
                        dto.Uptime,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update live metrics for device {DeviceId}", device.Id);
                }
            }

            // Record ping for uptime graph
            await _pings.RecordAsync(
                new DevicePing { DeviceId = device.Id, Timestamp = DateTime.UtcNow, Success = true },
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Heartbeat: device {DeviceId} ({Token}) at {IP}",
                device.Id, dto.Token, dto.IpAddress);

            // Push real-time ping event to dashboard clients
            await _notifications.NotifyDevicePingAsync(
                device.UserId,
                new DevicePingEvent(
                    device.Id,
                    DateTime.UtcNow,
                    dto.Uptime,
                    dto.FreeHeap,
                    dto.WifiRssi))
                .ConfigureAwait(false);

            // Push online status change so dashboard icon updates immediately
            await _notifications.NotifyDeviceStatusChangedAsync(
                device.UserId,
                new DeviceStatusChangedEvent(
                    device.Id,
                    "Online",
                    DateTime.UtcNow,
                    dto.IpAddress))
                .ConfigureAwait(false);

            return Ok(new InternalApiResponse(true, "Heartbeat recorded."));
        }

        /// <summary>
        /// Called by the UDP Server when it begins streaming a frame to a device (AUTH sent).
        /// Pushes a <c>DeviceTransferStarted</c> SignalR event so the dashboard shows SEND status.
        /// </summary>
        /// <param name="dto">Transfer-start payload.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("device-transfer-started")]
        [ProducesResponseType(typeof(InternalApiResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(InternalApiResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> DeviceTransferStarted(
            [FromBody] DeviceTransferStartedDto dto,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            if (string.IsNullOrWhiteSpace(dto.Token))
                return BadRequest(new InternalApiResponse(false, "Token is required."));

            Device? device = await _devices.GetByTokenAsync(dto.Token, ct).ConfigureAwait(false);
            if (device is null)
            {
                _logger.LogWarning("Transfer-started from unknown device token: {Token}", dto.Token);
                return BadRequest(new InternalApiResponse(false, "Device not found."));
            }

            await _notifications.NotifyDeviceTransferStartedAsync(
                device.UserId,
                new DeviceTransferStartedEvent(device.Id, dto.TemplateId, dto.ImageSizeBytes, DateTime.UtcNow, dto.FileFlag))
                .ConfigureAwait(false);

            return Ok(new InternalApiResponse(true, "Transfer started recorded."));
        }

        /// <summary>
        /// Called by the UDP Server periodically during an active frame transfer (every W=8 chunks ACKed).
        /// Pushes a <c>DeviceTransferProgress</c> SignalR event for live dashboard progress display.
        /// </summary>
        /// <param name="dto">Transfer-progress payload.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("device-transfer-progress")]
        [ProducesResponseType(typeof(InternalApiResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(InternalApiResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> DeviceTransferProgress(
            [FromBody] DeviceTransferProgressDto dto,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            if (string.IsNullOrWhiteSpace(dto.Token))
                return BadRequest(new InternalApiResponse(false, "Token is required."));

            Device? device = await _devices.GetByTokenAsync(dto.Token, ct).ConfigureAwait(false);
            if (device is null)
                return BadRequest(new InternalApiResponse(false, "Device not found."));

            await _notifications.NotifyDeviceTransferProgressAsync(
                device.UserId,
                new DeviceTransferProgressEvent(
                    device.Id,
                    dto.ChunksSent,
                    dto.TotalChunks,
                    dto.ElapsedMs,
                    dto.Retries,
                    DateTime.UtcNow))
                .ConfigureAwait(false);

            return Ok(new InternalApiResponse(true, "Progress recorded."));
        }

        /// <summary>
        /// Called by the UDP Server when an image transfer to a device completes.
        /// </summary>
        /// <param name="dto">Grab-completion payload.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("device-grab-complete")]
        [ProducesResponseType(typeof(InternalApiResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(InternalApiResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> DeviceGrabComplete(
            [FromBody] DeviceGrabCompleteDto dto,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            if (string.IsNullOrWhiteSpace(dto.Token))
                return BadRequest(new InternalApiResponse(false, "Token is required."));

            Device? device = await _devices.GetByTokenAsync(dto.Token, ct).ConfigureAwait(false);
            if (device is null)
            {
                _logger.LogWarning("Grab-complete from unknown device token: {Token}", dto.Token);
                return BadRequest(new InternalApiResponse(false, "Device not found."));
            }

            string outcome = dto.Outcome ?? (dto.Success ? "success" : "fail");
            _logger.LogInformation(
                "Grab complete: device {DeviceId}, template {TemplateId}, outcome={Outcome}, bytes={Bytes}, ms={Ms}, retries={Retries}",
                device.Id, dto.TemplateId, outcome, dto.ImageSizeBytes, dto.DurationMs, dto.Retries);

            // Increment lifetime frame counter on successful delivery (not not_modified).
            if (dto.Success && outcome == "success")
            {
                await _devices.IncrementFramesSentAsync(device.Id, ct).ConfigureAwait(false);
            }

            await _notifications.NotifyDeviceGrabCompletedAsync(
                device.UserId,
                new DeviceGrabCompletedEvent(
                    device.Id,
                    dto.TemplateId,
                    dto.Success,
                    dto.ErrorMessage,
                    dto.ImageSizeBytes,
                    dto.DurationMs,
                    DateTime.UtcNow,
                    outcome,
                    dto.Retries,
                    dto.ThroughputKBps))
                .ConfigureAwait(false);

            return Ok(new InternalApiResponse(true, "Grab completion recorded."));
        }

        /// <summary>
        /// Records the outcome of a UDP transfer session as a <see cref="DevicePing"/>.
        /// Called by UDP Server after every session ends (success or failure).
        /// </summary>
        /// <param name="token">The permanent device token.</param>
        /// <param name="request">Session outcome.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("device/{token}/ping")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> RecordPing(
            string token,
            [FromBody] InternalPingRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            Device? device = await _devices.GetByTokenAsync(token, ct).ConfigureAwait(false);
            if (device is null)
                return NotFound("Device token not registered.");

            await _pings.RecordAsync(
                new DevicePing { DeviceId = device.Id, Success = request.Success },
                ct)
                .ConfigureAwait(false);

            return NoContent();
        }

        /// <summary>
        /// Called by the UDP Server when a device sends a HELLO but cannot be authenticated.
        /// Pushes a <c>DeviceKnocking</c> SignalR event so the dashboard can display the attempt.
        /// </summary>
        /// <param name="dto">Knocking payload: token, IP address, and failure reason.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("device-knocking")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> DeviceKnocking(
            [FromBody] DeviceKnockingDto dto,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            int? deviceId   = null;
            string? devName = null;
            int? ownerId    = null;

            // Try to look up the device so we can route the event to the right user.
            if (!string.IsNullOrWhiteSpace(dto.Token) && dto.Token != "?")
            {
                Device? device = await _devices.GetByTokenAsync(dto.Token, ct).ConfigureAwait(false);
                if (device is not null)
                {
                    deviceId = device.Id;
                    devName  = device.Name;
                    ownerId  = device.UserId;
                }
            }

            await _notifications.NotifyDeviceKnockingAsync(
                ownerId,
                new DeviceKnockingEvent(deviceId, devName, dto.IpAddress, dto.Token, dto.Reason, DateTime.UtcNow))
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Device knocking: ip={Ip} token={Token} reason={Reason} deviceId={DeviceId}",
                dto.IpAddress, dto.Token, dto.Reason, deviceId?.ToString() ?? "unknown");

            return Ok();
        }

        /// <summary>
        /// Checks whether there is a pending reboot command for the device identified by
        /// <paramref name="token"/>, and if so marks it as <c>sent</c> so it is not
        /// dispatched again on the next heartbeat.
        /// Called by UDP Server after receiving a PING packet.
        /// </summary>
        /// <param name="token">The permanent device token.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("device/{token}/consume-restart")]
        [ProducesResponseType(typeof(PendingRestartDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> ConsumeRestart(string token, CancellationToken ct = default)
        {
            Device? device = await _devices.GetByTokenAsync(token, ct).ConfigureAwait(false);
            if (device is null)
                return NotFound("Device token not registered.");

            IReadOnlyList<DeviceCommand> pending =
                await _commands.GetPendingByDeviceAsync(device.Id, ct).ConfigureAwait(false);

            DeviceCommand? reboot = pending.FirstOrDefault(
                c => string.Equals(c.CommandType, "reboot", StringComparison.OrdinalIgnoreCase)
                  && string.Equals(c.Status,      "pending", StringComparison.OrdinalIgnoreCase));

            // Check the in-memory frame-refresh queue — consumes the entry atomically.
            bool hasPendingRefresh = _refreshQueue.ConsumeRefresh(device.Id);

            if (reboot is null)
                return Ok(new PendingRestartDto(false, hasPendingRefresh));

            reboot.Status = "sent";
            await _commands.UpdateAsync(reboot, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Restart command {CommandId} marked as sent for device {DeviceId}",
                reboot.Id, device.Id);

            return Ok(new PendingRestartDto(true, hasPendingRefresh));
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Removes the stale device record (and its status template) that shares the same
        /// <paramref name="macAddress"/> as the current device. Called on the first HELLO after
        /// a physical board is re-flashed — the new token creates a fresh device entry while
        /// the old one becomes an unreachable ghost that would clutter the dashboard.
        /// </summary>
        private async Task RemoveStaleMacDeviceAsync(
            string macAddress, int userId, int currentDeviceId, CancellationToken ct)
        {
            Device? stale = await _devices
                .GetByMacAndUserAsync(macAddress, userId, currentDeviceId, ct)
                .ConfigureAwait(false);

            if (stale is null)
                return;

            _logger.LogInformation(
                "Re-flash detected: MAC {Mac} was device {StaleId} ('{StaleName}'). Removing stale record.",
                macAddress, stale.Id, stale.Name);

            // Delete the HTML template file from disk before removing the DB record.
            if (stale.ActiveTemplateId.HasValue)
            {
                string htmlPath = Path.Combine(
                    _sharedConfig.ExchangeFolder,
                    "SitesTemplates",
                    $"t{stale.ActiveTemplateId.Value}.html");

                if (System.IO.File.Exists(htmlPath))
                {
                    try
                    {
                        System.IO.File.Delete(htmlPath);
                    }
                    catch (Exception ex)
                    {
                        // Non-critical — log and continue; DB cleanup is more important.
                        _logger.LogWarning(ex,
                            "Could not delete stale template file: {Path}", htmlPath);
                    }
                }

                await _templates.DeleteAsync(stale.ActiveTemplateId.Value, ct).ConfigureAwait(false);
            }

            await _devices.DeleteAsync(stale.Id, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Stale device {StaleId} and its template removed after re-flash of MAC {Mac}.",
                stale.Id, macAddress);
        }

        /// <summary>
        /// Enqueues an immediate grab for the device's active template at the device's
        /// reported display resolution, but only when the last grab was at a different size.
        /// This prevents a 320×240 device from displaying a wrongly-sized 480×320 frame
        /// for up to 5 minutes after its first connection.
        /// </summary>
        private async Task TriggerResolutionRegrabAsync(
            Device device, int reportedWidth, int reportedHeight, CancellationToken ct)
        {
            if (!device.ActiveTemplateId.HasValue) return;

            // Check whether the last successful grab already matches the reported resolution.
            GrabLog? lastLog = await _grabLogs
                .GetLastSuccessfulAsync(device.ActiveTemplateId.Value, ct)
                .ConfigureAwait(false);

            bool alreadyCorrectSize = lastLog is not null
                && lastLog.Width  == reportedWidth
                && lastLog.Height == reportedHeight;

            if (alreadyCorrectSize) return;

            // Class-C templates use BATCH grabs.  Creating a single-frame GrabLog here
            // would make GetLastSuccessfulAsync return it as the most-recent entry at the
            // device's reported resolution and prevent the existing batch (rendered at the
            // old resolution) from being matched by the fallback scan in GetFrame.
            // Skip the single-frame trigger — DeviceTemplateRefreshWorker detects the new
            // device resolution on its next cycle and renders the batch at the correct size.
            if (await IsBatchTemplateAsync(_grabLogs, device.ActiveTemplateId.Value, ct)
                    .ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "TriggerResolutionRegrab: Class-C template {TemplateId} — " +
                    "skipping single-frame regrab at {W}×{H}; " +
                    "DeviceTemplateRefreshWorker will render batch at new resolution",
                    device.ActiveTemplateId.Value, reportedWidth, reportedHeight);
                return;
            }

            // Build the HTML file name that SiteTemplateController serves to Chromium.
            string htmlFileName = $"t{device.ActiveTemplateId.Value}.html";
            string sitesFolder  = Path.Combine(_sharedConfig.ExchangeFolder, "SitesTemplates");
            string htmlFilePath = Path.Combine(sitesFolder, htmlFileName);

            if (!System.IO.File.Exists(htmlFilePath))
            {
                _logger.LogWarning(
                    "Re-grab skipped — template file not found: {Path}", htmlFilePath);
                return;
            }

            Guid tokenGuid = Guid.NewGuid();
            GrabLog log = await _grabLogs
                .CreateAsync(device.ActiveTemplateId.Value, tokenGuid, reportedWidth, reportedHeight)
                .ConfigureAwait(false);

            LoadPageResultDTO loadResult =
                await _grabber.AddUrlAsync($"SitesTemplates/{htmlFileName}").ConfigureAwait(false);

            if (!loadResult.Success)
            {
                await _grabLogs
                    .MarkCompletedAsync(log.Id, false,
                        $"Re-grab browser load failed: {loadResult.ErrorMessage}")
                    .ConfigureAwait(false);
                _logger.LogWarning(
                    "Resolution re-grab failed for device {DeviceId}: {Error}",
                    device.Id, loadResult.ErrorMessage);
                return;
            }

            await _grabChannel.EnqueueAsync(new ScreenShotParamDTO
            {
                TokenGuid            = tokenGuid,
                TabName              = loadResult.TabName,
                Width                = reportedWidth,
                Height               = reportedHeight,
                GrabLogId            = log.Id,
                TemplateId           = device.ActiveTemplateId.Value,
                UserId               = device.UserId,
                CloseTabAfterCapture = true,
                Monochrome           = false,
            }).ConfigureAwait(false);

            _logger.LogInformation(
                "Resolution re-grab enqueued for device {DeviceId} at {W}×{H} (was {OldW}×{OldH})",
                device.Id, reportedWidth, reportedHeight,
                lastLog?.Width ?? 0, lastLog?.Height ?? 0);
        }

        // ── Immediate re-grab trigger ─────────────────────────────────────────

        /// <summary>
        /// Fires an immediate grab for <paramref name="templateId"/> when <c>GetFrame</c> finds
        /// no successful grab log. Debounced to at most once per 60 seconds per template so a
        /// burst of HELLO packets from the same device does not flood the grab channel.
        /// </summary>
        private async Task TriggerGrabIfIdleAsync(int templateId, int userId, int width, int height)
        {
            var now = DateTime.UtcNow;
            if (_lastGrabTriggerUtc.TryGetValue(templateId, out DateTime last) &&
                (now - last).TotalSeconds < 60)
            {
                return;
            }

            _lastGrabTriggerUtc[templateId] = now;

            string htmlFileName = $"t{templateId}.html";
            string htmlFilePath = Path.Combine(_sharedConfig.ExchangeFolder, "SitesTemplates", htmlFileName);

            if (!System.IO.File.Exists(htmlFilePath))
            {
                _logger.LogWarning(
                    "TriggerGrab: HTML file not found for template {TemplateId} — skipping", templateId);
                return;
            }

            try
            {
                // Create the DI scope early so we can check for Class-C before opening
                // a Chrome tab.  This method is fire-and-forget — the original request
                // scope (and its DbContext) may already be disposed.  _grabber and
                // _grabChannel are singletons and do not need a scope.
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                var grabLogs = scope.ServiceProvider.GetRequiredService<IGrabLogRepository>();

                // Class-C templates use BATCH grabs.  A single-frame GrabLog created here
                // would become the most-recent entry at this resolution and block the batch
                // from being served on subsequent HELLOs.  Skip the trigger and let
                // DeviceTemplateRefreshWorker render the batch on its next 5-minute cycle.
                if (await IsBatchTemplateAsync(grabLogs, templateId, CancellationToken.None)
                        .ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "TriggerGrabIfIdle: Class-C template {TemplateId} — " +
                        "skipping single-frame grab; DeviceTemplateRefreshWorker will render batch",
                        templateId);
                    return;
                }

                LoadPageResultDTO loadResult =
                    await _grabber.AddUrlAsync($"SitesTemplates/{htmlFileName}").ConfigureAwait(false);

                if (!loadResult.Success)
                {
                    _logger.LogWarning(
                        "TriggerGrab: browser failed to load template {TemplateId}: {Error}",
                        templateId, loadResult.ErrorMessage);
                    return;
                }

                Guid tokenGuid = Guid.NewGuid();

                // CancellationToken.None: fire-and-forget, no caller CT available.
                GrabLog log = await grabLogs
                    .CreateAsync(templateId, tokenGuid, width, height, CancellationToken.None)
                    .ConfigureAwait(false);

                // CancellationToken.None: fire-and-forget, no caller CT available.
                await _grabChannel.EnqueueAsync(new ScreenShotParamDTO
                {
                    TokenGuid            = tokenGuid,
                    TabName              = loadResult.TabName,
                    Width                = width,
                    Height               = height,
                    GrabLogId            = log.Id,
                    TemplateId           = templateId,
                    UserId               = userId,
                    CloseTabAfterCapture = true,
                    Monochrome           = false,
                }, CancellationToken.None).ConfigureAwait(false);

                _logger.LogInformation(
                    "TriggerGrab: enqueued immediate re-grab for template {TemplateId} at {W}×{H}",
                    templateId, width, height);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TriggerGrab: failed for template {TemplateId}", templateId);
            }
        }

        // ── Class-C detection helper ──────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> when any recent successful grab for <paramref name="templateId"/>
        /// has a batch manifest file on disk, which indicates a Class-C (multi-frame animated)
        /// template.  Used to guard against creating single-frame <c>GrabLog</c> entries that
        /// would pollute <c>GetLastSuccessfulAsync</c> results and prevent the batch from
        /// being served on subsequent HELLO packets.
        /// </summary>
        private async Task<bool> IsBatchTemplateAsync(
            IGrabLogRepository grabLogs, int templateId, CancellationToken ct)
        {
            IReadOnlyList<GrabLog> recent = await grabLogs
                .GetRecentAsync(templateId, count: 10, ct)
                .ConfigureAwait(false);

            foreach (GrabLog log in recent)
            {
                if (log.Success != true)
                    continue;

                string batchPath = Path.Combine(
                    _sharedConfig.ExchangeFolder,
                    $"{log.TokenGuid:N}_batch.json");

                if (System.IO.File.Exists(batchPath))
                    return true;
            }

            return false;
        }

        // ── Guest device immediate re-grab ────────────────────────────────────

        /// <summary>
        /// Enqueues an immediate grab for a guest device when <c>GetFrame</c> finds
        /// no <c>.bin</c> file on disk (e.g. first HELLO before the 5-minute refresh cycle runs).
        /// Fire-and-forget — errors are logged and swallowed.
        /// </summary>
        private async Task TriggerGuestGrabAsync(GuestDevice guestDevice)
        {
            string templatePath = Path.Combine(
                _sharedConfig.ExchangeFolder, "SitesTemplates", guestDevice.TemplateName);

            if (!System.IO.File.Exists(templatePath))
            {
                _logger.LogWarning(
                    "TriggerGuestGrab: template file missing for guest device {Guid}: {Path}",
                    guestDevice.Guid, templatePath);
                return;
            }

            try
            {
                LoadPageResultDTO loadResult =
                    await _grabber.AddUrlAsync($"SitesTemplates/{guestDevice.TemplateName}")
                                  .ConfigureAwait(false);

                if (!loadResult.Success)
                {
                    _logger.LogWarning(
                        "TriggerGuestGrab: browser failed to load '{Name}' for guest {Guid}: {Error}",
                        guestDevice.TemplateName, guestDevice.Guid, loadResult.ErrorMessage);
                    return;
                }

                // Use CancellationToken.None — this is fire-and-forget with no caller CT.
                await _grabChannel.EnqueueAsync(new ScreenShotParamDTO
                {
                    TokenGuid            = guestDevice.Guid,
                    TabName              = loadResult.TabName,
                    Width                = guestDevice.DisplayWidth,
                    Height               = guestDevice.DisplayHeight,
                    CloseTabAfterCapture = true,
                    // GrabLogId, TemplateId, UserId at defaults — guest grab, no DB records.
                }, CancellationToken.None).ConfigureAwait(false);

                _logger.LogInformation(
                    "TriggerGuestGrab: enqueued immediate re-grab for guest device {Guid} → {Name}",
                    guestDevice.Guid, guestDevice.TemplateName);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "TriggerGuestGrab: failed for guest device {Guid}", guestDevice.Guid);
            }
        }

        // ── Deploy webhook ────────────────────────────────────────────────────

        /// <summary>
        /// Called by GitHub Actions to broadcast a deploy lifecycle event to all dashboard clients.
        /// Authentication uses a pre-shared secret in <c>X-Deploy-Secret</c> — no JWT required.
        /// </summary>
        /// <param name="secret">Value of the <c>X-Deploy-Secret</c> request header.</param>
        /// <param name="dto">Deploy phase and optional commit SHA.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("deploy-notify")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> DeployNotify(
            [FromHeader(Name = "X-Deploy-Secret")] string? secret,
            [FromBody] DeployNotifyRequestDto dto,
            CancellationToken ct)
        {
            string configured = _sharedConfig.DeployWebhookSecret;
            if (string.IsNullOrWhiteSpace(configured)
                || string.IsNullOrWhiteSpace(secret)
                || !string.Equals(secret, configured, StringComparison.Ordinal))
            {
                return Unauthorized();
            }

            string? shortSha = dto.Commit is { Length: > 7 } sha ? sha[..7] : dto.Commit;

            await _notifications.NotifyDeployAsync(
                new DTOs.DeployNotificationEvent(dto.Phase, shortSha, DateTime.UtcNow))
                .ConfigureAwait(false);

            return Ok();
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Frame resolution response sent to UDP Server.
    /// When <see cref="BatchManifestPath"/> is non-null the device is a Class-C template;
    /// the UDP Server should use BATCH_START/BATCH_COMMIT instead of the regular DATA/DONE flow.
    /// </summary>
    public sealed record FrameInfoDto(
        int DeviceId,
        int TemplateId,
        Guid TokenGuid,
        string FilePath,
        string? BatchManifestPath = null);

    /// <summary>Ping recording request from UDP Server.</summary>
    public sealed record InternalPingRequestDto(bool Success);

    /// <summary>Response for the consume-restart endpoint.</summary>
    /// <param name="HasPendingRestart">
    /// <c>true</c> when a pending "reboot" command was found and marked as sent.
    /// </param>
    /// <param name="HasPendingFrameRefresh">
    /// <c>true</c> when a new frame was grabbed since the device last connected.
    /// The UDP server should send an early AUTH packet so the device fetches the
    /// new frame immediately instead of waiting for the 5-minute idle to expire.
    /// </param>
    public sealed record PendingRestartDto(bool HasPendingRestart, bool HasPendingFrameRefresh = false);

    /// <summary>Request body for <c>POST /api/internal/deploy-notify</c>.</summary>
    public sealed record DeployNotifyRequestDto(
        /// <summary>"starting" before services stop; "complete" after services restart.</summary>
        string Phase,
        /// <summary>Full or short commit SHA from <c>GITHUB_SHA</c>. Optional.</summary>
        string? Commit);
}
