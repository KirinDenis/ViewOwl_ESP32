using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PuppeteerSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ViewOwl.Config;
using ViewOwl.DTO;
using ViewOwl.Grabber.Engine.Compression;
using ViewOwl.Grabber.Engine.Converters;

namespace ViewOwl.Grabber.Engine
{
    /// <summary>
    /// Headless Chromium implementation of <see cref="IGrabber"/> and <see cref="IScreenshotCapture"/>.
    /// Responsible for browser lifecycle and screenshot capture only.
    /// Tab management is delegated to <see cref="TabManager"/>.
    /// Pixel-format conversion is delegated to <see cref="IImageConverter"/> implementations.
    /// </summary>
    public sealed class Chrome : IGrabber, IScreenshotCapture, IAsyncDisposable
    {
        // Base CSS injected before every capture. pixelated rendering avoids blur artefacts
        // when Chromium downscales high-DPI content to the 480×320 viewport.
        private const string BaseStyleInjection =
            "* { image-rendering: pixelated !important; }";

        // Additional CSS appended when ScreenShotParamDTO.Monochrome == true.
        private const string MonochromeStyleInjection =
            "html { filter: grayscale(1) !important; }";

        private static readonly ViewPortOptions DefaultViewport =
            new() { Width = 480, Height = 320 };

        private IBrowser? _browser;

        // CRC32 of the last compressed frame written per device token.
        // In-memory only — intentionally resets on server restart so the first grab
        // after a reboot always writes the file and triggers a device refresh.
        private readonly ConcurrentDictionary<Guid, uint> _frameCrcCache = new();

        // Raw BGR565 pixels of the previous grab per device — used as the delta base.
        // Reset on server restart so the first grab after reboot always writes a full frame.
        private readonly ConcurrentDictionary<Guid, byte[]> _prevRawCache = new();

        // CRC32(init=0) of the previous .bin file per device — matches esp_rom_crc32_le(0,...).
        // Embedded in the delta header as base_crc so the device can verify it holds the right base.
        private readonly ConcurrentDictionary<Guid, uint> _prevBinEsp32CrcCache = new();

        // How many CRC-named delta files to keep per device in the exchange folder.
        // Keeping several allows a device that is multiple grab-cycles behind to still find
        // a matching delta and avoid downloading the full 307 KB frame.
        // 20 files × ~2 s/grab ≈ 40 s coverage — more than enough to outlast a full 307 KB
        // delivery (~14–15 s) even when the grab cycle is faster than the delivery cycle.
        private const int DeltaFileKeepCount = 20;

        // Serialises screenshot capture — Chromium must not be screenshotted concurrently
        private readonly SemaphoreSlim _screenshotLock = new(1, 1);

        private readonly SemaphoreSlim _reinitLock = new(1, 1);

        // UTC ticks of the last successful screenshot or browser (re)launch. Updated on every
        // successful grab and on every InitAsync so a freshly recycled browser starts with a
        // fresh window. Read by the watchdog via LastSuccessfulGrabUtc. Accessed with
        // Interlocked because the writer (capture thread) and reader (watchdog) run concurrently.
        private long _lastSuccessfulGrabTicks = DateTime.UtcNow.Ticks;

        private readonly TabManager _tabManager;
        private readonly IEnumerable<IImageConverter> _converters;
        private readonly SharedConfig _sharedConfig;

        /// <summary>
        /// Initialises the grabber with its collaborators.
        /// </summary>
        /// <param name="tabManager">Manages open browser tabs.</param>
        /// <param name="converters">All registered pixel-format converters.</param>
        /// <param name="sharedOptions">Shared configuration (ExchangeFolder, WebApiBaseUrl).</param>
        public Chrome(
            TabManager tabManager,
            IEnumerable<IImageConverter> converters,
            IOptions<SharedConfig> sharedOptions)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            _tabManager   = tabManager;
            _converters   = converters;
            _sharedConfig = sharedOptions.Value;
        }

        // ── IGrabber ──────────────────────────────────────────────────────────

