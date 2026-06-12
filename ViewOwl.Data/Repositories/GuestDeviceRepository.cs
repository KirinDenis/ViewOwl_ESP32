using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IGuestDeviceRepository"/>.
    /// </summary>
    public sealed class GuestDeviceRepository : IGuestDeviceRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public GuestDeviceRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<GuestDevice> CreateAsync(
            Guid guid, string templateName, int? templateId,
            int displayWidth, int displayHeight,
            CancellationToken ct = default)
        {
            var device = new GuestDevice
            {
                Guid          = guid,
                TemplateName  = templateName,
                TemplateId    = templateId,
                DisplayWidth  = displayWidth,
                DisplayHeight = displayHeight,
                CreatedAt     = DateTime.UtcNow,
            };

            _db.GuestDevices.Add(device);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return device;
        }

        /// <inheritdoc/>
        public async Task<GuestDevice?> GetByGuidAsync(Guid guid, CancellationToken ct = default)
            => await _db.GuestDevices
                        .AsNoTracking()
                        .FirstOrDefaultAsync(g => g.Guid == guid, ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<GuestDevice>> GetAllAsync(CancellationToken ct = default)
            => await _db.GuestDevices
                        .AsNoTracking()
                        .OrderByDescending(g => g.CreatedAt)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task UpdateTemplateAsync(int id, string templateName, int? templateId, CancellationToken ct = default)
        {
            GuestDevice? device = await _db.GuestDevices
                                           .FindAsync([id], ct)
                                           .ConfigureAwait(false);

            if (device is null)
                return;

            device.TemplateName = templateName;
            device.TemplateId   = templateId;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task MarkGrabbedAsync(int id, CancellationToken ct = default)
        {
            GuestDevice? device = await _db.GuestDevices
                                           .FindAsync([id], ct)
                                           .ConfigureAwait(false);

            if (device is null)
                return;

            device.LastGrabbedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Guid>> DeleteStaleForTemplateAsync(
            int? templateId, string templateName, Guid keepGuid, CancellationToken ct = default)
        {
            List<GuestDevice> stale;

            if (templateId.HasValue)
            {
                // Primary match: same DB template id, different GUID.
                stale = await _db.GuestDevices
                                 .Where(g => g.TemplateId == templateId.Value && g.Guid != keepGuid)
                                 .ToListAsync(ct)
                                 .ConfigureAwait(false);
            }
            else
            {
                // Legacy match: same template name, different GUID.
                // TemplateName is always stored via Path.GetFileName() so casing is consistent
                // on both ends — a plain ordinal comparison is correct and avoids locale issues.
                stale = await _db.GuestDevices
                                 .Where(g => g.TemplateId == null &&
                                             g.TemplateName == templateName &&
                                             g.Guid != keepGuid)
                                 .ToListAsync(ct)
                                 .ConfigureAwait(false);
            }

            if (stale.Count == 0)
                return Array.Empty<Guid>();

            _db.GuestDevices.RemoveRange(stale);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return stale.ConvertAll(g => g.Guid);
        }
    }
}
