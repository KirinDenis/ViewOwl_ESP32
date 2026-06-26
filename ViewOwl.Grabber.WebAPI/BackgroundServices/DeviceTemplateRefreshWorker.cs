using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.DTO;
using ViewOwl.Grabber.Engine;
using ViewOwl.Grabber.WebAPI.Services;

namespace ViewOwl.Grabber.WebAPI.BackgroundServices
{
    /// <summary>
    /// Periodically re-grabs every HTML template that is currently assigned as the
    /// active template of at least one device.
    /// This keeps the on-display content fresh (clocks, weather, etc.) without requiring
    /// the user to manually trigger a grab from the admin panel.
    /// Runs every 5 minutes; GrabWorker executes the actual capture.
    /// </summary>
    /// <remarks>
    /// Template class grab strategies:
    /// Class A/B — single screenshot via <see cref="Chrome"/>.
    /// Class C   — multi-frame batch: one screenshot per <c>?vow_frame=N</c>, writes batch manifest.
    /// Skip optimisation: HTML SHA256 hash sidecar prevents re-render when template HTML is unchanged.
    /// See <see href="../../docs/templates/overview.md">docs/templates/overview.md</see>.
    /// </remarks>
    public sealed class DeviceTemplateRefreshWorker : BackgroundService
    {
        private readonly IGrabber _grabber;
        private readonly GrabChannel _grabChannel;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SharedConfig _sharedConfig;
        private readonly IFrameRefreshQueue _refreshQueue;
        private readonly DisplayTypeCatalog _catalog;
        private readonly ILogger<DeviceTemplateRefreshWorker> _logger;

        /// <summary>
        /// Initialises the worker with its collaborators.
        /// </summary>
        /// <param name="grabber">Headless browser used to open template tabs.</param>
        /// <param name="grabChannel">Shared channel that GrabWorker drains.</param>
        /// <param name="scopeFactory">Factory for creating scoped repository instances.</param>
        /// <param name="sharedOptions">Shared configuration (ExchangeFolder path).</param>
        /// <param name="refreshQueue">
        /// Singleton queue used to notify the UDP server that devices should re-download
        /// their frame on the next PING heartbeat.  Populated after each successful batch
        /// grab so Class-C devices receive an early AUTH trigger rather than waiting up to
        /// 5 minutes for the idle timer to expire.
        /// </param>
        /// <param name="catalog">
        /// Display-type registry used to resolve the per-device output frame format
        /// (BGR565 for LCDs, 1-bit monochrome for e-paper).
        /// </param>
        /// <param name="logger">Logger.</param>
        public DeviceTemplateRefreshWorker(
            IGrabber grabber,
            GrabChannel grabChannel,
            IServiceScopeFactory scopeFactory,
            IOptions<SharedConfig> sharedOptions,
            IFrameRefreshQueue refreshQueue,
            DisplayTypeCatalog catalog,
            ILogger<DeviceTemplateRefreshWorker> logger)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            _grabber      = grabber;
            _grabChannel  = grabChannel;
            _scopeFactory = scopeFactory;
            _sharedConfig = sharedOptions.Value;
            _refreshQueue = refreshQueue;
            _catalog      = catalog;
            _logger       = logger;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Short initial delay to let the browser process warm up fully before
                // the first capture cycle.  5 s is sufficient; the previous 30 s caused
                // a long window on server restart where devices received
                // "AUTH rejected: template assigned but no frame captured yet".
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

                while (!stoppingToken.IsCancellationRequested)
                {
                    await RefreshAssignedTemplatesAsync(stoppingToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — stoppingToken was cancelled. Not an error.
            }
        }

        // ── Private implementation ────────────────────────────────────────────

