using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using ViewOwl.Config;
using ViewOwl.DTO;
using ViewOwl.Grabber.Config;
using ViewOwl.Grabber.Engine;

namespace ViewOwl.Grabber.WebAPI.BackgroundServices
{
    /// <summary>
    /// Scheduled grabber: loads sources from <c>GrabbedList.json</c>, opens a browser tab
    /// for each source, then enqueues periodic screenshot requests to <see cref="GrabChannel"/>.
    /// Actual capture is handled by <see cref="GrabWorker"/>.
    /// </summary>
    public class GrabberWorker : BackgroundService
    {
        private readonly IGrabber _grabber;
        private readonly GrabChannel _grabChannel;
        private readonly GrabberConfig _grabberConfig;
        private readonly SharedConfig _sharedConfig;

        private List<GrabberSourceDTO> _sources = [];

        /// <summary>
        /// Loads source list from disk on construction.
        /// Creates a default example file if the list does not exist yet.
        /// </summary>
        public GrabberWorker(
            IGrabber grabber,
            GrabChannel grabChannel,
            IOptions<GrabberConfig> optionsGrabberConfig,
            IOptions<SharedConfig> optionsSharedConfig)
        {
            ArgumentNullException.ThrowIfNull(optionsGrabberConfig);
            ArgumentNullException.ThrowIfNull(optionsSharedConfig);

            _grabber      = grabber;
            _grabChannel  = grabChannel;
            _grabberConfig = optionsGrabberConfig.Value;
            _sharedConfig  = optionsSharedConfig.Value;

            string configPath =
                $"{_sharedConfig.ExchangeFolder}{_grabberConfig.GrabbedListFileName}";

            // Try to read grabbed URLs; if file does not exist, write a default example
            if (File.Exists(configPath))
            {
                _sources =
                    JsonConvert.DeserializeObject<List<GrabberSourceDTO>>(
                        File.ReadAllText(configPath)) ?? [];
            }
            else
            {
                _sources =
                [
                    new GrabberSourceDTO
                    {
                        Source    = "https://kyivfortress.net/owlos/",
                        TokenGuid = Guid.NewGuid()
                    },
                    new GrabberSourceDTO
                    {
                        Source    = "https://my.alfred.edu/zoom/_images/foster-lake.jpg",
                        TokenGuid = Guid.NewGuid()
                    }
                ];

                File.WriteAllText(configPath, JsonConvert.SerializeObject(_sources));
            }
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Each grab cycle opens a fresh ephemeral tab per source and closes it after capture.
            // The previous approach of opening persistent tabs at startup caused all tabs to be
            // lost whenever the browser reinitialised under memory pressure (28 animated-canvas
            // tabs simultaneously).  Ephemeral tabs mirror DeviceTemplateRefreshWorker and keep
            // at most 1–2 tabs open at any moment.
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (GrabberSourceDTO dto in _sources)
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;

                    LoadPageResultDTO loadResult;
                    try
                    {
                        loadResult = await _grabber.AddUrlAsync(dto.Source).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"[GrabberWorker] Tab open failed for '{dto.Source}': {ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken)
                                  .ConfigureAwait(false);
                        continue;
                    }

                    if (!loadResult.Success)
                    {
                        Console.WriteLine(
                            $"[GrabberWorker] Navigation failed for '{dto.Source}'");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken)
                                  .ConfigureAwait(false);
                        continue;
                    }

                    ScreenShotParamDTO param;

                    if (dto.Targets is { Count: > 0 })
                    {
                        // Multi-target: one screenshot produces one .bin per target resolution.
                        param = new ScreenShotParamDTO
                        {
                            TabName              = loadResult.TabName,
                            Targets              = dto.Targets,
                            CloseTabAfterCapture = true,
                        };
                    }
                    else
                    {
                        // Legacy single-target format.
                        param = new ScreenShotParamDTO
                        {
                            Width                = (int)dto.Width,
                            Height               = (int)dto.Height,
                            TokenGuid            = dto.TokenGuid,
                            TabName              = loadResult.TabName,
                            ConverterId          = dto.ConverterId,
                            CloseTabAfterCapture = true,
                        };
                    }

                    // Enqueue the request; GrabWorker executes the capture and closes the tab.
                    await _grabChannel.EnqueueAsync(param, stoppingToken).ConfigureAwait(false);

                    // Pace enqueues so the browser is not overwhelmed when many sources exist.
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken)
                              .ConfigureAwait(false);
                }

                // Wait between full grab cycles.
                // Gallery previews are static — hourly refresh is sufficient and keeps
                // Chromium load low while DeviceTemplateRefreshWorker handles live devices.
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await _grabber.DisposeAsync().ConfigureAwait(false);
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
