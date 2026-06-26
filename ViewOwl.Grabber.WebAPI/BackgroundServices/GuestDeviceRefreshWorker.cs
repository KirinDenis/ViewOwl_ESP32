using System.Text.Json;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.DTO;
using ViewOwl.Grabber.Engine;

namespace ViewOwl.Grabber.WebAPI.BackgroundServices
{
    /// <summary>
    /// Periodically re-grabs the assigned template for every registered guest device so
    /// dynamic content (clocks, weather, etc.) stays current even without a user account.
    /// Class-C (animated) templates are captured as multi-frame batches so the device
    /// plays the full animation loop rather than a frozen single frame.
    /// Runs every 5 minutes, in step with <see cref="DeviceTemplateRefreshWorker"/>.
    /// </summary>
    public sealed class GuestDeviceRefreshWorker : BackgroundService
    {
        private readonly IGrabber _grabber;
        private readonly GrabChannel _grabChannel;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SharedConfig _sharedConfig;
        private readonly DisplayTypeCatalog _catalog;
        private readonly ILogger<GuestDeviceRefreshWorker> _logger;

        /// <summary>
        /// Initialises the worker with its collaborators.
        /// </summary>
        public GuestDeviceRefreshWorker(
            IGrabber grabber,
            GrabChannel grabChannel,
            IServiceScopeFactory scopeFactory,
            IOptions<SharedConfig> sharedOptions,
            DisplayTypeCatalog catalog,
            ILogger<GuestDeviceRefreshWorker> logger)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            _grabber      = grabber;
            _grabChannel  = grabChannel;
            _scopeFactory = scopeFactory;
            _sharedConfig = sharedOptions.Value;
            _catalog      = catalog;
            _logger       = logger;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Short initial delay so Chrome is fully initialised before the first cycle.
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

                while (!stoppingToken.IsCancellationRequested)
                {
                    await RefreshAllGuestDevicesAsync(stoppingToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path — not an error.
            }
        }

        // ── Private implementation ────────────────────────────────────────────

        /// <summary>
        /// Loads all registered guest devices from the database, resolves the template
        /// metadata for each DB-backed device, and refreshes one device at a time.
        /// </summary>
        private async Task RefreshAllGuestDevicesAsync(CancellationToken ct)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var guestDeviceRepo = scope.ServiceProvider.GetRequiredService<IGuestDeviceRepository>();
            var templateRepo    = scope.ServiceProvider.GetRequiredService<ITemplateRepository>();

            IReadOnlyList<GuestDevice> devices;
            try
            {
                devices = await guestDeviceRepo.GetAllAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GuestDeviceRefreshWorker] Failed to load guest devices");
                return;
            }

            if (devices.Count == 0)
                return;

            _logger.LogInformation(
                "[GuestDeviceRefreshWorker] Refreshing {Count} guest device(s)", devices.Count);

            foreach (GuestDevice device in devices)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    // Load the DB template for devices that have one, so we can detect
                    // Class-C and perform a multiframe grab instead of a single-frame one.
                    Template? template = device.TemplateId.HasValue
                        ? await templateRepo.GetByIdAsync(device.TemplateId.Value, ct)
                                            .ConfigureAwait(false)
                        : null;

                    await RefreshOneDeviceAsync(device, template, ct).ConfigureAwait(false);

