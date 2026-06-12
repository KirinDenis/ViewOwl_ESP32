using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Data access contract for <see cref="GuestDevice"/> entities.
    /// </summary>
    public interface IGuestDeviceRepository
    {
        /// <summary>
        /// Creates a new guest device record and returns the persisted entity.
        /// </summary>
        /// <param name="templateId">
        /// DB primary key of the selected <c>Template</c>, or <see langword="null"/> for
        /// legacy devices that reference a static file by name only.
        /// </param>
        Task<GuestDevice> CreateAsync(
            Guid guid, string templateName, int? templateId,
            int displayWidth, int displayHeight,
            CancellationToken ct = default);

        /// <summary>
        /// Returns the guest device with the given GUID, or <see langword="null"/> if not found.
        /// </summary>
        Task<GuestDevice?> GetByGuidAsync(Guid guid, CancellationToken ct = default);

        /// <summary>
        /// Returns all guest devices, ordered by creation date descending.
        /// </summary>
        Task<IReadOnlyList<GuestDevice>> GetAllAsync(CancellationToken ct = default);

        /// <summary>
        /// Updates the stored template name (and optional DB template id) for the device so
        /// <see cref="GuestDeviceRefreshWorker"/> picks up the new template on its next cycle.
        /// No-op if the record does not exist.
        /// </summary>
        Task UpdateTemplateAsync(int id, string templateName, int? templateId, CancellationToken ct = default);

        /// <summary>
        /// Updates <see cref="GuestDevice.LastGrabbedAt"/> to the current UTC time.
        /// No-op if the record does not exist.
        /// </summary>
        Task MarkGrabbedAsync(int id, CancellationToken ct = default);

        /// <summary>
        /// Deletes all guest devices assigned to the same template, except the device
        /// identified by <paramref name="keepGuid"/>.
        /// When <paramref name="templateId"/> is set, matches by template DB id;
        /// otherwise matches by <paramref name="templateName"/> (case-insensitive).
        /// Returns the GUIDs of deleted records so the caller can clean up exchange files.
        /// </summary>
        Task<IReadOnlyList<Guid>> DeleteStaleForTemplateAsync(
            int? templateId, string templateName, Guid keepGuid, CancellationToken ct = default);
    }
}
