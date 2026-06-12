using ViewOwl.Grabber.WebAPI.DTOs;

namespace ViewOwl.Grabber.WebAPI.Services
{
    /// <summary>
    /// Computes per-template pipeline health for the ISA-101 dashboard.
    /// </summary>
    public interface ITemplateHealthService
    {
        /// <summary>
        /// Returns health information for all templates visible to the specified user.
        /// Results are computed in a single database query (no N+1).
        /// </summary>
        /// <param name="userId">The authenticated user's id — only their templates are returned.</param>
        /// <param name="ct">Cancellation token.</param>
        Task<IReadOnlyList<TemplateHealthDto>> GetAllAsync(int userId, CancellationToken ct = default);
    }
}
