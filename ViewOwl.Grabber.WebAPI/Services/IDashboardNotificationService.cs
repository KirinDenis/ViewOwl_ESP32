using ViewOwl.Grabber.WebAPI.DTOs;

namespace ViewOwl.Grabber.WebAPI.Services
{
    /// <summary>
    /// Sends real-time events to dashboard clients via SignalR.
    /// Each method targets the SignalR group of the device owner so only
    /// the relevant user receives the notification.
    /// </summary>
    public interface IDashboardNotificationService
    {
        /// <summary>Notifies the owner when a device comes online or goes offline.</summary>
        Task NotifyDeviceStatusChangedAsync(int userId, DeviceStatusChangedEvent evt);

        /// <summary>Notifies the owner when a PING heartbeat is received from a device.</summary>
        Task NotifyDevicePingAsync(int userId, DevicePingEvent evt);

        /// <summary>Notifies the owner when the grabber worker captures a template.</summary>
        Task NotifyGrabberUpdateAsync(int userId, GrabberUpdateEvent evt);

        /// <summary>Notifies the owner when the UDP Server starts streaming a frame to a device.</summary>
        Task NotifyDeviceTransferStartedAsync(int userId, DeviceTransferStartedEvent evt);

        /// <summary>Notifies the owner with live progress during an active UDP transfer.</summary>
        Task NotifyDeviceTransferProgressAsync(int userId, DeviceTransferProgressEvent evt);

        /// <summary>Notifies the owner when the UDP Server finishes delivering a frame to a device.</summary>
        Task NotifyDeviceGrabCompletedAsync(int userId, DeviceGrabCompletedEvent evt);

        /// <summary>
        /// Notifies when a device sends HELLO but cannot be authenticated.
        /// When <paramref name="ownerId"/> is null the event is broadcast to all connected users
        /// (used for devices not yet registered in the database).
        /// </summary>
        Task NotifyDeviceKnockingAsync(int? ownerId, DeviceKnockingEvent evt);

        /// <summary>
        /// Broadcasts a deploy lifecycle event to ALL connected dashboard clients.
        /// Called by CI/CD before services stop ("starting") and after restart ("complete").
        /// </summary>
        Task NotifyDeployAsync(DeployNotificationEvent evt);
    }
}
