using System.Collections.Concurrent;

namespace ViewOwl.Grabber.WebAPI.Services
{
    /// <summary>
    /// Thread-safe per-template rate limiter backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// Uses <see cref="Environment.TickCount64"/> (monotonic, millisecond resolution) so it is
    /// immune to system clock adjustments.
    /// </summary>
    public sealed class FramePushThrottle : IFramePushThrottle
    {
        // templateId → TickCount64 of the last allowed push
        private readonly ConcurrentDictionary<int, long> _lastTicks = new();

        /// <inheritdoc/>
        public bool TryAcquire(int templateId, int minIntervalMs)
        {
            long now = Environment.TickCount64;

            // If the key is absent (first push ever) → always allow.
            // If the key exists and the interval has not elapsed → reject.
            if (_lastTicks.TryGetValue(templateId, out long last) && now - last < minIntervalMs)
                return false;

            // Record the timestamp and allow.  A concurrent write from another request is
            // harmless: both pass, and the winner's timestamp wins — still correct.
            _lastTicks[templateId] = now;
            return true;
        }
    }
}