        /// <summary>
        /// Loads all templates that need refreshing: those assigned to devices plus all public
        /// templates (<see cref="Template.IsPublic"/> = <c>true</c>).  The union is deduplicated
        /// by template ID so a template that is both assigned and public is only captured once.
        /// Public templates are included so guest devices flashed from the landing page always
        /// find a ready frame — no AUTH-rejected delay on first connection.
        /// </summary>
        private async Task RefreshAssignedTemplatesAsync(CancellationToken ct)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var templateRepo = scope.ServiceProvider.GetRequiredService<ITemplateRepository>();
            var grabLogRepo  = scope.ServiceProvider.GetRequiredService<IGrabLogRepository>();
            var deviceRepo   = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();

            IReadOnlyList<Template> templates;
            try
            {
                IReadOnlyList<Template> assigned = await templateRepo
                    .GetAssignedToDevicesAsync(ct).ConfigureAwait(false);
                IReadOnlyList<Template> publicTemplates = await templateRepo
                    .GetPublicAsync(ct).ConfigureAwait(false);

                // Union: assigned + public, deduplicated by Id.
                var seen   = new HashSet<int>(assigned.Count + publicTemplates.Count);
                var merged = new List<Template>(assigned.Count + publicTemplates.Count);
                foreach (Template t in assigned)
                    if (seen.Add(t.Id)) merged.Add(t);
                foreach (Template t in publicTemplates)
                    if (seen.Add(t.Id)) merged.Add(t);
                templates = merged;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DeviceTemplateRefreshWorker] Failed to load templates for refresh");
                return;
            }

            if (templates.Count == 0)
                return;

            _logger.LogInformation(
                "[DeviceTemplateRefreshWorker] Refreshing {Count} template(s) (assigned + public)", templates.Count);

            string sitesFolder = Path.Combine(_sharedConfig.ExchangeFolder, "SitesTemplates");
            Directory.CreateDirectory(sitesFolder);

            foreach (Template template in templates)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await RefreshOneTemplateAsync(template, grabLogRepo, deviceRepo, sitesFolder, ct)
                        .ConfigureAwait(false);

