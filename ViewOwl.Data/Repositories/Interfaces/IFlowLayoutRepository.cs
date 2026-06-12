using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Persistence operations for per-user React Flow canvas layouts.
    /// </summary>
    public interface IFlowLayoutRepository
    {
        /// <summary>
        /// Returns the layout row for the given user, or <c>null</c> if none exists yet.
        /// </summary>
        /// <param name="userId">The user's database ID.</param>
        /// <param name="ct">Cancellation token.</param>
        Task<FlowLayout?> GetByUserAsync(int userId, CancellationToken ct = default);

        /// <summary>
        /// Inserts or updates the positions JSON for the given user.
        /// </summary>
        /// <param name="userId">The user's database ID.</param>
        /// <param name="positionsJson">Serialised node positions.</param>
        /// <param name="ct">Cancellation token.</param>
        Task UpsertAsync(int userId, string positionsJson, CancellationToken ct = default);
    }
}