                    // Pace captures so the browser is not overwhelmed when many devices exist.
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[GuestDeviceRefreshWorker] Error refreshing guest device {Guid} (template: {Name})",
                        device.Guid, device.TemplateName);
                }
            }
        }

        /// <summary>
        /// Captures a fresh frame (or frame batch) for a single guest device and marks
        /// the device's <c>LastGrabbedAt</c> timestamp in the database.
        /// When <paramref name="template"/> is a Class-C template the full animation
        /// batch is captured via <see cref="GrabMultiframeForGuestAsync"/> and a
        /// <c>_batch.json</c> sidecar is written so the UDP server uses BATCH_START/COMMIT.
        /// All other templates fall back to the existing single-frame path.
        /// </summary>
        private async Task RefreshOneDeviceAsync(
            GuestDevice device, Template? template, CancellationToken ct)
        {
            string grabUrl;

            if (device.TemplateId.HasValue)
            {
                // DB template — SiteTemplateController serves it dynamically as t{id}.html.
                // No file-system check required.
                grabUrl = $"t{device.TemplateId.Value}.html";
            }
            else
            {
                // Legacy static file — skip if the .html file has been removed from disk.
                string templatePath = Path.Combine(
                    _sharedConfig.ExchangeFolder, "SitesTemplates", device.TemplateName);

                if (!System.IO.File.Exists(templatePath))
                {
                    _logger.LogWarning(
                        "[GuestDeviceRefreshWorker] Template file missing for guest device {Guid}: {Path}",
                        device.Guid, templatePath);
                    return;
                }

                grabUrl = $"SitesTemplates/{device.TemplateName}";
            }

            // Resolve the output frame format. Guest devices store only their resolution (no
            // display-type name like Device.DisplayType), so the display type — and thus the
            // default frame format (e.g. mono1bit for a 400x300 e-paper) — is looked up by
            // resolution. A template may request an alternate format via data-vow-render
            // (e.g. "mono-CGA" → mono4gray), honoured only when the resolved display type
            // supports it. Mirrors DeviceTemplateRefreshWorker so guest e-paper devices get
            // mono frames instead of the BGR565 default.
            string? displayTypeName =
                _catalog.GetProtocolNameByResolution(device.DisplayWidth, device.DisplayHeight);
            string frameFormat = _catalog.GetFrameFormat(displayTypeName);

            // Class-C template: capture all animation frames and write a batch manifest.
            // GetFrame (InternalController) will serve the manifest path so the UDP server
            // uses the BATCH_START/BATCH_COMMIT protocol, enabling on-device animation.
            if (template is not null)
            {
                TemplateVowMetadata meta = TemplateMetadataParser.Parse(template.HtmlContent);
                if (meta.RenderFormat is not null
                    && _catalog.IsFormatSupported(displayTypeName, meta.RenderFormat))
                {
                    frameFormat = meta.RenderFormat;
                }

                if (meta.VowClass == 'C')
                {
                    await GrabMultiframeForGuestAsync(device, meta, grabUrl, frameFormat, ct)
                        .ConfigureAwait(false);
                    await MarkGrabbedAsync(device.Id, ct).ConfigureAwait(false);
                    return;
                }
            }

            // Single-frame path for Class-A / Class-B templates and legacy static templates.
            LoadPageResultDTO loadResult =
                await _grabber.AddUrlAsync(grabUrl)
                              .ConfigureAwait(false);

            if (!loadResult.Success)
            {
                _logger.LogWarning(
                    "[GuestDeviceRefreshWorker] Browser failed to load '{Name}' for device {Guid}: {Error}",
                    device.TemplateName, device.Guid, loadResult.ErrorMessage);
                return;
            }

            var param = new ScreenShotParamDTO
            {
                // Use the device GUID as the file token so Chrome writes {guid}.bin —
                // the same path the UDP server looks up when the device sends HELLO.
                TokenGuid            = device.Guid,
                TabName              = loadResult.TabName,
                Width                = device.DisplayWidth,
                Height               = device.DisplayHeight,
                CloseTabAfterCapture = true,
                // Output format resolved from the device resolution (mono for e-paper, BGR565
                // for LCDs) plus any supported template override; the DTO default is BGR565.
                ConverterId          = frameFormat,
                // GrabLogId, TemplateId, UserId left at defaults — guest grab has no DB template.
            };

            await _grabChannel.EnqueueAsync(param, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "[GuestDeviceRefreshWorker] Queued grab for guest device {Guid} → {Name} ({W}x{H}, {Format})",
                device.Guid, device.TemplateName, device.DisplayWidth, device.DisplayHeight, frameFormat);

            await MarkGrabbedAsync(device.Id, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Captures all animation frames for a Class-C guest device template at the device's
        /// native resolution and writes a batch manifest JSON sidecar.
        /// Unlike <see cref="DeviceTemplateRefreshWorker"/>, guest devices use their fixed
        /// <see cref="GuestDevice.Guid"/> as the base token — no <c>GrabLog</c> row is
        /// created, and files are overwritten in-place on every cycle.
        /// </summary>
        private async Task GrabMultiframeForGuestAsync(
            GuestDevice device,
            TemplateVowMetadata meta,
            string templateUrl,
            string frameFormat,
            CancellationToken ct)
        {
            // Base path: {ExchangeFolder}/{guid:N}
            // Chrome writes: {basePath}_batch_0.bin … _batch_N.bin
            // Manifest:      {basePath}_batch.json
            string basePath = Path.Combine(_sharedConfig.ExchangeFolder, device.Guid.ToString("N"));

            string[] framePaths;
            try
            {
                framePaths = await _grabber
                    .TakeMultiframeScreenshotAsync(
                        templateUrl, meta.FrameCount,
                        device.DisplayWidth, device.DisplayHeight,
                        basePath, frameFormat, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[GuestDeviceRefreshWorker] Multiframe grab failed for guest device {Guid} → {Url}",
                    device.Guid, templateUrl);
                return;
            }

            // Write (or overwrite) the batch manifest so GetFrame returns it on the next HELLO.
            string manifestPath = basePath + "_batch.json";
            var manifest = new BatchManifest(meta.FrameCount, (byte)meta.Fps, framePaths);
            await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(manifest),
                    ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "[GuestDeviceRefreshWorker] Class-C multiframe grab complete: guest device {Guid}, " +
                "{FrameCount} frames at {W}x{H} ({Format})",
                device.Guid, framePaths.Length, device.DisplayWidth, device.DisplayHeight, frameFormat);
        }

        /// <summary>
        /// Updates <c>LastGrabbedAt</c> for the guest device after a successful grab.
        /// This timestamp reflects scheduling time, not write-completion time — acceptable for monitoring.
        /// </summary>
        private async Task MarkGrabbedAsync(int deviceId, CancellationToken ct)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IGuestDeviceRepository>();
                await repo.MarkGrabbedAsync(deviceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Non-fatal: monitoring timestamp only.
                _logger.LogWarning(
                    ex,
                    "[GuestDeviceRefreshWorker] MarkGrabbedAsync failed for device {Id}", deviceId);
            }
        }
    }
}
