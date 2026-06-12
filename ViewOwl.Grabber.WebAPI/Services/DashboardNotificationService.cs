using Microsoft.AspNetCore.SignalR;
using ViewOwl.Grabber.WebAPI.DTOs;
using ViewOwl.Grabber.WebAPI.Hubs;

namespace ViewOwl.Grabber.WebAPI.Services
{
    /// <summary>
    /// SignalR implementation of <see cref="IDashboardNotificationService"/>.
    /// Uses <see cref="IHubContext{ViewOwlHub}"/> because the existing frontend
    /// connects to <c>/hubs/viewowl</c>.  <see cref="DashboardHub"/> clients at
    /// <c>/hubs/dashboard</c> share the same group names so they also receive events.
    /// </summary>
    public sealed class DashboardNotificationService : IDashboardNotificationService
    {
        private readonly IHubContext<ViewOwlHub> _hub;
        private readonly ILogger<DashboardNotificationService> _logger;

        /// <summary>
        /// Initialises the service.
        /// </summary>
        public DashboardNotificationService(
            IHubContext<ViewOwlHub> hub,
            ILogger<DashboardNotificationService> logger)
        {
            ArgumentNullException.ThrowIfNull(hub);
            ArgumentNullException.ThrowIfNull(logger);
            _hub    = hub;
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task NotifyDeviceStatusChangedAsync(int userId, DeviceStatusChangedEvent evt)
        {
            try
            {
                await _hub.Clients
                          .Group(ViewOwlHub.UserGroup(userId))
                          .SendAsync("DeviceStatusChanged", evt)
                          .ConfigureAwait(false);

                _logger.LogDebug(
                    "→ DeviceStatusChanged user={UserId} device={DeviceId} status={Status}",
                    userId, evt.DeviceId, evt.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to push DeviceStatusChanged to user {UserId}", userId);
            }
        }

        /// <inheritdoc/>
        public async Task NotifyDevicePingAsync(int userId, DevicePingEvent evt)
        {
            try
            {
                await _hub.Clients
                          .Group(ViewOwlHub.UserGroup(userId))
                          .SendAsync("DevicePing", evt)
                          .ConfigureAwait(false);

                _logger.LogDebug(
                    "→ DevicePing user={UserId} device={DeviceId}",
                    userId, evt.DeviceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to push DevicePing to user {UserId}", userId);
            }
        }

        /// <inheritdoc/>
        public async Task NotifyGrabberUpdateAsync(int userId, GrabberUpdateEvent evt)
        {
            try
            {
                await _hub.Clients
                          .Group(ViewOwlHub.UserGroup(userId))
                          .SendAsync("GrabberUpdate", evt)
                          .ConfigureAwait(false);

                _logger.LogDebug(
                    "→ GrabberUpdate user={UserId} template={TemplateId} success={Success}",
                    userId, evt.TemplateId, evt.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to push GrabberUpdate to user {UserId}", userId);
            }
        }

        /// <inheritdoc/>
        public async Task NotifyDeviceTransferStartedAsync(int userId, DeviceTransferStartedEvent evt)
        {
            try
            {
                await _hub.Clients
                          .Group(ViewOwlHub.UserGroup(userId))
                          .SendAsync("DeviceTransferStarted", evt)
                          .ConfigureAwait(false);

                _logger.LogDebug(
                    "→ DeviceTransferStarted user={UserId} device={DeviceId} template={TemplateId} bytes={Bytes}",
                    userId, evt.DeviceId, evt.TemplateId, evt.ImageSizeBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to push DeviceTransferStarted to user {UserId}", userId);
            }
        }

        /// <inheritdoc/>
        public async Task NotifyDeviceTransferProgressAsync(int userId, DeviceTransferProgressEvent evt)
        {
            try
            {
                await _hub.Clients
                          .Group(ViewOwlHub.UserGroup(userId))
                          .SendAsync("DeviceTransferProgress", evt)
                          .ConfigureAwait(false);

                _logger.LogDebug(
                    "→ DeviceTransferProgress user={UserId} device={DeviceId} chunks={Sent}/{Total}",
                    userId, evt.DeviceId, evt.ChunksSent, evt.TotalChunks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to push DeviceTransferProgress to user {UserId}", userId);
            }
        }

        /// <inheritdoc/>
        public async Task NotifyDeviceGrabCompletedAsync(int userId, DeviceGrabCompletedEvent evt)
        {
            try
            {
                await _hub.Clients
                          .Group(ViewOwlHub.UserGroup(userId))
                          .SendAsync("DeviceGrabCompleted", evt)
                          .ConfigureAwait(false);

                _logger.LogDebug(
                    "→ DeviceGrabCompleted user={UserId} device={DeviceId} template={TemplateId} outcome={Outcome}",
                    userId, evt.DeviceId, evt.TemplateId, evt.Outcome ?? (evt.Success ? "success" : "fail"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to push DeviceGrabCompleted to user {UserId}", userId);
            }
        }

        /// <inheritdoc/>
        public async Task NotifyDeviceKnockingAsync(int? ownerId, DeviceKnockingEvent evt)
        {
            try
            {
                // Send to owner's group when the device is known; broadcast to all when it isn't,
                // so any logged-in user can see an unregistered device trying to connect.
                Microsoft.AspNetCore.SignalR.IClientProxy target = ownerId.HasValue
                    ? _hub.Clients.Group(ViewOwlHub.UserGroup(ownerId.Value))
                    : _hub.Clients.All;

                await target.SendAsync("DeviceKnocking", evt).ConfigureAwait(false);

                _logger.LogDebug(
                    "→ DeviceKnocking ip={Ip} token={Token} reason={Reason}",
                    evt.IpAddress, evt.Token, evt.Reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to push DeviceKnocking for {Ip}", evt.IpAddress);
            }
        }

        /// <inheritdoc/>
        public async Task NotifyDeployAsync(DeployNotificationEvent evt)
        {
            try
            {
                await _hub.Clients.All.SendAsync("DeployNotification", evt).ConfigureAwait(false);
                _logger.LogInformation("→ DeployNotification phase={Phase} commit={Commit}", evt.Phase, evt.Commit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast DeployNotification phase={Phase}", evt.Phase);
            }
        }
    }
}
