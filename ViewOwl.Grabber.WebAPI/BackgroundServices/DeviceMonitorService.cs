using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Grabber.WebAPI.DTOs;
using ViewOwl.Grabber.WebAPI.Services;

namespace ViewOwl.Grabber.WebAPI.BackgroundServices
{
    /// <summary>
    /// Background service that monitors device heartbeats and marks devices as offline
    /// if they haven't sent a PING packet within the configured threshold.
    /// </summary>
    public sealed class DeviceMonitorService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DeviceMonitorService> _logger;
        private readonly DeviceMonitorConfig _config;

        /// <summary>
        /// Initialises the service with its dependencies.
        /// </summary>
        public DeviceMonitorService(
            IServiceProvider serviceProvider,
            ILogger<DeviceMonitorService> logger,
            IOptions<DeviceMonitorConfig> config)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(config);

            _serviceProvider = serviceProvider;
            _logger          = logger;
            _config          = config.Value;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "DeviceMonitorService started. Check interval: {CheckInterval}s, Offline threshold: {OfflineThreshold}s",
                _config.CheckIntervalSeconds,
                _config.OfflineThresholdSeconds);

            try
            {
                // Short initial delay so the app is fully started before first check.
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await CheckDevicesAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in DeviceMonitorService check cycle");
                    }

                    await Task.Delay(
                        TimeSpan.FromSeconds(_config.CheckIntervalSeconds),
                        stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — stoppingToken was cancelled. Not an error.
            }

            _logger.LogInformation("DeviceMonitorService stopped");
        }

        private async Task CheckDevicesAsync(CancellationToken ct)
        {
            // Scoped services (repositories) must be resolved inside a scope;
            // the hosted service itself is singleton-lifetime.
            using var scope = _serviceProvider.CreateScope();

            var deviceRepo     = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var notifications  = scope.ServiceProvider.GetRequiredService<IDashboardNotificationService>();
            var pipelineEvents = scope.ServiceProvider.GetRequiredService<IPipelineEventRepository>();

            DateTime cutoff = DateTime.UtcNow.AddSeconds(-_config.OfflineThresholdSeconds);

            IReadOnlyList<Device> staleDevices =
                await deviceRepo.GetNotSeenSinceAsync(cutoff, ct).ConfigureAwait(false);

            int markedOffline = 0;

            foreach (Device device in staleDevices)
            {
                if (device.Status != DeviceStatus.Online)
                    continue;

                device.Status = DeviceStatus.Offline;
                await deviceRepo.UpdateAsync(device, ct).ConfigureAwait(false);

                // Log the offline transition so the pipeline timeline shows the gap.
                await pipelineEvents.AddAsync(new PipelineEvent
                {
                    Ts        = DateTime.UtcNow,
                    EventType = PipelineEventTypes.DeviceOffline,
                    DeviceId  = device.Id,
                    Success   = false,
                }, ct).ConfigureAwait(false);

                markedOffline++;

                _logger.LogInformation(
                    "Device {DeviceId} ({DeviceName}) marked as offline. Last seen: {LastSeen}",
                    device.Id,
                    device.Name,
                    device.LastSeenAt?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                        ?? "never");

                await notifications.NotifyDeviceStatusChangedAsync(
                    device.UserId,
                    new DeviceStatusChangedEvent(
                        device.Id,
                        "Offline",
                        device.LastSeenAt,
                        device.IpAddress))
                    .ConfigureAwait(false);
            }

            if (markedOffline > 0)
            {
                _logger.LogInformation(
                    "DeviceMonitorService: Marked {Count} device(s) as offline",
                    markedOffline);
            }
            else
            {
                _logger.LogDebug("DeviceMonitorService: All devices are online or already offline");
            }
        }
    }
}
