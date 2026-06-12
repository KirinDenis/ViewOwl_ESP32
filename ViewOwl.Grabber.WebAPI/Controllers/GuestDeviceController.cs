using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.DTO;
using ViewOwl.Grabber.Engine;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Public API for guest devices flashed from the landing page.
    /// No authentication required — called by the browser immediately after serial provisioning.
    /// </summary>
    [ApiController]
    [Route("api/guest")]
    public sealed class GuestDeviceController : ControllerBase
    {
        private readonly IGuestDeviceRepository _guestDevices;
        private readonly IGrabber _grabber;
        private readonly GrabChannel _grabChannel;
        private readonly SharedConfig _sharedConfig;
        private readonly ILogger<GuestDeviceController> _logger;

        /// <summary>
        /// Initialises the controller with its dependencies.
        /// </summary>
        public GuestDeviceController(
            IGuestDeviceRepository guestDevices,
            IGrabber grabber,
            GrabChannel grabChannel,
            IOptions<SharedConfig> sharedOptions,
            ILogger<GuestDeviceController> logger)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            _guestDevices = guestDevices;
            _grabber      = grabber;
            _grabChannel  = grabChannel;
            _sharedConfig = sharedOptions.Value;
            _logger       = logger;
        }

        // ── Request / response DTOs ───────────────────────────────────────────

        /// <summary>Request body for <see cref="Register"/>.</summary>
        public sealed record RegisterRequest(
            /// <summary>Unique GUID baked into the firmware during serial provisioning.</summary>
            Guid Guid,
            /// <summary>Template display name (DB <c>Template.Name</c>) or legacy filename.</summary>
            string TemplateName,
            /// <summary>
            /// DB primary key of the selected <c>Template</c>.
            /// <see langword="null"/> for legacy registrations that reference a static file by name.
            /// When provided, the server uses <c>t{TemplateId}.html</c> for grabs instead of
            /// looking up a static file in <c>SitesTemplates/</c>.
            /// </summary>
            int? TemplateId,
            /// <summary>Display pixel width (e.g. 480 or 320).</summary>
            int DisplayWidth,
            /// <summary>Display pixel height (e.g. 320 or 240).</summary>
            int DisplayHeight);

        /// <summary>Response body for <see cref="Register"/>.</summary>
        public sealed record RegisterResponse(
            /// <summary>The GUID that identifies this device.</summary>
            Guid Guid,
            /// <summary>Template file name echoed back for client confirmation.</summary>
            string TemplateName,
            /// <summary>
            /// <c>true</c> when the device was newly created; <c>false</c> when it was
            /// already registered (idempotent re-registration after a reflash).
            /// </summary>
            bool Created);

        // ── Endpoints ─────────────────────────────────────────────────────────

        /// <summary>
        /// Registers a freshly flashed guest device and schedules an immediate grab
        /// so the exchange-folder .bin file is ready before the device connects via UDP.
        /// Idempotent: if the GUID already exists the call succeeds and returns <c>Created=false</c>.
        /// </summary>
        /// <param name="request">Device details collected during serial provisioning.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("device")]
        [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Register(
            [FromBody] RegisterRequest request,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request.TemplateName))
                return BadRequest("TemplateName is required.");

            if (request.DisplayWidth <= 0 || request.DisplayHeight <= 0)
                return BadRequest("DisplayWidth and DisplayHeight must be positive.");

            // Prevent path traversal — only bare filenames are accepted.
            string safeName = Path.GetFileName(request.TemplateName);
            if (string.IsNullOrEmpty(safeName) || safeName != request.TemplateName)
                return BadRequest("Invalid template name.");

            // Idempotency: check whether this GUID was already registered (e.g. reflash).
            IReadOnlyList<GuestDevice> existing = await _guestDevices.GetAllAsync(ct).ConfigureAwait(false);
            GuestDevice? found = existing.FirstOrDefault(g => g.Guid == request.Guid);

            if (found is not null)
            {
                // If the requested template differs from the stored one, update the record so
                // GuestDeviceRefreshWorker picks up the new template on its next cycle.
                bool templateChanged = !string.Equals(
                    found.TemplateName, safeName, StringComparison.OrdinalIgnoreCase);

                if (templateChanged)
                {
                    await _guestDevices.UpdateTemplateAsync(found.Id, safeName, request.TemplateId, ct)
                                       .ConfigureAwait(false);

                    _logger.LogInformation(
                        "[GuestDevice] Template updated for {Guid}: {OldName} → {NewName}",
                        request.Guid, found.TemplateName, safeName);
                }
                else
                {
                    _logger.LogInformation(
                        "[GuestDevice] Re-registration for existing GUID {Guid} (template: {Name})",
                        request.Guid, safeName);
                }

                // Always grab the requested template immediately (covers both PUSH and reflash).
                await TriggerImmediateGrabAsync(found.Guid, safeName, request.TemplateId, found.DisplayWidth, found.DisplayHeight, ct)
                    .ConfigureAwait(false);

                // One active guest device per template: evict any stale records and their files.
                await PruneStaleGuestDevicesAsync(request.TemplateId, safeName, found.Guid, ct)
                    .ConfigureAwait(false);

                return Ok(new RegisterResponse(found.Guid, safeName, Created: false));
            }

            // Create the new guest device record.
            GuestDevice device = await _guestDevices.CreateAsync(
                request.Guid,
                safeName,
                request.TemplateId,
                request.DisplayWidth,
                request.DisplayHeight,
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "[GuestDevice] Registered new device {Guid} → template {Name} ({W}x{H})",
                device.Guid, device.TemplateName, device.DisplayWidth, device.DisplayHeight);

            // Fire-and-forget an immediate grab so the .bin is ready for the first UDP HELLO.
            await TriggerImmediateGrabAsync(device.Guid, device.TemplateName, device.TemplateId, device.DisplayWidth, device.DisplayHeight, ct)
                .ConfigureAwait(false);

            // One active guest device per template: evict any stale records and their files.
            await PruneStaleGuestDevicesAsync(request.TemplateId, safeName, device.Guid, ct)
                .ConfigureAwait(false);

            return CreatedAtAction(
                nameof(Register),
                new RegisterResponse(device.Guid, device.TemplateName, Created: true));
        }

        // ── Frame push endpoint ───────────────────────────────────────────────

        /// <summary>
        /// Accepts a browser-captured BGR565 frame for a guest device and writes it
        /// atomically to <c>ExchangeFolder/{guid}.bin</c> for immediate UDP delivery.
        /// Called by the landing-page push loop at 1–5-second intervals.
        /// No authentication required — the device GUID is the caller's credential.
        /// </summary>
        /// <param name="guid">The device GUID that identifies the guest device.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("device/{guid:guid}/frame")]
        [Consumes("application/octet-stream")]
        [RequestSizeLimit(1_048_576)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> PushFrame(Guid guid, CancellationToken ct = default)
        {
            GuestDevice? device = await _guestDevices.GetByGuidAsync(guid, ct).ConfigureAwait(false);
            if (device is null)
            {
                _logger.LogWarning("[GuestDevice] PushFrame: unknown guid={Guid}", guid);
                return NotFound();
            }

            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            byte[] frameBytes = ms.ToArray();

            // Validate frame format — must be raw BGR565 (exact size) or a
            // flag-prefixed compressed format produced by the browser compressor.
            int  expectedRaw  = device.DisplayWidth * device.DisplayHeight * 2;
            bool isLegacyRaw  = frameBytes.Length == expectedRaw;
            bool isKnownFlag  = frameBytes.Length >= 2 &&
                                (frameBytes[0] == 0x00 ||  // raw + flag byte
                                 frameBytes[0] == 0x01 ||  // palette+RLE
                                 frameBytes[0] == 0x03);   // palette+LZ4

            if (!isLegacyRaw && !isKnownFlag)
            {
                _logger.LogWarning(
                    "[GuestDevice] PushFrame: invalid frame guid={Guid} bytes={Bytes} flag=0x{Flag:X2}",
                    guid, frameBytes.Length, frameBytes.Length > 0 ? frameBytes[0] : 0);
                return BadRequest(
                    $"Invalid frame: {frameBytes.Length} bytes received. " +
                    $"Expected {expectedRaw} bytes (raw BGR565) or a compressed frame.");
            }

            // Atomic write: .tmp → rename so the UDP Server never reads a partial file.
            string binPath = Path.Combine(_sharedConfig.ExchangeFolder, $"{guid}.bin");
            string tmpPath = binPath + ".tmp";

            await System.IO.File.WriteAllBytesAsync(tmpPath, frameBytes, ct).ConfigureAwait(false);
            System.IO.File.Move(tmpPath, binPath, overwrite: true);

            _logger.LogDebug(
                "[GuestDevice] Frame pushed: guid={Guid} bytes={Bytes}",
                guid, frameBytes.Length);

            return NoContent();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Removes all guest device records for the same template except <paramref name="keepGuid"/>,
        /// then deletes their exchange-folder files.  Enforces the "one active guest device per
        /// template" invariant so <see cref="GuestDeviceRefreshWorker"/> never accumulates work
        /// for devices abandoned after a reflash.
        /// </summary>
        private async Task PruneStaleGuestDevicesAsync(
            int? templateId, string templateName, Guid keepGuid, CancellationToken ct)
        {
            IReadOnlyList<Guid> staleGuids = await _guestDevices
                .DeleteStaleForTemplateAsync(templateId, templateName, keepGuid, ct)
                .ConfigureAwait(false);

            if (staleGuids.Count == 0)
                return;

            _logger.LogInformation(
                "[GuestDevice] Pruned {Count} stale guest device(s) for template '{Name}': {Guids}",
                staleGuids.Count, templateName, string.Join(", ", staleGuids));

            DeleteExchangeFiles(staleGuids);
        }

        /// <summary>
        /// Attempts to delete the exchange-folder files associated with a list of abandoned
        /// device GUIDs.  Each device may have a single-frame <c>.bin</c> and/or a set of
        /// Class-C batch files (<c>{guid:N}_batch*.bin</c>, <c>{guid:N}_batch.json</c>).
        /// Errors are logged as warnings and swallowed — a missed file is harmless.
        /// </summary>
        private void DeleteExchangeFiles(IReadOnlyList<Guid> guids)
        {
            foreach (Guid staleGuid in guids)
            {
                // Single-frame file written by Chrome / PushFrame.
                // Chrome writes both a .bin (frame data) and a .png (debug preview) using
                // the default Guid.ToString() format (with dashes).  Delete both so stale
                // devices do not accumulate orphaned preview images on disk.
                TryDeleteFile(Path.Combine(_sharedConfig.ExchangeFolder, $"{staleGuid}.bin"));
                TryDeleteFile(Path.Combine(_sharedConfig.ExchangeFolder, $"{staleGuid}.bin.tmp"));
                TryDeleteFile(Path.Combine(_sharedConfig.ExchangeFolder, $"{staleGuid}.png"));

                // Class-C batch files: {guid:N}_batch_0.bin … _batch_N.bin + _batch.json
                string batchPrefix = staleGuid.ToString("N");
                try
                {
                    foreach (string file in Directory.GetFiles(
                        _sharedConfig.ExchangeFolder, $"{batchPrefix}_batch*"))
                    {
                        TryDeleteFile(file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[GuestDevice] Failed to enumerate batch files for stale device {Guid}",
                        staleGuid);
                }
            }
        }

        /// <summary>Deletes a single file if it exists; logs a warning on failure.</summary>
        private void TryDeleteFile(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "[GuestDevice] Failed to delete stale exchange file: {Path}", path);
            }
        }

        /// <summary>
        /// Opens an ephemeral browser tab for the template and enqueues a screenshot
        /// using the device GUID as the output token — Chrome writes the result to
        /// <c>ExchangeFolder/{guid}.bin</c>.  Errors are logged and swallowed so a
        /// transient browser hiccup never breaks device registration.
        /// </summary>
        /// <param name="templateId">
        /// DB primary key of the selected template.  When set the grab is routed through
        /// <c>SiteTemplateController</c> as <c>t{templateId}.html</c> — no file-system
        /// check needed since the controller serves DB templates dynamically.
        /// When <see langword="null"/> the legacy <c>SitesTemplates/</c> file path is used.
        /// </param>
        private async Task TriggerImmediateGrabAsync(
            Guid guid, string templateName, int? templateId, int width, int height, CancellationToken ct)
        {
            string grabUrl;

            if (templateId.HasValue)
            {
                // DB template — served dynamically by SiteTemplateController.
                // No file-system check needed; the controller returns 404 if the id is unknown.
                grabUrl = $"t{templateId.Value}.html";
            }
            else
            {
                // Legacy static file — verify it exists before opening a Chrome tab.
                string templatePath = Path.Combine(
                    _sharedConfig.ExchangeFolder, "SitesTemplates", templateName);

                if (!System.IO.File.Exists(templatePath))
                {
                    _logger.LogWarning(
                        "[GuestDevice] Template file not found, skipping immediate grab: {Path}",
                        templatePath);
                    return;
                }

                grabUrl = $"SitesTemplates/{templateName}";
            }

            try
            {
                LoadPageResultDTO loadResult =
                    await _grabber.AddUrlAsync(grabUrl).ConfigureAwait(false);

                if (!loadResult.Success)
                {
                    _logger.LogWarning(
                        "[GuestDevice] Browser failed to load template '{Name}': {Error}",
                        templateName, loadResult.ErrorMessage);
                    return;
                }

                var param = new ScreenShotParamDTO
                {
                    // Use the device GUID as the file token so Chrome writes {guid}.bin.
                    TokenGuid            = guid,
                    TabName              = loadResult.TabName,
                    Width                = width,
                    Height               = height,
                    CloseTabAfterCapture = true,
                    // GrabLogId, TemplateId, UserId left at defaults (0/null) — guest grab,
                    // no DB template record and no authenticated user.
                };

                await _grabChannel.EnqueueAsync(param, ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "[GuestDevice] Immediate grab enqueued for {Guid} → {Name} ({W}x{H})",
                    guid, templateName, width, height);
            }
            catch (Exception ex)
            {
                // A failed immediate grab is non-fatal: the GuestDeviceRefreshWorker will
                // produce the first .bin on its next 5-minute cycle.
                _logger.LogError(
                    ex,
                    "[GuestDevice] Immediate grab failed for {Guid} → {Name}", guid, templateName);
            }
        }
    }
}
