using ViewOwl.Grabber.Engine;

namespace ViewOwl.Grabber.WebAPI.BackgroundServices
{
    /// <summary>
    /// Recovers a wedged headless browser. The grabber browser can stop producing frames
    /// while still reporting as connected (CDP unresponsive), which freezes the whole grab
    /// pipeline until a manual <c>systemctl restart</c>. This watchdog polls the time of the
    /// last successful grab and force-recycles the browser when it goes stale, so the pipeline
    /// self-heals.
    /// </summary>
    /// <remarks>
    /// Complements the navigation timeout in <see cref="TabManager"/>: the timeout stops a
    /// single hung navigation from freezing the worker (grabs fail fast instead), and this
    /// watchdog notices that no grab has succeeded and recycles the browser so they resume.
    /// </remarks>
    public sealed class GrabberWatchdogService : BackgroundService
    {
        // How often to check browser health.
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

        // No successful grab within this window means the browser is wedged. The
        // DeviceTemplateRefreshWorker grabs assigned + public templates every 5 minutes, so a
        // healthy pipeline produces a grab well inside this window; 10 minutes leaves ample
        // margin against a normal idle gap before a recycle is triggered.
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(10);

        private readonly IGrabber _grabber;
        private readonly ILogger<GrabberWatchdogService> _logger;

        /// <summary>
        /// Initialises the watchdog with the grabber it supervises.
        /// </summary>
        /// <param name="grabber">The headless browser grabber to monitor and recycle.</param>
        /// <param name="logger">Logger.</param>
        public GrabberWatchdogService(IGrabber grabber, ILogger<GrabberWatchdogService> logger)
        {
            _grabber = grabber;
            _logger  = logger;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Wait one interval before the first check so the browser has time to warm up
                // and the first grab cycle can run.
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        TimeSpan sinceLast = DateTime.UtcNow - _grabber.LastSuccessfulGrabUtc;
                        if (sinceLast > StaleThreshold)
                        {
                            _logger.LogWarning(
                                "[GrabberWatchdog] No successful grab in {Minutes:F1} min " +
                                "(threshold {Threshold} min) — recycling the headless browser.",
                                sinceLast.TotalMinutes, StaleThreshold.TotalMinutes);

                            await _grabber.ForceReinitAsync(stoppingToken).ConfigureAwait(false);

                            _logger.LogInformation(
                                "[GrabberWatchdog] Browser recycled; grab pipeline should resume.");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // shutdown — propagate to exit the loop
                    }
                    catch (Exception ex)
                    {
                        // A failed check must not kill the watchdog — log and keep polling.
                        _logger.LogError(ex, "[GrabberWatchdog] Health check failed.");
                    }

                    await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — stoppingToken was cancelled.
            }
        }
    }
}
