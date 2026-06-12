namespace ViewOwl.Grabber.WebAPI.Services
{
    /// <summary>
    /// Per-template rate limiter for browser-side frame pushes.
    /// Prevents a single template from saturating the server with binary writes.
    /// </summary>
    public interface IFramePushThrottle
    {
        /// <summary>
        /// Returns <c>true</c> if a push for <paramref name="templateId"/> is allowed right now,
        /// updating the last-push timestamp as a side-effect.
        /// Returns <c>false</c> (without updating the timestamp) when the last push happened
        /// less than <paramref name="minIntervalMs"/> milliseconds ago.
        /// </summary>
        bool TryAcquire(int templateId, int minIntervalMs);
    }
}
