namespace ViewOwl.Grabber.WebAPI.Services
{
    /// <summary>
    /// In-memory queue that tracks which devices need to fetch a new frame.
    /// Populated by <c>GrabWorker</c> after a successful capture; consumed by
    /// <c>InternalController.ConsumeRestart</c> so the UDP server can push an
    /// early AUTH packet to the device during its next PING heartbeat.
    /// </summary>
    public interface IFrameRefreshQueue
    {
        /// <summary>
        /// Marks <paramref name="deviceId"/> as needing a frame refresh.
        /// Idempotent — calling twice before the device pings is harmless.
        /// </summary>
        /// <param name="deviceId">Primary key of the device that should re-download its frame.</param>
        void ScheduleRefresh(int deviceId);

        /// <summary>
        /// Returns <c>true</c> and removes the entry when a refresh is pending for
        /// <paramref name="deviceId"/>; returns <c>false</c> when nothing is queued.
        /// Thread-safe and atomic — concurrent calls for the same device will only
        /// return <c>true</c> once.
        /// </summary>
        /// <param name="deviceId">Primary key of the device being checked.</param>
        bool ConsumeRefresh(int deviceId);
    }
}
