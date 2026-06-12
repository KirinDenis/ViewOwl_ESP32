using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Data access contract for <see cref="GrabLog"/> records.
    /// </summary>
    public interface IGrabLogRepository
    {
        /// <summary>
        /// Creates a new pending grab log record and returns it.
        /// </summary>
        Task<GrabLog> CreateAsync(
            int templateId, Guid tokenGuid,
            int width = 480, int height = 320,
            CancellationToken ct = default);

        /// <summary>
        /// Marks an existing log as completed (success or failure).
        /// No-op if the record is not found.
        /// </summary>
        Task MarkCompletedAsync(
            int id, bool success,
            string? message = null,
            CancellationToken ct = default);

        /// <summary>
        /// Returns the most recent <paramref name="count"/> grab logs for
        /// <paramref name="templateId"/>, newest first (no-tracking).
        /// </summary>
        Task<IReadOnlyList<GrabLog>> GetRecentAsync(
            int templateId, int count = 20,
            CancellationToken ct = default);

        /// <summary>
        /// Returns the most recent successful grab log for
        /// <paramref name="templateId"/>, or <c>null</c> if none exist.
        /// </summary>
        Task<GrabLog?> GetLastSuccessfulAsync(
            int templateId,
            CancellationToken ct = default);

        /// <summary>
        /// Returns the most recent successful grab log for <paramref name="templateId"/>
        /// whose captured dimensions exactly match <paramref name="width"/> × <paramref name="height"/>.
        /// Falls back to the dimension-agnostic overload when no dimension-matching log is found,
        /// so callers always receive a usable frame even on the first cycle after a resolution change.
        /// </summary>
        Task<GrabLog?> GetLastSuccessfulAsync(
            int templateId,
            int width,
            int height,
            CancellationToken ct = default);

        /// <summary>
        /// Returns the total count of successful grab log entries for <paramref name="templateId"/>.
        /// </summary>
        Task<int> GetSuccessfulCountAsync(int templateId, CancellationToken ct = default);

        /// <summary>
        /// Returns the count of grab log entries (all outcomes) for <paramref name="templateId"/>
        /// since UTC midnight today, plus the count of successful ones.
        /// Used to compute grabs-today and today's success rate for the device status display.
        /// </summary>
        Task<(int Total, int Successful)> GetTodayStatsAsync(int templateId, CancellationToken ct = default);

        /// <summary>
        /// Returns all successful grab logs for <paramref name="templateId"/> at the given
        /// resolution, ordered newest-first, skipping the most recent
        /// <paramref name="keepCount"/> entries.
        /// Used by <c>DeviceTemplateRefreshWorker</c> to find exchange-folder files that are
        /// safe to delete before a new grab is enqueued.
        /// </summary>
        Task<IReadOnlyList<GrabLog>> GetStaleSuccessfulAsync(
            int templateId, int width, int height,
            int keepCount,
            CancellationToken ct = default);

        /// <summary>
        /// Permanently deletes a grab log record by primary key.
        /// No-op if the record does not exist.
        /// </summary>
        Task DeleteAsync(int id, CancellationToken ct = default);
    }
}
