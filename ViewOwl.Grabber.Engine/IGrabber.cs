using ViewOwl.DTO;

namespace ViewOwl.Grabber.Engine
{
    /// <summary>
    /// Public contract for the headless browser grabber.
    /// Covers browser lifecycle and tab management only.
    /// Screenshot capture is an internal concern accessible via <see cref="IScreenshotCapture"/>,
    /// consumed exclusively by <c>GrabWorker</c> through the <see cref="GrabQueue"/>.
    /// </summary>
    public interface IGrabber
    {
        /// <summary>Launches the browser and warms up the rendering pipeline.</summary>
        Task<bool> InitAsync();

        /// <summary>
        /// UTC timestamp of the most recent successful screenshot (or browser (re)launch).
        /// Read by the grabber watchdog to detect a wedged browser that has stopped
        /// producing frames even though it still reports as connected.
        /// </summary>
        DateTime LastSuccessfulGrabUtc { get; }

        /// <summary>
        /// Forcibly recycles the headless browser regardless of its reported connection
        /// state. Kills the process first so a hung <c>Dispose</c> cannot block the recycle,
        /// then relaunches via <see cref="InitAsync"/>. Used by the watchdog to recover a
        /// browser that is "connected" but no longer responding to navigation.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task ForceReinitAsync(CancellationToken ct = default);

        /// <summary>
        /// Opens a new browser tab for <paramref name="url"/> and returns metadata
        /// including the final tab URL used as the capture key.
        /// </summary>
        /// <param name="url">HTTP/HTTPS URL or local template path.</param>
        Task<LoadPageResultDTO> AddUrlAsync(string url);

        /// <summary>
        /// Closes and removes the tab identified by <paramref name="tabName"/>.
        /// </summary>
        /// <param name="tabName">The tab URL as returned by <see cref="AddUrlAsync"/>.</param>
        Task RemoveUrlAsync(string tabName);

        /// <summary>Releases the browser and all associated resources.</summary>
        ValueTask DisposeAsync();

        /// <summary>
        /// Captures <paramref name="frameCount"/> animation frames for a Class-C template.
        /// Navigates to <c>{templateUrl}?vow_frame=N</c> for each frame, converts each
        /// screenshot to a compressed BGR565 binary, and writes it as
        /// <c>{outputBasePath}_batch_{N}.bin</c>.
        /// </summary>
        /// <param name="templateUrl">Local template path or full HTTP/HTTPS URL.</param>
        /// <param name="frameCount">Number of frames to capture (1–16).</param>
        /// <param name="width">Capture viewport width in pixels.</param>
        /// <param name="height">Capture viewport height in pixels.</param>
        /// <param name="outputBasePath">
        /// Base file path (no extension). Output files are
        /// <c>{outputBasePath}_batch_0.bin</c> through <c>{outputBasePath}_batch_{N-1}.bin</c>.
        /// </param>
        /// <param name="converterId">
        /// Pixel-format converter to apply, matching an <c>IImageConverter.FormatId</c>
        /// (e.g. "bgr565" for LCDs, "mono1bit" for e-paper). Defaults to "bgr565".
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Array of absolute paths to the written frame files, in frame order.</returns>
        Task<string[]> TakeMultiframeScreenshotAsync(
            string templateUrl,
            int frameCount,
            int width,
            int height,
            string outputBasePath,
            string converterId = "bgr565",
            CancellationToken ct = default);
    }
}
