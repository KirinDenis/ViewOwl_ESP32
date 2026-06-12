using Microsoft.AspNetCore.SignalR;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Grabber.Engine;
using ViewOwl.Grabber.WebAPI.Controllers;
using ViewOwl.Grabber.WebAPI.DTOs;
using ViewOwl.Grabber.WebAPI.Hubs;
using ViewOwl.Grabber.WebAPI.Services;
using System.Text.Json;

namespace ViewOwl.Grabber.WebAPI.BackgroundServices
{
    /// <summary>
    /// Hosted service that drains <see cref="GrabChannel"/> and executes each
    /// screenshot capture request via <see cref="IScreenshotCapture"/>.
    /// Runs as a single-consumer loop so Chromium is never called concurrently.
    /// After each capture it optionally updates the associated <c>GrabLog</c> record
    /// and closes the browser tab when the request is an on-demand one-shot grab.
    /// </summary>
    public sealed class GrabWorker : BackgroundService
    {
        private readonly GrabChannel _channel;
        private readonly IScreenshotCapture _capture;
        private readonly IGrabber _grabber;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHubContext<ViewOwlHub> _hub;
        private readonly IFrameRefreshQueue _refreshQueue;

        /// <summary>
        /// Initialises the worker with its collaborators.
        /// </summary>
        /// <param name="channel">The shared screenshot request channel.</param>
        /// <param name="capture">The screenshot capture implementation.</param>
        /// <param name="grabber">The grabber, used to close ephemeral tabs after one-shot captures.</param>
        /// <param name="scopeFactory">
        /// Factory used to resolve the scoped <see cref="IGrabLogRepository"/> and
        /// <see cref="IDeviceRepository"/> from this singleton background service.
        /// </param>
        /// <param name="refreshQueue">
        /// Singleton queue used to notify the UDP server that a device should
        /// re-download its frame on the next PING heartbeat.
        /// </param>
        public GrabWorker(
            GrabChannel channel,
            IScreenshotCapture capture,
            IGrabber grabber,
            IServiceScopeFactory scopeFactory,
            IHubContext<ViewOwlHub> hub,
            IFrameRefreshQueue refreshQueue)
        {
            _channel      = channel;
            _capture      = capture;
            _grabber      = grabber;
            _scopeFactory = scopeFactory;
            _hub          = hub;
            _refreshQueue = refreshQueue;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // ReadAllAsync completes when GrabChannel.DisposeAsync() is called or token fires
            await foreach (var param in _channel.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                bool success = false;
                string? errorMessage = null;
                var grabStartedAt = DateTime.UtcNow;

                // Log grab_started — Chromium is about to load and capture the template.
                if (param.TemplateId > 0)
                    await LogGrabLifecycleEventAsync(param.TemplateId, PipelineEventTypes.GrabStarted).ConfigureAwait(false);

                try
                {
                    await _capture.TakeScreenshotAsync(param, stoppingToken).ConfigureAwait(false);
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown path — exit the loop without updating the log
                    break;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    await Console.Error.WriteLineAsync(
                        $"[GrabWorker] Screenshot failed for {param.TabName}: {ex.Message}")
                        .ConfigureAwait(false);
                }

                // Write a pipeline event so every grab outcome is observable
                // in the ISA-101 dashboard (alert board, L3 template history, L4 diagnostics).
                if (param.TemplateId > 0)
                {
                    int durationMs = (int)(DateTime.UtcNow - grabStartedAt).TotalMilliseconds;
                    await LogGrabPipelineEventAsync(
                        param.TemplateId, success, durationMs, errorMessage)
                        .ConfigureAwait(false);
                }

                // Notify dashboard clients about grab outcome so GrabberNode can update
                // its grab counter and error state in real time, and so DeviceNode can
                // enter PENDING state when a new frame is ready for the connected device.
                if (param.UserId > 0 && param.TemplateId > 0)
                {
                    await _hub.Clients
                              .Group(ViewOwlHub.UserGroup(param.UserId))
                              .SendAsync(
                                  "GrabberUpdate",
                                  new GrabberUpdateEvent(param.TemplateId, success, errorMessage, DateTime.UtcNow),
                                  stoppingToken)
                              .ConfigureAwait(false);
                }

                // When the grab succeeded, schedule a frame-refresh for all devices whose
                // active template just got a new .bin file — they will receive an early
                // AUTH trigger on their next PING so they don't wait the full idle period.
                if (success && param.TemplateId > 0)
                {
                    await ScheduleFrameRefreshAsync(param.TemplateId, stoppingToken)
                        .ConfigureAwait(false);
                }

                // Update the associated GrabLog row when this was an on-demand grab
                if (param.GrabLogId.HasValue)
                {
                    await UpdateGrabLogAsync(param.GrabLogId.Value, success, errorMessage)
                        .ConfigureAwait(false);

                    // Notify the owning user via SignalR so the dashboard can
                    // update the grab log panel without polling.
                    if (param.UserId > 0)
                    {
                        var completedDto = new GrabLogDto(
                            Id          : param.GrabLogId.Value,
                            TemplateId  : param.TemplateId,
                            TokenGuid   : param.TokenGuid,
                            RequestedAt : DateTime.UtcNow,
                            CompletedAt : DateTime.UtcNow,
                            Success     : success,
                            Message     : errorMessage ?? (success ? "OK" : "Unknown error"),
                            Width       : param.Width,
                            Height      : param.Height);

                        await _hub.Clients
                                  .Group(ViewOwlHub.UserGroup(param.UserId))
                                  .SendAsync("GrabCompleted", completedDto, stoppingToken)
                                  .ConfigureAwait(false);
                    }
                }

                // Close the ephemeral tab opened exclusively for this on-demand capture
                if (param.CloseTabAfterCapture)
                {
                    try
                    {
                        await _grabber.RemoveUrlAsync(param.TabName).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await Console.Error.WriteLineAsync(
                            $"[GrabWorker] Failed to close tab {param.TabName}: {ex.Message}")
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        /// <inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Complete the channel writer so ReadAllAsync exits cleanly
            await _channel.DisposeAsync().ConfigureAwait(false);
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Writes a <c>grab_completed</c> or <c>grab_failed</c> pipeline event with duration.
        /// Errors are swallowed — a failed pipeline log write must never break the grab loop.
        /// </summary>
        private async Task LogGrabPipelineEventAsync(
            int templateId, bool success, int durationMs, string? errorMessage)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var pipelineEvents =
                    scope.ServiceProvider.GetRequiredService<IPipelineEventRepository>();

                await pipelineEvents.AddAsync(new PipelineEvent
                {
                    Ts           = DateTime.UtcNow,
                    EventType    = success ? PipelineEventTypes.GrabCompleted : PipelineEventTypes.GrabFailed,
                    TemplateId   = templateId,
                    Success      = success,
                    DurationMs   = durationMs,
                    ErrorMessage = errorMessage,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"[GrabWorker] LogGrabPipelineEvent(templateId={templateId}) failed: {ex.Message}")
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Writes a lightweight lifecycle event (<c>grab_started</c>) with no outcome fields.
        /// Errors are swallowed.
        /// </summary>
        private async Task LogGrabLifecycleEventAsync(int templateId, string eventType)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var pipelineEvents =
                    scope.ServiceProvider.GetRequiredService<IPipelineEventRepository>();

                await pipelineEvents.AddAsync(new PipelineEvent
                {
                    Ts         = DateTime.UtcNow,
                    EventType  = eventType,
                    TemplateId = templateId,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"[GrabWorker] LogGrabLifecycleEvent({eventType}, templateId={templateId}) failed: {ex.Message}")
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Opens a short-lived DI scope to resolve the scoped repository and
        /// marks the specified <c>GrabLog</c> row as completed.
        /// </summary>
        private async Task UpdateGrabLogAsync(int grabLogId, bool success, string? errorMessage)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var grabLogs = scope.ServiceProvider.GetRequiredService<IGrabLogRepository>();
            await grabLogs.MarkCompletedAsync(grabLogId, success, errorMessage).ConfigureAwait(false);
        }

        /// <summary>
        /// Looks up all devices whose active template matches <paramref name="templateId"/>
        /// and enqueues a frame-refresh notification for each one.
        /// Errors are swallowed — a missed notification is non-fatal; the device will
        /// refresh on its own after the 5-minute idle period.
        /// </summary>
        private async Task ScheduleFrameRefreshAsync(int templateId, CancellationToken ct)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();

                IReadOnlyList<Device> devices =
                    await deviceRepo.GetByActiveTemplateIdAsync(templateId, ct)
                                    .ConfigureAwait(false);

                foreach (Device d in devices)
                    _refreshQueue.ScheduleRefresh(d.Id);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"[GrabWorker] ScheduleFrameRefresh(templateId={templateId}) failed: {ex.Message}")
                    .ConfigureAwait(false);
            }
        }
    }
}
