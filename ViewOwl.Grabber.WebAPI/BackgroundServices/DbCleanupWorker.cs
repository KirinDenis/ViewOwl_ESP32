using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data;
using ViewOwl.Security;

namespace ViewOwl.Grabber.WebAPI.BackgroundServices
{
    /// <summary>
    /// Daily maintenance job that deletes stale rows from high-volume tables, compacts both
    /// SQLite databases with <c>VACUUM</c>, and removes stale exchange-folder artefacts so
    /// that disk usage stays bounded over time.
    /// </summary>
    /// <remarks>
    /// Retention windows (configurable via the fields below):
    /// <list type="bullet">
    ///   <item><description><c>DevicePings</c>    — 7 days</description></item>
    ///   <item><description><c>PipelineEvents</c> — 14 days</description></item>
    ///   <item><description><c>GrabLogs</c>       — 30 days</description></item>
    ///   <item><description><c>SecurityEvents</c> — 30 days</description></item>
    ///   <item><description>Exchange-folder <c>*_TEST.png</c> debug files — 1 day</description></item>
    ///   <item><description>Exchange-folder <c>*_batch*.json</c> temporary files — 1 day</description></item>
    /// </list>
    /// The first run is delayed 30 minutes after startup so that the freshly seeded DB is not
    /// immediately trimmed during local development. Subsequent runs fire every 24 hours.
    /// </remarks>
    public sealed class DbCleanupWorker : BackgroundService
    {
        private static readonly TimeSpan InitialDelay  = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RunInterval   = TimeSpan.FromHours(24);

        private static readonly TimeSpan DevicePingRetention    = TimeSpan.FromDays(7);
        private static readonly TimeSpan PipelineEventRetention = TimeSpan.FromDays(14);
        private static readonly TimeSpan GrabLogRetention       = TimeSpan.FromDays(30);
        private static readonly TimeSpan SecurityEventRetention = TimeSpan.FromDays(30);
        private static readonly TimeSpan ExchangeFileRetention  = TimeSpan.FromDays(1);

        private readonly IServiceProvider          _serviceProvider;
        private readonly ILogger<DbCleanupWorker>  _logger;
        private readonly SharedConfig              _sharedConfig;

        /// <summary>
        /// Initialises the worker with required services.
        /// </summary>
        public DbCleanupWorker(
            IServiceProvider         serviceProvider,
            ILogger<DbCleanupWorker> logger,
            IOptions<SharedConfig>   sharedConfig)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(sharedConfig);

            _serviceProvider = serviceProvider;
            _logger          = logger;
            _sharedConfig    = sharedConfig.Value;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "DbCleanupWorker started. First run in {InitialDelay} minutes.",
                InitialDelay.TotalMinutes);

            try
            {
                // Delay so the server is fully warm before touching the database.
                await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await RunCleanupAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "DbCleanupWorker: unhandled error during cleanup cycle.");
                    }

                    await Task.Delay(RunInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — not an error.
            }

            _logger.LogInformation("DbCleanupWorker stopped.");
        }

        private async Task RunCleanupAsync(CancellationToken ct)
        {
            DateTime now = DateTime.UtcNow;

            // Scoped services must be resolved per-operation; the hosted service is singleton.
            await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();

            var db       = scope.ServiceProvider.GetRequiredService<ViewOwlDbContext>();
            var securityDb = scope.ServiceProvider.GetRequiredService<SecurityDbContext>();

            // ── DevicePings ───────────────────────────────────────────────────
            DateTime pingCutoff = now - DevicePingRetention;
            int pingsDeleted = await db.DevicePings
                .Where(p => p.Timestamp < pingCutoff)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            // ── PipelineEvents ────────────────────────────────────────────────
            DateTime eventCutoff = now - PipelineEventRetention;
            int eventsDeleted = await db.PipelineEvents
                .Where(e => e.Ts < eventCutoff)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            // ── GrabLogs ──────────────────────────────────────────────────────
            DateTime grabCutoff = now - GrabLogRetention;
            int grabsDeleted = await db.GrabLogs
                .Where(g => g.RequestedAt < grabCutoff)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            // ── SecurityEvents ────────────────────────────────────────────────
            DateTime secCutoff = now - SecurityEventRetention;
            int secEventsDeleted = await securityDb.SecurityEvents
                .Where(e => e.CreatedAt < secCutoff)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            // ── SQLite VACUUM ─────────────────────────────────────────────────
            // SQLite does not return freed pages to the OS after DELETE — only VACUUM does.
            // VACUUM cannot run inside a transaction; EF's ExecuteDeleteAsync uses its own
            // transaction per call, so issuing it here (after all deletes) is safe.
            await db.Database
                .ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", ct)
                .ConfigureAwait(false);
            await db.Database
                .ExecuteSqlRawAsync("VACUUM", ct)
                .ConfigureAwait(false);

            await securityDb.Database
                .ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", ct)
                .ConfigureAwait(false);
            await securityDb.Database
                .ExecuteSqlRawAsync("VACUUM", ct)
                .ConfigureAwait(false);

            // ── Exchange folder: stale debug / temp files ─────────────────────
            int exchangeFilesDeleted = DeleteStaleExchangeFiles(now);

            _logger.LogInformation(
                "DbCleanupWorker: deleted {Pings} pings, {Events} pipeline events, "
                + "{Grabs} grab logs, {SecEvents} security events, "
                + "{ExFiles} stale exchange files. VACUUM complete.",
                pingsDeleted,
                eventsDeleted,
                grabsDeleted,
                secEventsDeleted,
                exchangeFilesDeleted);
        }

        /// <summary>
        /// Deletes debug PNG screenshots and temporary batch files from the exchange folder
        /// that are older than <see cref="ExchangeFileRetention"/>.
        /// </summary>
        /// <remarks>
        /// <c>*_TEST.png</c> — debug thumbnails written by Chrome.cs after each grab (legacy;
        /// the write has been removed, but this keeps the folder clean if old files remain).
        /// <c>*_batch*.json</c> — temporary orchestration manifests written per grab session.
        /// </remarks>
        private int DeleteStaleExchangeFiles(DateTime now)
        {
            if (string.IsNullOrWhiteSpace(_sharedConfig.ExchangeFolder) ||
                !Directory.Exists(_sharedConfig.ExchangeFolder))
            {
                return 0;
            }

            DateTime cutoff = now - ExchangeFileRetention;
            int deleted     = 0;

            // Patterns that should never accumulate — wipe anything older than 1 day.
            string[] patterns = ["*_TEST.png", "*_batch*.json", "*_batch*.bin"];

            foreach (string pattern in patterns)
            {
                foreach (string file in Directory.EnumerateFiles(_sharedConfig.ExchangeFolder, pattern))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.Delete(file);
                            deleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "DbCleanupWorker: failed to delete exchange file {File}.", file);
                    }
                }
            }

            return deleted;
        }
    }
}