        /// <summary>
        /// Launches headless Chromium, binds <see cref="TabManager"/>, and warms up the browser.
        /// </summary>
        public async Task<bool> InitAsync()
        {
            var args = new[]
            {
                "--no-sandbox", "--disable-setuid-sandbox",
                "--disable-dev-shm-usage", "--disable-gpu",
                "--disable-extensions", "--disable-background-networking",
                "--disable-default-apps", "--disable-sync",
                "--no-first-run", "--no-zygote",
                "--renderer-process-limit=1", "--single-process",
                // Prevent Chrome from throttling JS timers and rAF in headless/background tabs.
                // Without these flags setInterval and requestAnimationFrame are frozen between
                // grabs, so every screenshot captures the same pixels and no new frame is written.
                "--disable-background-timer-throttling",
                "--disable-renderer-backgrounding",
                "--disable-backgrounding-occluded-windows"
            };

            string executablePath = GetChromiumExecutablePath();

            // On Windows the binary must be present in Chrome-for-testing\Chrome-win64\.
            // Fail fast with a visible banner rather than letting PuppeteerSharp throw
            // a cryptic ProcessException that gets swallowed by the ILogger pipeline.
            if (OperatingSystem.IsWindows() && !File.Exists(executablePath))
            {
                PrintChromiumMissingBanner(executablePath);
                throw new FileNotFoundException(
                    "Chrome for Testing not found. Run setup-chrome.ps1 to download it.",
                    executablePath);
            }

            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless       = true,
                ExecutablePath = executablePath,
                Args           = args
            }).ConfigureAwait(false);

            _tabManager.Initialize(_browser);

            // Warm-up: navigating once primes font caches and GPU rasterisation
            string warmupUrl =
                $"{_sharedConfig.WebApiBaseUrl}/SiteTemplate?name=servicepage.html";
            try
            {
                await _tabManager
                    .OpenAsync(warmupUrl, DefaultViewport, BaseStyleInjection)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Chrome] Warm-up failed: {ex.Message}");
            }

            // A freshly launched browser is healthy — reset the watchdog clock so a recycle
            // does not immediately re-trigger before the first grab cycle has had time to run.
            Interlocked.Exchange(ref _lastSuccessfulGrabTicks, DateTime.UtcNow.Ticks);

            return true;
        }

        /// <inheritdoc/>
        public DateTime LastSuccessfulGrabUtc =>
            new(Interlocked.Read(ref _lastSuccessfulGrabTicks), DateTimeKind.Utc);

        /// <inheritdoc/>
        public async Task ForceReinitAsync(CancellationToken ct = default)
        {
            await _reinitLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Console.WriteLine("[Chrome] Watchdog: force-recycling the browser...");

                if (_browser is not null)
                {
                    // Kill the process first: a wedged browser can make Dispose() block
                    // indefinitely, which would hang the watchdog itself.
                    try { _browser.Process?.Kill(entireProcessTree: true); }
                    catch { /* process already gone */ }
                    try { _browser.Dispose(); }
                    catch { /* process already gone */ }
                    _browser = null;
                }

                _tabManager.Reset();
                await InitAsync().ConfigureAwait(false);
                Console.WriteLine("[Chrome] Watchdog: browser recycled.");
            }
            finally
            {
                _reinitLock.Release();
            }
        }

        /// <summary>
        /// Opens a new browser tab for <paramref name="source"/>.
        /// Local template paths are routed through <c>SiteTemplateController</c> because
        /// Chromium on ARM64 blocks <c>file://</c> access.
        /// </summary>
        /// <param name="source">HTTP/HTTPS URL or local template path.</param>
        public async Task<LoadPageResultDTO> AddUrlAsync(string url)
        {
            ArgumentNullException.ThrowIfNull(url);

            var result = new LoadPageResultDTO { Source = url };

            Console.WriteLine($"[Chrome] AddUrlAsync: {url}");

            string url2 = BuildUrl(url);

            try
            {
                await EnsureBrowserAsync().ConfigureAwait(false);

                var (_, finalUrl, success) = await _tabManager
                    .OpenAsync(url2, DefaultViewport, BaseStyleInjection)
                    .ConfigureAwait(false);

                if (success)
                {
                    result.Navigated = true;
                    result.TabName   = finalUrl;
                    result.Success   = true;
                    Console.WriteLine($"[Chrome] Loaded: {finalUrl}");
                }
                else
                {
                    Console.WriteLine($"[Chrome] Navigation failed: {url2}");
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                Console.WriteLine($"[Chrome] AddUrlAsync exception: {ex.Message}");
            }

            return result;
        }

        /// <inheritdoc/>
        public Task RemoveUrlAsync(string tabName)
            => _tabManager.RemoveAsync(tabName);

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            _screenshotLock.Dispose();
            _reinitLock.Dispose();

            if (_browser is not null)
            {
                // CloseAsync shuts down all open pages automatically
                await _browser.CloseAsync().ConfigureAwait(false);
                _browser.Dispose();
                _browser = null;
            }
        }

        // ── IScreenshotCapture ────────────────────────────────────────────────

        /// <summary>
        /// Verifies the browser process is still alive and re-launches it if not.
        /// Uses a dedicated lock so only one reinitialisation runs at a time even
        /// when multiple callers detect the disconnection concurrently.
        /// </summary>
        private async Task EnsureBrowserAsync(CancellationToken ct = default)
        {
            // Fast path — browser is healthy
            if (_browser?.IsConnected == true) return;

            await _reinitLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Double-check after acquiring the lock; another thread may have
                // already reinitialised the browser while this one was waiting.
                if (_browser?.IsConnected == true) return;

                Console.WriteLine("[Chrome] Browser disconnected — reinitialising...");

                if (_browser is not null)
                {
                    try { _browser.Dispose(); } catch { /* process is already dead */ }
                    _browser = null;
                }

                _tabManager.Reset();
                await InitAsync().ConfigureAwait(false);
                Console.WriteLine("[Chrome] Browser reinitialised successfully.");
            }
            finally
            {
                _reinitLock.Release();
            }
        }

        // ── IScreenshotCapture (impl) ─────────────────────────────────────────

        /// <summary>
        /// Takes a screenshot of the tab identified by <see cref="ScreenShotParamDTO.TabName"/>,
        /// resizes it to the requested dimensions, converts the pixels using the converter
        /// specified by <see cref="ScreenShotParamDTO.ConverterId"/>, and writes the result
        /// to ExchangeFolder as <c>{TokenGuid}.bin</c>.
        /// A debug PNG is also saved as <c>{TokenGuid}_TEST.png</c>.
        /// </summary>
        /// <param name="param">Screenshot parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task TakeScreenshotAsync(
            ScreenShotParamDTO param,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(param);
            await _screenshotLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Console.WriteLine($"[Chrome] TakeScreenshotAsync: {param.TabName}");

                IPage? page = _tabManager.Find(param.TabName);
                if (page is null)
                {
                    Console.WriteLine($"[Chrome] Page not found: {param.TabName}");
                    return;
                }

                await page.BringToFrontAsync().ConfigureAwait(false);

                // Resize viewport to match the primary capture target so that
                // JS layout (window.innerWidth / innerHeight) reflects the actual
                // device display size.  This allows responsive templates (e.g.
                // device_status.html) to adapt their canvas to 320×240 or 480×320
                // without relying on a post-capture ImageSharp downscale that would
                // silently squish the layout.
                int targetW = param.Targets is { Count: > 0 }
                    ? param.Targets[0].Width
                    : param.Width;
                int targetH = param.Targets is { Count: > 0 }
                    ? param.Targets[0].Height
                    : param.Height;

                bool viewportChanged =
                    page.Viewport is null
                    || page.Viewport.Width  != targetW
                    || page.Viewport.Height != targetH;

                if (viewportChanged)
                {
                    await page.SetViewportAsync(
                        new PuppeteerSharp.ViewPortOptions { Width = targetW, Height = targetH })
                        .ConfigureAwait(false);

                    // SetViewportAsync alone does not update window.innerWidth for a page
                    // that has already loaded — the JS canvas sizing code already ran with
                    // the old value.  A reload forces the page to re-execute with the
                    // correct viewport dimensions so responsive templates scale properly.
                    await page.ReloadAsync(
                        timeout: null,
                        waitUntil: [PuppeteerSharp.WaitUntilNavigation.Networkidle0])
                        .ConfigureAwait(false);
                }

                // Give JS timers, rAF, and live-data fetches time to complete.
                // 500 ms was not enough: weather / API templates call fetch() on load
                // and the result only updates the DOM after the call resolves; the clock
                // timer (setInterval 1 000 ms) also fires after 1 s.  2 000 ms covers
                // both without adding visible latency to the 5-minute grab cycle.
                await Task.Delay(2_000, ct).ConfigureAwait(false);

                if (param.Monochrome)
                {
                    // Inject grayscale filter right before capture so it does not persist
                    // on the live tab (the tab may be reused for the next periodic grab).
                    await page.AddStyleTagAsync(new PuppeteerSharp.AddTagOptions
                    {
                        Content = MonochromeStyleInjection
                    }).ConfigureAwait(false);
                }

                byte[] pngBytes = await page.ScreenshotDataAsync().ConfigureAwait(false);

                // Build the list of output targets: when Targets is populated,
                // one screenshot produces multiple .bin files at different resolutions.
                // Otherwise fall back to the single top-level Width/Height/TokenGuid.
                List<GrabTargetDTO> targets = param.Targets is { Count: > 0 }
                    ? param.Targets
                    :
                    [
                        new GrabTargetDTO
                        {
                            TokenGuid   = param.TokenGuid,
                            Width       = param.Width,
                            Height      = param.Height,
                            ConverterId = param.ConverterId
                        }
                    ];

                foreach (GrabTargetDTO target in targets)
                {
                    await ProcessTargetAsync(pngBytes, target, ct).ConfigureAwait(false);
                }

                // Mark the browser healthy for the watchdog: a screenshot completed end-to-end.
                Interlocked.Exchange(ref _lastSuccessfulGrabTicks, DateTime.UtcNow.Ticks);
            }
            finally
            {
                _screenshotLock.Release();
            }
        }

        // ── Class-C multi-frame capture ───────────────────────────────────────

        /// <summary>
        /// Captures all animation frames for a Class-C template by navigating to
        /// <c>?vow_frame=N</c> for each frame index and taking a screenshot.
        /// </summary>
        /// <param name="templateUrl">
        /// Full URL of the template (e.g. <c>http://localhost:5000/SiteTemplate?name=scifi_hud.html</c>).
        /// The method appends <c>&amp;vow_frame=N</c> for each frame.
        /// </param>
        /// <param name="frameCount">Number of frames to capture (matches <c>data-vow-frames</c>).</param>
        /// <param name="width">Output width in pixels.</param>
        /// <param name="height">Output height in pixels.</param>
        /// <param name="outputBasePath">
        /// Path prefix for output files.  Frame N is written as
        /// <c>{outputBasePath}_batch_{N}.bin</c>.
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// Array of absolute file paths to the compressed frame files, in frame order.
        /// </returns>
        public async Task<string[]> TakeMultiframeScreenshotAsync(
            string templateUrl,
            int frameCount,
            int width,
            int height,
            string outputBasePath,
            string converterId = "bgr565",
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(templateUrl);
            if (frameCount <= 0 || frameCount > 16)
                throw new ArgumentOutOfRangeException(nameof(frameCount));

            // Resolve local template paths through SiteTemplateController (same as AddUrlAsync)
            // so Chromium ARM64 receives a valid HTTP URL instead of a relative path.
            string resolvedUrl = BuildUrl(templateUrl);

            await _screenshotLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureBrowserAsync(ct).ConfigureAwait(false);

                string[] outputPaths = new string[frameCount];

                for (int frameIdx = 0; frameIdx < frameCount; frameIdx++)
                {
                    // Append vow_frame query parameter to the resolved template URL.
                    string frameUrl = resolvedUrl.Contains('?', StringComparison.Ordinal)
                        ? $"{resolvedUrl}&vow_frame={frameIdx}"
                        : $"{resolvedUrl}?vow_frame={frameIdx}";

                    // Navigate to this frame's URL in a fresh tab (Class C always reload).
                    var viewport = new PuppeteerSharp.ViewPortOptions
                    {
                        Width  = width,
                        Height = height
                    };

                    var (page, finalFrameUrl, success) = await _tabManager
                        .OpenAsync(frameUrl, viewport, BaseStyleInjection)
                        .ConfigureAwait(false);

                    if (!success || page is null)
                    {
                        Console.WriteLine($"[Chrome] Multiframe: navigation failed for frame {frameIdx}");
                        throw new InvalidOperationException(
                            $"Navigation failed for vow_frame={frameIdx}");
                    }

                    // Give JS time to render the frame state.
                    await Task.Delay(1_000, ct).ConfigureAwait(false);

                    byte[] pngBytes = await page.ScreenshotDataAsync().ConfigureAwait(false);

                    // Save frame 0 as a preview PNG so the gallery thumbnail endpoint
                    // (/api/public-templates/{id}/preview) can serve it.
                    // GetPreview looks for {tokenGuid}.png (D-format with hyphens);
                    // outputBasePath carries the N-format GUID, so parse it back.
                    if (frameIdx == 0)
                    {
                        string baseFileName = Path.GetFileName(outputBasePath);
                        if (Guid.TryParseExact(baseFileName, "N", out Guid previewGuid))
                        {
                            string previewPngPath = Path.Combine(
                                Path.GetDirectoryName(outputBasePath)!,
                                $"{previewGuid}.png");
                            await File.WriteAllBytesAsync(previewPngPath, pngBytes, ct)
                                      .ConfigureAwait(false);
                        }
                    }

                    // Close the tab immediately — Class C always uses fresh tabs.
                    await _tabManager.RemoveAsync(finalFrameUrl).ConfigureAwait(false);

                    // Convert PNG → BGR565 compressed bin.
                    // Frame format details: docs/hardware.md — "Frame format / BGR565"
                    using Image<Rgb24> image = Image.Load<Rgb24>(pngBytes);
                    image.Mutate(op => op.Resize(width, height));

                    // Select the output format requested by the caller (e.g. "mono1bit" for
                    // e-paper); fall back to BGR565 when the id is unknown.
                    IImageConverter converter =
                        FindConverter(converterId) ?? FindConverter("bgr565")!;

                    image.Mutate(op => op.Quantize(
                        new SixLabors.ImageSharp.Processing.Processors.Quantization.OctreeQuantizer(
                            new SixLabors.ImageSharp.Processing.Processors.Quantization.QuantizerOptions
                            {
                                MaxColors = 255
                            })));

                    byte[] rawBytes    = converter.Convert(image);
                    // Mono (1-bit / 4-gray) is not BGR565 — ship it raw so the palette
                    // compressor cannot mis-encode it; the firmware expects flag + raw.
                    byte[] outputBytes = converterId is "mono1bit" or "mono4gray"
                        ? FrameCompressor.Raw(rawBytes)
                        : FrameCompressor.Compress(rawBytes);

                    string outPath = $"{outputBasePath}_batch_{frameIdx}.bin";
                    await File.WriteAllBytesAsync(outPath, outputBytes, ct).ConfigureAwait(false);
                    outputPaths[frameIdx] = outPath;

                    Console.WriteLine(
                        $"[Chrome] Multiframe: frame {frameIdx}/{frameCount - 1} → " +
                        $"{outputBytes.Length} B → {outPath}");
                }

                return outputPaths;
            }
            finally
            {
                _screenshotLock.Release();
            }
        }

        // ── Per-target processing ─────────────────────────────────────────────

        /// <summary>
        /// Resizes the captured PNG to the target resolution, converts to the
        /// requested pixel format, compresses, computes deltas, and writes
        /// the output .bin file.  Called once per <see cref="GrabTargetDTO"/>.
        /// </summary>
        private async Task ProcessTargetAsync(
            byte[] pngBytes,
            GrabTargetDTO target,
            CancellationToken ct)
        {
            using Image<Rgb24> image = Image.Load<Rgb24>(pngBytes);
            image.Mutate(op => op.Resize(target.Width, target.Height));

            // Capture a full-colour PNG snapshot at the target resolution BEFORE palette
            // quantisation.  Written alongside the .bin so that the preview endpoint can
            // serve it directly without decoding BGR565, and developers can inspect frames.
            using var previewStream = new MemoryStream();
            image.SaveAsPng(previewStream);
            byte[] previewPngBytes = previewStream.ToArray();

            IImageConverter converter =
                FindConverter(target.ConverterId) ?? FindConverter("bgr565")!;

            image.Mutate(op => op.Quantize(
                new SixLabors.ImageSharp.Processing.Processors.Quantization.OctreeQuantizer(
                    new SixLabors.ImageSharp.Processing.Processors.Quantization.QuantizerOptions
                    {
                        MaxColors = 255
                    })));

            byte[] rawBytes    = converter.Convert(image);
            // Mono (1-bit / 4-gray) is not BGR565 — ship it raw so the palette
            // compressor cannot mis-encode it; the firmware expects flag + raw.
            byte[] outputBytes = target.ConverterId is "mono1bit" or "mono4gray"
                ? FrameCompressor.Raw(rawBytes)
                : FrameCompressor.Compress(rawBytes);

            uint newCrc = ComputeCrc32(outputBytes);
            if (_frameCrcCache.TryGetValue(target.TokenGuid, out uint prevCrc) && prevCrc == newCrc)
            {
                Console.WriteLine(
                    $"[Chrome] Frame unchanged for {target.TokenGuid} " +
                    $"({target.Width}x{target.Height}, crc=0x{newCrc:X8}) — skipping write");
                return;
            }

            _frameCrcCache[target.TokenGuid] = newCrc;

            uint currBinEsp32Crc = ComputeEsp32Crc32(outputBytes);

            if (_prevRawCache.TryGetValue(target.TokenGuid, out byte[]? prevRaw)
                && _prevBinEsp32CrcCache.TryGetValue(target.TokenGuid, out uint prevBinEsp32Crc)
                && prevRaw.Length == rawBytes.Length)
            {
                byte[]? deltaBytes = DeltaEncoder.Encode(
                    prevRaw, rawBytes,
                    target.Width, target.Height,
                    prevBinEsp32Crc, currBinEsp32Crc,
                    out int changedTiles);

                if (deltaBytes is not null)
                {
                    string deltaPath = _sharedConfig.ExchangeFolder
                        + target.TokenGuid
                        + $".delta.{prevBinEsp32Crc:X8}.bin";
                    await File.WriteAllBytesAsync(deltaPath, deltaBytes, ct).ConfigureAwait(false);
                    Console.WriteLine(
                        $"[Chrome] Delta saved ({target.Width}x{target.Height}): " +
                        $"{deltaBytes.Length} B " +
                        $"({deltaBytes.Length * 100 / Math.Max(1, outputBytes.Length)}% of full frame, " +
                        $"{changedTiles} tile(s) changed) " +
                        $"baseCrc=0x{prevBinEsp32Crc:X8} newCrc=0x{currBinEsp32Crc:X8}");

                    PruneDeltaFiles(target.TokenGuid);
                }
                else
                {
                    Console.WriteLine(
                        $"[Chrome] Delta skipped ({target.Width}x{target.Height}): " +
                        $"{changedTiles} tile(s) changed " +
                        $"(>{(int)(DeltaEncoder.MaxDeltaRatio * 100)}% threshold) " +
                        $"baseCrc=0x{prevBinEsp32Crc:X8}");
                }
            }

            _prevRawCache[target.TokenGuid]         = rawBytes;
            _prevBinEsp32CrcCache[target.TokenGuid] = currBinEsp32Crc;

            await File.WriteAllBytesAsync(
                _sharedConfig.ExchangeFolder + target.TokenGuid + ".bin",
                outputBytes, ct).ConfigureAwait(false);

            // Write the full-colour preview PNG alongside the .bin.
            // The preview endpoint serves this directly; no BGR565 decoding at request time.
            await File.WriteAllBytesAsync(
                _sharedConfig.ExchangeFolder + target.TokenGuid + ".png",
                previewPngBytes, ct).ConfigureAwait(false);

            Console.WriteLine(
                $"[Chrome] Target {target.Width}x{target.Height} saved: " +
                $"{target.TokenGuid}.bin ({outputBytes.Length} B) + .png ({previewPngBytes.Length} B)");
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Removes old CRC-named delta files for <paramref name="tokenGuid"/>, keeping the
        /// most recent <see cref="DeltaFileKeepCount"/> files so stale deltas do not
        /// accumulate indefinitely on disk.
        /// </summary>
        private void PruneDeltaFiles(Guid tokenGuid)
        {
            try
            {
                string prefix  = tokenGuid.ToString();
                string[] files = Directory.GetFiles(
                    _sharedConfig.ExchangeFolder, $"{prefix}.delta.*.bin");

                // Sort newest-first by last-write time; delete anything beyond the keep window.
                IEnumerable<string> toDelete = files
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Skip(DeltaFileKeepCount);

                foreach (string f in toDelete)
                {
                    try { File.Delete(f); }
                    catch { /* best-effort — a locked file is skipped */ }
                }
            }
            catch
            {
                // Non-fatal: if the directory scan fails, old files simply accumulate.
            }
        }

        /// <summary>
        /// Builds the full HTTP URL for a source.
        /// Local template paths are rewritten to go through <c>SiteTemplateController</c>
        /// so Chromium ARM64 can access them without <c>file://</c>.
        /// </summary>
        private string BuildUrl(string source)
        {
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return source;

            string fileName = Path.GetFileName(source);
            return $"{_sharedConfig.WebApiBaseUrl}/SiteTemplate?name={Uri.EscapeDataString(fileName)}";
        }

        private IImageConverter? FindConverter(string? formatId)
            => formatId is null
                ? null
                : _converters.FirstOrDefault(
                    c => c.FormatId.Equals(formatId, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Computes standard CRC32/ISO-HDLC of <paramref name="data"/>
        /// (init=0xFFFFFFFF, poly=0xEDB88320 reflected, finalXOR=0xFFFFFFFF).
        /// Matches what <c>esp_rom_crc32_le</c> produces in practice on ESP32,
        /// so the value can be compared against the CRC the device reports in HELLO.
        /// </summary>
        private static uint ComputeCrc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>
        /// Computes CRC32 matching <c>esp_rom_crc32_le(0, buf, len)</c> on ESP32.
        /// Uses init=0 and no finalXOR — this is what the device reports in HELLO.Seq
        /// after successfully rendering a frame.
        /// </summary>
        private static uint ComputeEsp32Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0; // init=0, matches esp_rom_crc32_le(0, ...)
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return crc; // no finalXOR
        }

        /// <summary>
        /// Prints a highly-visible banner to <c>stderr</c> when the Chrome binary is missing
        /// on Windows.  Writes directly to <see cref="Console.Error"/> so the message is
        /// always visible regardless of the configured log level or sink.
        /// </summary>
        private static void PrintChromiumMissingBanner(string expectedPath)
        {
            const string Line = "================================================================================";
            Console.Error.WriteLine();
            Console.Error.WriteLine(Line);
            Console.Error.WriteLine("  !! CHROMIUM NOT FOUND — ViewOwl cannot render templates !!");
            Console.Error.WriteLine(Line);
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  Expected:  {expectedPath}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Fix — run ONE of the following from the solution root:");
            Console.Error.WriteLine();
            Console.Error.WriteLine("    Windows PowerShell:  .\\setup-chrome.ps1");
            Console.Error.WriteLine("    Linux / WSL:         ./setup-chrome.sh   (or: apt install chromium-browser)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Manual (Windows):");
            Console.Error.WriteLine("    https://storage.googleapis.com/chrome-for-testing-public/143.0.7499.169/win64/chrome-win64.zip");
            Console.Error.WriteLine(@"    Extract to: ViewOwl.Grabber.Engine\Chrome-for-testing\");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Line);
            Console.Error.WriteLine();
        }

        /// <summary>
        /// Returns the system path to the Chromium executable.
        /// On Linux: prefers a locally bundled binary, falls back to the system package.
        /// On Windows: uses the locally bundled Chrome for Testing.
        /// </summary>
        private static string GetChromiumExecutablePath()
        {
            string baseDir = AppContext.BaseDirectory;

            if (OperatingSystem.IsLinux())
            {
                string linuxPath = Path.Combine(
                    baseDir, "Chrome-for-testing", "chrome-linux64", "chrome");
                return File.Exists(linuxPath)
                    ? linuxPath
                    : "/usr/bin/chromium-browser";
            }

            return Path.Combine(
                baseDir, "Chrome-for-testing", "Chrome-win64", "chrome.exe");
        }
    }
}
