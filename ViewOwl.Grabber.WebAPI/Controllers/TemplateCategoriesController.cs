using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ViewOwl.Data;
using ViewOwl.Data.Models;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// CRUD endpoints for template display categories.
    /// List is public to all authenticated users; mutations are admin-only.
    /// </summary>
    [ApiController]
    [Route("api/template-categories")]
    [Authorize]
    public sealed class TemplateCategoriesController : ControllerBase
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the controller with a database context.
        /// </summary>
        /// <param name="db">EF Core database context.</param>
        public TemplateCategoriesController(ViewOwlDbContext db)
        {
            _db = db;
        }

        // ── Endpoints ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all template categories ordered by slug.
        /// Available to all authenticated users so the dashboard can show category filters.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet]
        [ProducesResponseType(typeof(IReadOnlyList<TemplateCategoryDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAll(CancellationToken ct = default)
        {
            var categories = await _db.TemplateCategories
                .OrderBy(c => c.Slug)
                .Select(c => new TemplateCategoryDto(
                    c.Id,
                    c.Label,
                    c.Slug,
                    c.IsSystem,
                    0))     // templateCount: computed on demand, not stored
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Ok(categories);
        }

        /// <summary>Creates a new template category. Admin-only.</summary>
        /// <param name="request">Category label; slug is derived automatically.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost]
        [ProducesResponseType(typeof(TemplateCategoryDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Create(
            [FromBody] CreateCategoryRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!IsAdmin())
                return Forbid();

            string slug = GenerateSlug(request.Label);

            if (await _db.TemplateCategories.AnyAsync(c => c.Slug == slug, ct).ConfigureAwait(false))
                return Conflict(new { error = $"A category with slug '{slug}' already exists." });

            var category = new TemplateCategory
            {
                Label     = request.Label.Trim(),
                Slug      = slug,
                IsSystem  = false,
                CreatedAt = DateTime.UtcNow,
            };

            _db.TemplateCategories.Add(category);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return CreatedAtAction(nameof(GetAll), TemplateCategoryDto.From(category, 0));
        }

        /// <summary>
        /// Renames a category label. The slug is never changed. Admin-only.
        /// </summary>
        /// <param name="id">Category id.</param>
        /// <param name="request">New label text.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPut("{id:int}")]
        [ProducesResponseType(typeof(TemplateCategoryDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Rename(
            int id,
            [FromBody] RenameCategoryRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!IsAdmin())
                return Forbid();

            TemplateCategory? category = await _db.TemplateCategories
                .FindAsync([id], ct)
                .ConfigureAwait(false);

            if (category is null)
                return NotFound();

            category.Label = request.Label.Trim();
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return Ok(TemplateCategoryDto.From(category, 0));
        }

        /// <summary>
        /// Deletes a category. System categories cannot be deleted. Admin-only.
        /// Templates that use this category via <c>data-vow-category</c> in their HTML
        /// are not modified — they will simply resolve to "other" on the client.
        /// </summary>
        /// <param name="id">Category id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpDelete("{id:int}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(int id, CancellationToken ct = default)
        {
            if (!IsAdmin())
                return Forbid();

            TemplateCategory? category = await _db.TemplateCategories
                .FindAsync([id], ct)
                .ConfigureAwait(false);

            if (category is null)
                return NoContent(); // idempotent

            if (category.IsSystem)
                return Conflict(new { error = "System categories cannot be deleted." });

            _db.TemplateCategories.Remove(category);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return NoContent();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private bool IsAdmin() =>
            User.FindFirstValue(ClaimTypes.Role) == "Admin"
            || User.IsInRole("Admin");

        /// <summary>
        /// Derives a URL-safe lowercase slug from a display label.
        /// Spaces become hyphens; non-alphanumeric characters are removed.
        /// </summary>
        private static string GenerateSlug(string label)
        {
            string lower = label.Trim().ToLowerInvariant();
            // Replace whitespace runs with a single hyphen.
            string hyphenated = Regex.Replace(lower, @"\s+", "-");
            // Strip anything that is not a letter, digit, or hyphen.
            string clean = Regex.Replace(hyphenated, @"[^a-z0-9\-]", string.Empty);
            // Collapse multiple consecutive hyphens.
            return Regex.Replace(clean, @"-{2,}", "-").Trim('-');
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    /// <summary>Template category response.</summary>
    public sealed record TemplateCategoryDto(
        int Id,
        string Label,
        string Slug,
        bool IsSystem,
        int TemplateCount)
    {
        /// <summary>Maps a <see cref="TemplateCategory"/> entity to a DTO.</summary>
        public static TemplateCategoryDto From(TemplateCategory c, int templateCount)
        {
            ArgumentNullException.ThrowIfNull(c);
            return new(c.Id, c.Label, c.Slug, c.IsSystem, templateCount);
        }
    }

    /// <summary>Create-category request body.</summary>
    public sealed record CreateCategoryRequestDto(string Label);

    /// <summary>Rename-category request body.</summary>
    public sealed record RenameCategoryRequestDto(string Label);
}
