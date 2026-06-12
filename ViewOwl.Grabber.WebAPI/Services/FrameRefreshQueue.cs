using System.Collections.Concurrent;

namespace ViewOwl.Grabber.WebAPI.Services
{
    /// <summary>
    /// Thread-safe in-memory implementation of <see cref="IFrameRefreshQueue"/>.
    /// Uses a <see cref="ConcurrentDictionary{TKey,TValue}"/> so
    /// <see cref="ScheduleRefresh"/> and <see cref="ConsumeRefresh"/> are lock-free.
    /// </summary>
    public sealed class FrameRefreshQueue : IFrameRefreshQueue
    {
        private readonly ConcurrentDictionary<int, bool> _pending = new();

        /// <inheritdoc/>
        public void ScheduleRefresh(int deviceId)
            => _pending.TryAdd(deviceId, true);

        /// <inheritdoc/>
        public bool ConsumeRefresh(int deviceId)
            => _pending.TryRemove(deviceId, out _);
    }
}
