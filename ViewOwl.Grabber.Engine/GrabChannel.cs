using System.Threading.Channels;
using ViewOwl.DTO;

namespace ViewOwl.Grabber.Engine
{
    /// <summary>
    /// Thread-safe unbounded channel for screenshot capture requests.
    /// Producers (<c>GrabberWorker</c>) enqueue <see cref="ScreenShotParamDTO"/> items;
    /// the single consumer (<c>GrabWorker</c>) reads them via an async stream.
    /// Uses <see cref="Channel{T}"/> for allocation-efficient, back-pressure-free operation.
    /// </summary>
    public sealed class GrabChannel : IAsyncDisposable
    {
        private readonly Channel<ScreenShotParamDTO> _channel =
            Channel.CreateUnbounded<ScreenShotParamDTO>(
                new UnboundedChannelOptions
                {
                    // Only GrabWorker reads; enables internal optimisations
                    SingleReader = true,
                    // Prevent synchronous continuations to avoid deadlocks on the thread pool
                    AllowSynchronousContinuations = false
                });

        /// <summary>
        /// Enqueues a screenshot capture request. This call never blocks the caller.
        /// </summary>
        /// <param name="param">The capture parameters to enqueue.</param>
        /// <param name="ct">Cancellation token.</param>
        public ValueTask EnqueueAsync(ScreenShotParamDTO param, CancellationToken ct = default)
            => _channel.Writer.WriteAsync(param, ct);

        /// <summary>
        /// Returns an async enumerable that yields items as they are enqueued.
        /// The stream completes when <see cref="DisposeAsync"/> is called.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public IAsyncEnumerable<ScreenShotParamDTO> ReadAllAsync(CancellationToken ct = default)
            => _channel.Reader.ReadAllAsync(ct);

        /// <summary>
        /// Signals the channel writer as complete.
        /// Any items already in the channel will still be consumed before the reader exits.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