                    // Pace captures so the browser is not overwhelmed when many templates exist
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
                        "[DeviceTemplateRefreshWorker] Error refreshing template {TemplateId} ({Name})",
                        template.Id, template.Name);
                }
            }
        }

        /// <summary>
        /// Writes the template HTML to disk, determines all unique display resolutions
        /// used by devices assigned to this template, and enqueues one capture per
        /// unique resolution so every device receives a frame rendered at its native size.
        /// </summary>
        private async Task RefreshOneTemplateAsync(
            Template template,
            IGrabLogRepository grabLogRepo,
            IDeviceRepository deviceRepo,
            string sitesFolder,
            CancellationToken ct)
        {
            string htmlFileName = $"t{template.Id}.html";
            string htmlFilePath = Path.Combine(sitesFolder, htmlFileName);
            await File.WriteAllTextAsync(htmlFilePath, template.HtmlContent, ct).ConfigureAwait(false);

            // Parse data-vow-* metadata to decide grab strategy.
            TemplateVowMetadata meta = TemplateMetadataParser.Parse(template.HtmlContent);

            // Collect the distinct (width, height) pairs reported by every device that
            // currently has this template active.  Devices that have never sent a
            // hardware-info heartbeat (DisplayWidth/Height still null) are excluded and
            // will fall back to the last known GrabLog dimensions on GetFrame.
            IReadOnlyList<Device> assignedDevices =
                await deviceRepo.GetByActiveTemplateIdAsync(template.Id, ct).ConfigureAwait(false);

            // Group by (width, height, frameFormat) so an e-paper device (mono1bit) and an
            // LCD device (bgr565) at the same resolution each get a frame in their own format.
            // The format is template-aware: a template may request an alternate output format
            // via data-vow-render (e.g. "mono-CGA" → mono4gray).  The request is honoured only
            // when the device's display type supports it; otherwise the device falls back to its
            // default display-type format from the registry.
            string? requestedFormat = meta.RenderFormat;
            var resolutions = assignedDevices
                .Where(d => d.DisplayWidth.HasValue && d.DisplayHeight.HasValue)
                .Select(d => (
                    W: d.DisplayWidth!.Value,
                    H: d.DisplayHeight!.Value,
                    Format: requestedFormat is not null
                            && _catalog.IsFormatSupported(d.DisplayType, requestedFormat)
                        ? requestedFormat
                        : _catalog.GetFrameFormat(d.DisplayType)))
                .Distinct()
                .ToList();

            // If no device has reported dimensions yet, fall back to the last successful
            // grab dimensions so the template is still refreshed on every cycle.
            // The fallback uses an empty format so ScreenShotParamDTO keeps its BGR565 default.
            if (resolutions.Count == 0)
            {
                GrabLog? lastLog =
                    await grabLogRepo.GetLastSuccessfulAsync(template.Id, ct).ConfigureAwait(false);
                resolutions.Add((W: lastLog?.Width ?? 480, H: lastLog?.Height ?? 320, Format: string.Empty));
            }

            foreach ((int width, int height, string format) in resolutions)
            {
                if (ct.IsCancellationRequested)
                    break;

                // Prune stale files BEFORE creating a new entry so the exchange folder
                // stays bounded regardless of how many cycles have run.
                // Class-C templates keep 6 slots instead of 4 so batch manifests survive
                // a few consecutive single-frame dashboard grabs without being pruned.
                // A single-frame grab (Save & Grab) displaces the oldest batch; with 6
                // slots up to 5 single-frame grabs can occur before the last batch is lost.
                int grabKeepCount = meta.VowClass == 'C' ? 6 : 4;
                await PruneStaleGrabsAsync(grabLogRepo, template.Id, width, height, grabKeepCount, ct)
                    .ConfigureAwait(false);

                if (meta.VowClass == 'C')
                {
                    // Skip re-render when the HTML has not changed since the last
                    // successful batch grab.  Class-C templates are pure animation
                    // (no real-time data), so re-rendering with an unchanged template
                    // only produces slightly different binary output due to Chrome
                    // non-determinism — yielding a new CRC that forces unnecessary
                    // BATCH re-downloads on every device that has the template.
                    // The hash sidecar file is written after each successful render and
                    // deleted implicitly when the exchange folder is cleaned up.
                    string hashSidecarPath = Path.Combine(
                        _sharedConfig.ExchangeFolder,
                        $"t{template.Id}_{width}x{height}.htmlhash");

                    if (await IsClassCUpToDateAsync(
                            template, grabLogRepo, hashSidecarPath, width, height, ct)
                        .ConfigureAwait(false))
                    {
                        _logger.LogInformation(
                            "[DeviceTemplateRefreshWorker] Class-C template {TemplateId} ({Name}) " +
                            "HTML unchanged at {W}x{H} — skipping re-render",
                            template.Id, template.Name, width, height);
                        continue;
                    }

                    await GrabMultiframeAtResolutionAsync(
                        template, meta, grabLogRepo, width, height, format, ct, hashSidecarPath)
                        .ConfigureAwait(false);
                }
                else
                {
                    await GrabAtResolutionAsync(
                        template, grabLogRepo, sitesFolder, htmlFileName, width, height, format, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        // ── Class-C dedup helpers ─────────────────────────────────────────────

        /// <summary>
        /// Returns the SHA-256 hex digest of the template HTML content encoded as UTF-8.
        /// Used to detect whether the HTML has changed since the last batch render so that
        /// unchanged Class-C templates are not re-rendered on every 5-minute cycle.
        /// </summary>
        private static string ComputeHtmlHash(string html)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(html));
            return Convert.ToHexString(digest);
        }

        /// <summary>
        /// Computes standard CRC32 (zlib variant: init=0xFFFFFFFF, polynomial=0xEDB88320,
        /// finalXOR=0xFFFFFFFF) over the concatenated bytes of all frame files in order.
        /// This matches the value the ESP32 firmware accumulates via
        /// <c>esp_rom_crc32_le(running, buf, len)</c> starting from 0 across every DATA
        /// chunk: the ROM helper inverts the seed on entry and the result on exit, so a
        /// chained call sequence equals one standard CRC32 over the concatenation.
        /// The previous init=0/no-finalXOR variant never matched the firmware value,
        /// which made the NOT_MODIFIED short-circuit dead (full batch re-download every
        /// idle cycle).
        /// Stored in the batch manifest so the UDP server can issue NOT_MODIFIED
        /// after a server restart without re-reading all frame files.
        /// </summary>
        private static uint ComputeBatchCrcFromFiles(string[] framePaths)
        {
            uint crc = 0xFFFFFFFF;
            foreach (string path in framePaths)
            {
                byte[] data = File.ReadAllBytes(path);
                foreach (byte b in data)
                {
                    crc ^= b;
                    for (int i = 0; i < 8; i++)
                        crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }
            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>
        /// Returns <c>true</c> when the Class-C template at the given resolution does not need
        /// to be re-rendered: the HTML content matches the hash stored in the sidecar file from
        /// the previous render cycle AND the batch manifest for the last successful grab still
        /// exists on disk (i.e. the files have not been pruned away).
        /// </summary>
        private async Task<bool> IsClassCUpToDateAsync(
            Template template,
            IGrabLogRepository grabLogRepo,
            string hashFilePath,
            int width,
            int height,
            CancellationToken ct)
        {
            // No sidecar → never rendered yet (or hash file was deleted).
            if (!File.Exists(hashFilePath))
                return false;

            string storedHash = await File.ReadAllTextAsync(hashFilePath, ct).ConfigureAwait(false);
            if (!string.Equals(storedHash, ComputeHtmlHash(template.HtmlContent), StringComparison.Ordinal))
                return false;

            // Hash matches — verify that the batch manifest is still on disk.
            GrabLog? last =
                await grabLogRepo.GetLastSuccessfulAsync(template.Id, width, height, ct)
                                 .ConfigureAwait(false);
            if (last is null)
                return false;

            string manifestPath = Path.Combine(
                _sharedConfig.ExchangeFolder,
                last.TokenGuid.ToString("N") + "_batch.json");

            if (!File.Exists(manifestPath))
                return false;

            // Manifests written before v1.2.34 have BatchCrc=0, and manifests written
            // before the CRC-variant fix (CrcVersion < 2) carry an init=0/no-finalXOR
            // CRC that can never match the firmware's standard CRC32 — both make the
            // NOT_MODIFIED short-circuit dead and cause a full BATCH re-download every
            // idle cycle.  Force a re-render so the new manifest gets a standard CRC.
            try
            {
                string manifestJson = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
                BatchManifest? m = JsonSerializer.Deserialize<BatchManifest>(manifestJson);
                return m is { BatchCrc: not 0, CrcVersion: >= 2 };
            }
            catch
            {
                // Unreadable manifest — re-render to generate a fresh one.
                return false;
            }
        }

        /// <summary>
        /// Captures all animation frames for a Class-C template at the specified resolution,
        /// writes a batch manifest JSON sidecar, and creates a <c>GrabLog</c> row so the
        /// UDP Server can resolve the batch on the next HELLO.
        /// On success, writes the SHA-256 hash of the template HTML to
        /// <paramref name="htmlHashSidecarPath"/> so future cycles can skip re-rendering
        /// when the content has not changed.
        /// </summary>
        private async Task GrabMultiframeAtResolutionAsync(
            Template template,
            TemplateVowMetadata meta,
            IGrabLogRepository grabLogRepo,
            int width,
            int height,
            string frameFormat,
            CancellationToken ct,
            string? htmlHashSidecarPath = null)
        {
            // Empty format (no-device fallback path) means "use the BGR565 default".
            string converterId = string.IsNullOrEmpty(frameFormat) ? "bgr565" : frameFormat;

            Guid tokenGuid = Guid.NewGuid();

            // Base file path: {ExchangeFolder}/{guid}
            // Chrome writes  {ExchangeFolder}/{guid}_batch_0.bin … _batch_N.bin
            string basePath = Path.Combine(_sharedConfig.ExchangeFolder, tokenGuid.ToString("N"));

            // Build the URL the same way GrabAtResolutionAsync does — local template via WebAPI.
            string htmlFileName = $"t{template.Id}.html";
            string templateUrl  = $"SitesTemplates/{htmlFileName}";

            string[] framePaths;
            try
            {
                framePaths = await _grabber
                    .TakeMultiframeScreenshotAsync(
                        templateUrl, meta.FrameCount, width, height, basePath, converterId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[DeviceTemplateRefreshWorker] Multiframe grab failed for template {TemplateId} ({Name}) {W}x{H}",
                    template.Id, template.Name, width, height);
                return;
            }

            // Compute standard CRC32 over all frame bytes — same value the ESP32
            // accumulates via esp_rom_crc32_le across every DATA chunk it receives.
            // Stored in the manifest so the UDP server can verify NOT_MODIFIED after a restart
            // without re-reading the frame files.
            uint batchCrc = ComputeBatchCrcFromFiles(framePaths);

            // Write batch manifest so the UDP Server knows to use BATCH_START/BATCH_COMMIT.
            string manifestPath = basePath + "_batch.json";
            var manifest = new BatchManifest(
                meta.FrameCount, (byte)meta.Fps, framePaths, batchCrc, CrcVersion: 2);
            await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(manifest),
                    ct)
                .ConfigureAwait(false);

            // Persist a GrabLog so GetLastSuccessfulAsync returns this batch's tokenGuid.
            // Width/Height are stored so resolution-aware GetFrame can match the right log.
            GrabLog log = await grabLogRepo
                .CreateAsync(template.Id, tokenGuid, width, height, ct)
                .ConfigureAwait(false);

            await grabLogRepo
                .MarkCompletedAsync(log.Id, success: true, message: null, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "[DeviceTemplateRefreshWorker] Class-C multiframe grab complete: template {TemplateId} ({Name}), " +
                "{FrameCount} frames at {W}x{H} format={Format}, token={TokenGuid}, batchCrc=0x{BatchCrc:X8}",
                template.Id, template.Name, framePaths.Length, width, height, converterId, tokenGuid, batchCrc);

            // Persist the HTML hash so subsequent 5-minute cycles can detect that the content
            // has not changed and skip the Chrome re-render.  Written after a successful grab
            // so a partial/failed render does not leave a stale hash.
            if (htmlHashSidecarPath is not null)
            {
                try
                {
                    await File.WriteAllTextAsync(
                        htmlHashSidecarPath,
                        ComputeHtmlHash(template.HtmlContent),
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Non-fatal: the next cycle will re-render as if HTML changed.
                    _logger.LogWarning(
                        ex,
                        "[DeviceTemplateRefreshWorker] Failed to write HTML hash sidecar '{Path}'",
                        htmlHashSidecarPath);
                }
            }

            // Schedule an early AUTH trigger for all devices using this template so they
            // receive the new batch frames without waiting up to 5 minutes for their idle
            // timer to fire.  Mirrors what GrabWorker does for Class-A/B after a single-frame grab.
            await ScheduleRefreshForTemplateAsync(template.Id, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Enqueues a frame-refresh notification for every device whose active template is
        /// <paramref name="templateId"/>.  The UDP Server's push loop picks these up on the
        /// next 500 ms poll and sends an early AUTH trigger so the device re-downloads
        /// the new frame immediately instead of waiting for its idle timer.
        /// Errors are logged and swallowed — a missed notification is non-fatal.
        /// </summary>
        private async Task ScheduleRefreshForTemplateAsync(int templateId, CancellationToken ct)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var deviceRepo =
                    scope.ServiceProvider.GetRequiredService<IDeviceRepository>();

                IReadOnlyList<Device> devices =
                    await deviceRepo.GetByActiveTemplateIdAsync(templateId, ct)
                                    .ConfigureAwait(false);

                foreach (Device d in devices)
                    _refreshQueue.ScheduleRefresh(d.Id);

                if (devices.Count > 0)
                    _logger.LogInformation(
                        "[DeviceTemplateRefreshWorker] Scheduled frame refresh for {Count} device(s) " +
                        "on template {TemplateId}",
                        devices.Count, templateId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[DeviceTemplateRefreshWorker] ScheduleRefreshForTemplateAsync failed " +
                    "for template {TemplateId}",
                    templateId);
            }
        }

        /// <summary>
        /// Opens a browser tab for <paramref name="htmlFileName"/>, creates a new
        /// <c>GrabLog</c> at the specified resolution, and enqueues the capture.
        /// GrabWorker takes it from there.
        /// </summary>
        private async Task GrabAtResolutionAsync(
            Template template,
            IGrabLogRepository grabLogRepo,
            string sitesFolder,
            string htmlFileName,
            int width,
            int height,
            string frameFormat,
            CancellationToken ct)
        {
            _ = sitesFolder; // HTML already written by caller; parameter kept for logging clarity

            LoadPageResultDTO loadResult =
                await _grabber.AddUrlAsync($"SitesTemplates/{htmlFileName}").ConfigureAwait(false);

            if (!loadResult.Success)
            {
                _logger.LogWarning(
                    "[DeviceTemplateRefreshWorker] Browser failed to load template {TemplateId} ({Name}): {Error}",
                    template.Id, template.Name, loadResult.ErrorMessage);
                return;
            }

            Guid tokenGuid = Guid.NewGuid();

            // A new GrabLog row means GetLastSuccessfulAsync will return the fresh frame
            // once GrabWorker marks it as successful.
            GrabLog log = await grabLogRepo
                .CreateAsync(template.Id, tokenGuid, width, height, ct)
                .ConfigureAwait(false);

            var param = new ScreenShotParamDTO
            {
                TokenGuid            = tokenGuid,
                TabName              = loadResult.TabName,
                Width                = width,
                Height               = height,
                GrabLogId            = log.Id,
                TemplateId           = template.Id,
                UserId               = template.UserId,
                CloseTabAfterCapture = true,
                Monochrome           = template.Monochrome,
            };

            // Select the output pixel format from the device's display type. When empty
            // (no-device fallback path) keep the DTO's BGR565 default unchanged.
            if (!string.IsNullOrEmpty(frameFormat))
            {
                param.ConverterId = frameFormat;
            }

            await _grabChannel.EnqueueAsync(param, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "[DeviceTemplateRefreshWorker] Queued refresh for template {TemplateId} ({Name}), " +
                "token={TokenGuid} resolution={W}x{H} format={Format}",
                template.Id, template.Name, tokenGuid, width, height, param.ConverterId);
        }

        /// <summary>
        /// Deletes exchange-folder files and GrabLog rows for all successful grabs beyond
        /// the most recent <paramref name="keepCount"/> for the given
        /// templateId × width × height pair.
        /// Class-C grabs (identified by a <c>_batch.json</c> sidecar) have their batch
        /// frame files removed; Class-A/B grabs have their <c>.bin</c> and <c>.png</c>
        /// removed.  Invoked before each new grab so the exchange folder stays bounded.
        /// </summary>
        private async Task PruneStaleGrabsAsync(
            IGrabLogRepository grabLogRepo,
            int templateId, int width, int height,
            int keepCount,
            CancellationToken ct)
        {
            IReadOnlyList<GrabLog> stale =
                await grabLogRepo
                    .GetStaleSuccessfulAsync(templateId, width, height, keepCount, ct)
                    .ConfigureAwait(false);

            foreach (GrabLog log in stale)
            {
                // Class C uses ToString("N") (no hyphens) — matches DeviceTemplateRefreshWorker
                // naming in GrabMultiframeAtResolutionAsync.
                // Class A/B uses the default Guid format (with hyphens) — matches Chrome.cs output.
                string guidN     = log.TokenGuid.ToString("N");
                string guidD     = log.TokenGuid.ToString("D");
                string batchJson = Path.Combine(_sharedConfig.ExchangeFolder, $"{guidN}_batch.json");

                if (File.Exists(batchJson))
                {
                    // Class C — enumerate frame paths from the manifest, then delete the manifest.
                    try
                    {
                        string json = await File.ReadAllTextAsync(batchJson, ct).ConfigureAwait(false);
                        BatchManifest? manifest = JsonSerializer.Deserialize<BatchManifest>(json);
                        if (manifest?.FramePaths is { Length: > 0 })
                        {
                            foreach (string framePath in manifest.FramePaths)
                            {
                                if (File.Exists(framePath))
                                    File.Delete(framePath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Manifest unreadable — fall back to glob-based cleanup.
                        _logger.LogWarning(
                            ex,
                            "[DeviceTemplateRefreshWorker] Could not read batch manifest for cleanup: {Path}",
                            batchJson);

                        foreach (string f in Directory.EnumerateFiles(
                            _sharedConfig.ExchangeFolder, $"{guidN}_batch_*.bin"))
                        {
                            File.Delete(f);
                        }
                    }

                    File.Delete(batchJson);
                }
                else
                {
                    // Class A/B — delete .bin and .png.
                    string binPath = Path.Combine(_sharedConfig.ExchangeFolder, $"{guidD}.bin");
                    string pngPath = Path.Combine(_sharedConfig.ExchangeFolder, $"{guidD}.png");
                    if (File.Exists(binPath)) File.Delete(binPath);
                    if (File.Exists(pngPath)) File.Delete(pngPath);
                }

                await grabLogRepo.DeleteAsync(log.Id, ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "[DeviceTemplateRefreshWorker] Pruned stale grab: template {TemplateId} token={TokenGuid}",
                    templateId, log.TokenGuid);
            }
        }
    }

    // ── Batch manifest ────────────────────────────────────────────────────────

    /// <summary>
    /// Sidecar JSON written alongside Class-C frame bins.
    /// Path: <c>{ExchangeFolder}/{tokenGuid}_batch.json</c>.
    /// The UDP Server reads this on HELLO to decide whether to use
    /// the BATCH_START/BATCH_COMMIT transfer protocol.
    /// </summary>
    internal sealed record BatchManifest(
        /// <summary>Total number of animation frames.</summary>
        int FrameCount,
        /// <summary>Playback speed in frames per second (1–30).</summary>
        byte Fps,
        /// <summary>Absolute paths to the compressed frame bin files, in order.</summary>
        string[] FramePaths,
        /// <summary>
        /// Standard CRC32 (zlib variant) computed over the concatenated bytes of all frame
        /// files in transmission order — identical to the value accumulated by the ESP32
        /// firmware via <c>esp_rom_crc32_le</c> across every DATA chunk.
        /// Stored so the UDP server can issue NOT_MODIFIED after a restart without re-reading
        /// the frame files.  0 in manifests written before this field was added (treated as
        /// "unknown" and falls back to a full BATCH transfer).
        /// </summary>
        uint BatchCrc = 0,
        /// <summary>
        /// CRC algorithm version.  0/1 = legacy init=0/no-finalXOR variant (never matches
        /// the firmware value); 2 = standard CRC32.  The up-to-date check in the refresh
        /// worker forces a re-render for manifests below version 2 so stale CRCs self-heal.
        /// </summary>
        int CrcVersion = 0);
}
