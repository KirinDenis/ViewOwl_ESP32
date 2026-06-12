using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Persistence operations for <see cref="PipelineEvent"/> records.
    /// </summary>
    public interface IPipelineEventRepository
    {
        /// <summary>
        /// Appends a new pipeline event row to the database.
        /// </summary>
        /// <param name="ev">The event to persist.</param>
        /// <param name="ct">Cancellation token.</param>
        Task AddAsync(PipelineEvent ev, CancellationToken ct = default);

        /// <summary>
        /// Returns the most recent pipeline events, optionally filtered by device, template, or type.
        /// Results are ordered by timestamp descending (newest first).
        /// </summary>
        /// <param name="deviceId">When non-null, only return events for this device.</param>
        /// <param name="templateId">When non-null, only return events for this template.</param>
        /// <param name="eventType">When non-null, only return events of this type.</param>
        /// <param name="since">When non-null, only return events after this UTC timestamp.</param>
        /// <param name="limit">Maximum number of rows to return (default 200).</param>
        /// <param name="ct">Cancellation token.</param>
        Task<IReadOnlyList<PipelineEvent>> GetRecentAsync(
            int?     deviceId   = null,
            int?     templateId = null,
            string?  eventType  = null,
            DateTime? since     = null,
            int      limit      = 200,
            CancellationToken ct = default);

        /// <summary>
        /// Returns recent events where <see cref="PipelineEvent.TemplateId"/> is in
        /// <paramref name="templateIds"/> OR <see cref="PipelineEvent.DeviceId"/> is in
        /// <paramref name="deviceIds"/>. Used to build the 4-lane pipeline summary in
        /// a single database round-trip.
        /// Results are ordered newest-first.
        /// </summary>
        Task<IReadOnlyList<PipelineEvent>> GetForEntitiesAsync(
            IReadOnlyList<int> templateIds,
            IReadOnlyList<int> deviceIds,
            int limit = 500,
            CancellationToken ct = default);
    }
}
