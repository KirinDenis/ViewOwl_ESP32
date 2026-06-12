using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Data access contract for <see cref="Template"/> entities.
    /// </summary>
    public interface ITemplateRepository
    {
        /// <summary>Returns every template in the database (no-tracking). Admin use only.</summary>
        Task<IReadOnlyList<Template>> GetAllAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns all templates where <see cref="Template.IsPublic"/> is <c>true</c>,
        /// ordered by name. Used by the public landing page gallery endpoint.
        /// </summary>
        Task<IReadOnlyList<Template>> GetPublicAsync(CancellationToken ct = default);

        /// <summary>Returns all templates owned by <paramref name="userId"/> (no-tracking).</summary>
        Task<IReadOnlyList<Template>> GetByUserAsync(int userId, CancellationToken ct = default);

        /// <summary>Returns the template with the given <paramref name="id"/>, or <c>null</c>.</summary>
        Task<Template?> GetByIdAsync(int id, CancellationToken ct = default);

        /// <summary>
        /// Returns the first template whose <see cref="Template.Name"/> exactly matches
        /// <paramref name="name"/> (case-insensitive), or <c>null</c> when not found.
        /// Used by the public preview endpoint to resolve a filename to a template record.
        /// </summary>
        Task<Template?> GetByNameAsync(string name, CancellationToken ct = default);

        /// <summary>
        /// Returns <c>true</c> when <paramref name="userId"/> already owns a template named
        /// <paramref name="name"/> (case-insensitive), optionally excluding <paramref name="excludeId"/>.
        /// </summary>
        Task<bool> NameExistsForUserAsync(int userId, string name, int? excludeId = null, CancellationToken ct = default);

        /// <summary>Persists a new template and returns the saved entity.</summary>
        Task<Template> CreateAsync(Template entity, CancellationToken ct = default);

        /// <summary>Saves changes to an existing template.</summary>
        Task UpdateAsync(Template entity, CancellationToken ct = default);

        /// <summary>Deletes the template with the given <paramref name="id"/>. No-op if not found.</summary>
        Task DeleteAsync(int id, CancellationToken ct = default);

        /// <summary>
        /// Returns all templates that are currently set as the active template
        /// of at least one device (no-tracking).
        /// Used by <c>DeviceTemplateRefreshWorker</c> to auto-refresh assigned templates.
        /// </summary>
        Task<IReadOnlyList<Template>> GetAssignedToDevicesAsync(CancellationToken ct = default);
    }
}
