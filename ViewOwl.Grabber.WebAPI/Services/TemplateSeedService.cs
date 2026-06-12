using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ViewOwl.Data;
using ViewOwl.Data.Models;

namespace ViewOwl.Grabber.WebAPI.Services
{
    /// <summary>
    /// One-time startup seeder — imports every <c>lw-*.html</c> and <c>sf-*.html</c>
    /// file found in the <c>SitesTemplates</c> folder into the database.
    ///
    /// Rules:
    ///   • Only runs when at least one matching file exists.
    ///   • Skips templates whose name already exists in the DB (idempotent).
    ///   • Auto-creates missing <see cref="TemplateCategory"/> rows.
    ///   • Assigns all templates to the first Admin user found.
    /// </summary>
    public static class TemplateSeedService
    {
        // Built-in categories that must exist before templates are inserted.
        private static readonly (string Slug, string Label, bool IsSystem)[] _knownCategories =
        [
            ("ambient",   "Ambient",   false),
            ("clock",     "Clock",     false),
            ("rates",     "Rates",     false),
            ("scifi",     "Sci-Fi",    false),
            ("system",    "System",    false),
            ("transport", "Transport", false),
            ("weather",   "Weather",   false),
            ("other",     "Other",     true),   // fallback — IsSystem prevents deletion
        ];

        private static readonly Regex _nameRx = new(@"data-vow-name=""([^""]+)""", RegexOptions.Compiled);

        /// <summary>
        /// Runs the seed operation using a scoped <see cref="ViewOwlDbContext"/>.
        /// Safe to call on every startup — skips already-imported templates.
        /// </summary>
        public static async Task SeedAsync(
            ViewOwlDbContext db,
            string sitesTemplatesPath,
            ILogger logger,
            CancellationToken ct = default)
        {
            // Collect all HTML templates, excluding the browser warm-up page.
            string[] files = Directory.GetFiles(sitesTemplatesPath, "*.html")
                .Where(f => !Path.GetFileName(f).Equals("servicepage.html", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (files.Length == 0)
            {
                logger.LogInformation("TemplateSeed: no .html files found in {Path}.", sitesTemplatesPath);
                return;
            }

            // Resolve admin user — all seeded templates are owned by the first admin.
            var adminUser = await db.Users
                .Where(u => u.Role == UserRole.Admin)
                .OrderBy(u => u.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (adminUser is null)
            {
                logger.LogWarning("TemplateSeed: no admin user found — skipping seed.");
                return;
            }

            // Ensure all known categories exist.
            await EnsureCategoriesAsync(db, logger, ct).ConfigureAwait(false);

            // Load existing template names for the admin to avoid duplicates.
            var existingNamesList = await db.Templates
                .Where(t => t.UserId == adminUser.Id)
                .Select(t => t.Name)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var existingNames = new HashSet<string>(existingNamesList, StringComparer.OrdinalIgnoreCase);

            int added = 0;
            int skipped = 0;

            foreach (string filePath in files.OrderBy(f => f))
            {
                string html = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);

                string name = ParseAttr(_nameRx, html)
                    ?? Path.GetFileNameWithoutExtension(filePath); // fallback to filename

                if (existingNames.Contains(name))
                {
                    skipped++;
                    continue;
                }

                var template = new Template
                {
                    UserId      = adminUser.Id,
                    Name        = name,
                    HtmlContent = html,
                    CreatedAt   = DateTime.UtcNow,
                };

                db.Templates.Add(template);
                existingNames.Add(name); // prevent duplicate within the same run
                added++;
            }

            if (added > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                logger.LogInformation(
                    "TemplateSeed: imported {Added} templates ({Skipped} already existed).",
                    added, skipped);
            }
            else
            {
                logger.LogInformation(
                    "TemplateSeed: all {Count} templates already in DB — nothing to import.",
                    skipped);
            }
        }

        /// <summary>
        /// Creates any <see cref="TemplateCategory"/> rows that don't yet exist,
        /// matching on <see cref="TemplateCategory.Slug"/>.
        /// </summary>
        private static async Task EnsureCategoriesAsync(
            ViewOwlDbContext db,
            ILogger logger,
            CancellationToken ct)
        {
            var existingSlugsList = await db.TemplateCategories
                .Select(c => c.Slug)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var existingSlugs = new HashSet<string>(existingSlugsList, StringComparer.OrdinalIgnoreCase);

            foreach (var (slug, label, isSystem) in _knownCategories)
            {
                if (existingSlugs.Contains(slug))
                    continue;

                db.TemplateCategories.Add(new TemplateCategory
                {
                    Slug      = slug,
                    Label     = label,
                    IsSystem  = isSystem,
                    CreatedAt = DateTime.UtcNow,
                });

                logger.LogInformation("TemplateSeed: created category '{Slug}'.", slug);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Returns the first capture group of <paramref name="rx"/> applied to
        /// <paramref name="html"/>, or <c>null</c> when the pattern does not match.</summary>
        private static string? ParseAttr(Regex rx, string html)
        {
            var m = rx.Match(html);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
