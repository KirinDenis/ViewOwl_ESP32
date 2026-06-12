using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ViewOwl.Config;
using ViewOwl.Data;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.DTO;
using ViewOwl.Grabber.Engine;
using ViewOwl.Grabber.WebAPI.Hubs;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// User-scoped template management endpoints.
    /// Each authenticated user can only see and manage their own templates.
    /// </summary>
    [ApiController]
    [Route("api/templates")]
    [Authorize]
    public sealed class TemplatesController : ControllerBase
    {
        private readonly ITemplateRepository _templates;
        private readonly IGrabLogRepository _grabLogs;
        private readonly IGrabber _grabber;
        private readonly GrabChannel _channel;
        private readonly SharedConfig _sharedConfig;
        private readonly IHubContext<ViewOwlHub> _hub;
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the controller with its dependencies.
        /// </summary>
        /// <param name="templates">Template data access.</param>
        /// <param name="grabLogs">Grab log data access.</param>
        /// <param name="grabber">Headless browser grabber for on-demand captures.</param>
        /// <param name="channel">Screenshot request channel shared with <c>GrabWorker</c>.</param>
        /// <param name="sharedOptions">Shared configuration (ExchangeFolder path).</param>
        public TemplatesController(
            ITemplateRepository templates,
            IGrabLogRepository grabLogs,
            IGrabber grabber,
            GrabChannel channel,
            IOptions<SharedConfig> sharedOptions,
            IHubContext<ViewOwlHub> hub,
            ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            _templates    = templates;
            _grabLogs     = grabLogs;
            _grabber      = grabber;
            _channel      = channel;
            _sharedConfig = sharedOptions.Value;
            _hub          = hub;
            _db           = db;
        }

        // ── Collection endpoints ──────────────────────────────────────────────

        /// <summary>
        /// Returns all templates visible to the authenticated user.
        /// Admins receive every template in the database; regular users see only their own.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet]
        [ProducesResponseType(typeof(IReadOnlyList<TemplateDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAll(CancellationToken ct = default)
        {
            int userId = GetUserId();
            var list = IsAdmin()
                ? await _templates.GetAllAsync(ct).ConfigureAwait(false)
                : await _templates.GetByUserAsync(userId, ct).ConfigureAwait(false);
            return Ok(list.Select(t => new TemplateDto(
                t.Id, t.Name, t.UserId, t.CreatedAt,
                ExtractVowAttr(t.HtmlContent ?? string.Empty, "class",    "A"),
                ExtractVowAttr(t.HtmlContent ?? string.Empty, "category", "other"))));
        }

        /// <summary>Creates a new template. Admin-only.</summary>
        /// <param name="request">Template details including HTML content.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost]
        [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Create(
            [FromBody] CreateTemplateRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!IsAdmin())
                return Forbid();

            int userId = GetUserId();

            if (await _templates.NameExistsForUserAsync(userId, request.Name, ct: ct).ConfigureAwait(false))
                return Conflict(new { error = $"A template named '{request.Name}' already exists." });

            var template = new Template
            {
                Name        = request.Name,
                HtmlContent = request.HtmlContent,
                UserId      = userId
            };

            var created = await _templates.CreateAsync(template, ct).ConfigureAwait(false);
            var dto = TemplateDto.From(created);
            await _hub.Clients.Group(ViewOwlHub.UserGroup(userId))
                      .SendAsync("TemplateCreated", dto, ct)
                      .ConfigureAwait(false);
            return CreatedAtAction(nameof(GetAll), dto);
        }

        // ── Single-item endpoints ─────────────────────────────────────────────

        /// <summary>
        /// Returns a single template including its full HTML content.
        /// Admins can read any template; regular users can only read their own.
        /// </summary>
        /// <param name="id">Template id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{id:int}")]
        [ProducesResponseType(typeof(TemplateDetailDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetById(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var template = await _templates.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (template is null)
                return NotFound();

            if (template.UserId != userId && !IsAdmin())
                return Forbid();

            return Ok(TemplateDetailDto.From(template));
        }

        /// <summary>
        /// Updates the name and HTML content of an existing template. Admin-only.
        /// </summary>
        /// <param name="id">Template id.</param>
        /// <param name="request">New name and HTML content.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPut("{id:int}")]
        [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Update(
            int id,
            [FromBody] UpdateTemplateRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!IsAdmin())
                return Forbid();

            int userId = GetUserId();
            var template = await _templates.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (template is null)
                return NotFound();

            if (await _templates.NameExistsForUserAsync(userId, request.Name, excludeId: id, ct: ct).ConfigureAwait(false))
                return Conflict(new { error = $"A template named '{request.Name}' already exists." });

            template.Name        = request.Name;
            template.HtmlContent = request.HtmlContent;

            await _templates.UpdateAsync(template, ct).ConfigureAwait(false);
            var dto = TemplateDto.From(template);
            await _hub.Clients.Group(ViewOwlHub.UserGroup(userId))
                      .SendAsync("TemplateUpdated", dto, ct)
                      .ConfigureAwait(false);
            return Ok(dto);
        }

        /// <summary>Deletes a template by id. Admin-only.</summary>
        /// <param name="id">Template id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpDelete("{id:int}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Delete(int id, CancellationToken ct = default)
        {
            if (!IsAdmin())
                return Forbid();

            var template = await _templates.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (template is null)
                return NoContent(); // idempotent

            await _templates.DeleteAsync(id, ct).ConfigureAwait(false);

            // Broadcast to all clients so every open dashboard removes the deleted template.
            await _hub.Clients.All
                      .SendAsync("TemplateDeleted", id, ct)
                      .ConfigureAwait(false);
            return NoContent();
        }

        // ── Visibility endpoint ───────────────────────────────────────────────

        /// <summary>
        /// Toggles the <see cref="Template.IsPublic"/> flag without touching the
        /// template HTML or name. Admin-only.
        /// </summary>
        /// <param name="id">Template id.</param>
        /// <param name="request">New visibility value.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPatch("{id:int}/public")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> SetPublic(
            int id,
            [FromBody] SetPublicRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!IsAdmin())
                return Forbid();

            var template = await _templates.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (template is null)
                return NotFound();

            template.IsPublic = request.IsPublic;
            await _templates.UpdateAsync(template, ct).ConfigureAwait(false);
            return NoContent();
        }

        // ── Grab endpoints ────────────────────────────────────────────────────

        /// <summary>
        /// Queues an on-demand screenshot grab for the template.
        /// Creates a <c>GrabLog</c> record and enqueues a capture request.
        /// The capture executes asynchronously; poll <c>GET /{id}/log</c> for status.
        /// </summary>
        /// <param name="id">Template id.</param>
        /// <param name="request">Optional capture dimensions; defaults to 480×320.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("{id:int}/grab")]
        [ProducesResponseType(typeof(GrabLogDto), StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Grab(
            int id,
            [FromBody] GrabRequestDto? request,
            CancellationToken ct = default)
        {
            int userId = GetUserId();
            var template = await _templates.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (template is null)
                return NotFound();

            if (template.UserId != userId && !IsAdmin())
                return Forbid();

            int width  = request?.Width  ?? 480;
            int height = request?.Height ?? 320;

            // Write the template HTML to a well-known path so SiteTemplateController can serve it
            string sitesFolder = Path.Combine(_sharedConfig.ExchangeFolder, "SitesTemplates");
            Directory.CreateDirectory(sitesFolder);

            string htmlFileName = $"t{id}.html";
            string htmlFilePath = Path.Combine(sitesFolder, htmlFileName);
            await System.IO.File.WriteAllTextAsync(htmlFilePath, template.HtmlContent, ct)
                                .ConfigureAwait(false);

            Guid tokenGuid = Guid.NewGuid();

            // Create the GrabLog row in Pending state before touching the browser
            GrabLog log = await _grabLogs
                .CreateAsync(id, tokenGuid, width, height, ct)
                .ConfigureAwait(false);

            // Open a temporary browser tab for this template
            LoadPageResultDTO loadResult =
                await _grabber.AddUrlAsync($"SitesTemplates/{htmlFileName}").ConfigureAwait(false);

            if (!loadResult.Success)
            {
                // Propagate the actual engine error so the grab log is useful for debugging
                string engineError = loadResult.ErrorMessage ?? "Navigation returned a non-OK response.";
                await _grabLogs
                    .MarkCompletedAsync(log.Id, false, $"Browser failed to load the template: {engineError}", ct)
                    .ConfigureAwait(false);
                return StatusCode(StatusCodes.Status500InternalServerError, engineError);
            }

            var param = new ScreenShotParamDTO
            {
                TokenGuid            = tokenGuid,
                TabName              = loadResult.TabName,
                Width                = width,
                Height               = height,
                GrabLogId            = log.Id,
                TemplateId           = id,
                UserId               = userId,
                CloseTabAfterCapture = true,
                Monochrome           = template.Monochrome
            };

            await _channel.EnqueueAsync(param, ct).ConfigureAwait(false);

            var logDto = GrabLogDto.From(log);
            await _hub.Clients.Group(ViewOwlHub.UserGroup(userId))
                      .SendAsync("GrabStarted", logDto, ct)
                      .ConfigureAwait(false);

            return Accepted(logDto);
        }

        /// <summary>
        /// Returns the last 20 grab log entries for the template, newest first.
        /// </summary>
        /// <param name="id">Template id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{id:int}/log")]
        [ProducesResponseType(typeof(IReadOnlyList<GrabLogDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetLog(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var template = await _templates.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (template is null)
                return NotFound();

            if (template.UserId != userId && !IsAdmin())
                return Forbid();

            var logs = await _grabLogs.GetRecentAsync(id, 20, ct).ConfigureAwait(false);
            return Ok(logs.Select(GrabLogDto.From));
        }

        /// <summary>
        /// Returns the most recent successful grab as a PNG image.
        /// Decodes the BGR565 binary produced by the grabber back to an RGB PNG.
        /// Returns <c>404</c> when no successful grab exists yet.
        /// </summary>
        /// <param name="id">Template id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{id:int}/preview-image")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetPreviewImage(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            var template = await _templates.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (template is null)
                return NotFound();

            if (template.UserId != userId && !IsAdmin())
                return Forbid();

            GrabLog? lastLog = await _grabLogs.GetLastSuccessfulAsync(id, ct).ConfigureAwait(false);
            if (lastLog is null)
                return NotFound("No successful grab exists for this template yet.");

            string binPath = Path.Combine(_sharedConfig.ExchangeFolder, $"{lastLog.TokenGuid}.bin");
            if (!System.IO.File.Exists(binPath))
                return NotFound("Preview binary file not found on disk.");

            byte[] data = await System.IO.File.ReadAllBytesAsync(binPath, ct).ConfigureAwait(false);

            byte[] png = DecodeBgr565ToPng(data, lastLog.Width, lastLog.Height);
            return File(png, "image/png");
        }

        /// <summary>
        /// Returns grab and delivery statistics for a template.
        /// Grab count equals the number of successful screenshot captures.
        /// Delivery count is approximated as equal to grab count (each successful
        /// grab produces a frame that is delivered to devices).
        /// </summary>
        /// <param name="id">Template id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{id:int}/stats")]
        [ProducesResponseType(typeof(TemplateStatsDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetStats(int id, CancellationToken ct = default)
        {
            int userId = GetUserId();
            Template? template = await _templates.GetByIdAsync(id, ct).ConfigureAwait(false);

            if (template is null)
                return NotFound();

            if (template.UserId != userId && !IsAdmin())
                return Forbid();

            // Sequential queries — same scoped DbContext must not run concurrently
            int grabCount = await _grabLogs.GetSuccessfulCountAsync(id, ct).ConfigureAwait(false);
            GrabLog? lastLog = await _grabLogs.GetLastSuccessfulAsync(id, ct).ConfigureAwait(false);

            return Ok(new TemplateStatsDto(
                TemplateId: id,
                GrabCount: grabCount,
                LastGrabbed: lastLog?.CompletedAt,
                DeliveryCount: grabCount,
                LastDelivered: lastLog?.CompletedAt));
        }

        /// <summary>
        /// Returns the sorted list of <c>lw-*.html</c> starter file names from SitesTemplates.
        /// These pre-built lightweight templates serve as starting points for new grab templates.
        /// Individual starter content is served by <c>GET /SiteTemplate?name={file}</c>.
        /// </summary>
        [HttpGet("starters")]
        [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
        public IActionResult GetStarters()
        {
            string sitesFolder = Path.Combine(_sharedConfig.ExchangeFolder, "SitesTemplates");
            if (!Directory.Exists(sitesFolder))
                return Ok(Array.Empty<string>());

            string[] starters = Directory.GetFiles(sitesFolder, "lw-*.html")
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(static f => f)
                .ToArray();

            return Ok(starters);
        }

        // ── Export / Import ───────────────────────────────────────────────────

        /// <summary>
        /// Exports all templates as a ZIP archive containing one HTML file per template
        /// and an <c>index.json</c> manifest with category metadata.
        /// The archive is suitable for importing into another ViewOwl instance.
        /// Admin-only.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("export")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Export(CancellationToken ct = default)
        {
            if (!IsAdmin())
                return Forbid();

            var templates  = await _templates.GetAllAsync(ct).ConfigureAwait(false);
            var categories = await _db.TemplateCategories
                .OrderBy(static c => c.Slug)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Track used filenames so two templates with similar names don't collide.
                var usedFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var templateEntries = new List<TemplateExportEntry>();

                foreach (Template t in templates)
                {
                    string html     = t.HtmlContent ?? string.Empty;
                    string filename = MakeUniqueFilename(t.Name, usedFilenames);
                    usedFilenames.Add(filename);

                    ZipArchiveEntry htmlEntry = zip.CreateEntry(filename, CompressionLevel.Optimal);
                    await using StreamWriter writer = new(htmlEntry.Open(), Encoding.UTF8);
                    await writer.WriteAsync(html).ConfigureAwait(false);

                    // Embed class and category in the manifest so index.json is a
                    // human-readable inventory without needing to parse the HTML.
                    string vowClass  = ExtractVowAttr(html, "class",    "A");
                    string category  = ExtractVowAttr(html, "category", "other");

                    templateEntries.Add(new TemplateExportEntry(t.Name, filename, vowClass, category));
                }

                // Write index.json last so readers can rely on it being present.
                var manifest = new TemplateExportManifest(
                    Version: 1,
                    ExportedAt: DateTime.UtcNow.ToString("O"),
                    Categories: categories.Select(static c => new CategoryExportEntry(c.Slug, c.Label)).ToArray(),
                    Templates: [.. templateEntries]);

                ZipArchiveEntry indexEntry = zip.CreateEntry("index.json", CompressionLevel.Optimal);
                await using StreamWriter indexWriter = new(indexEntry.Open(), Encoding.UTF8);
                await indexWriter.WriteAsync(
                    JsonSerializer.Serialize(manifest, TemplateExportJsonContext.Default.TemplateExportManifest))
                    .ConfigureAwait(false);
            }

            string zipName = $"viewowl-templates-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip";
            return File(ms.ToArray(), "application/zip", zipName);
        }

        /// <summary>
        /// Imports templates from a ZIP archive produced by <c>GET /api/templates/export</c>.
        /// When <paramref name="dryRun"/> is <c>true</c> no writes are performed and the response
        /// describes what <em>would</em> happen: which templates would be created and which updated.
        /// When <paramref name="dryRun"/> is <c>false</c> (default) the upsert is executed inside a
        /// database transaction so a mid-import failure leaves the database unchanged.
        /// Categories referenced in the manifest are created automatically if they do not exist.
        /// Admin-only.
        /// </summary>
        /// <param name="file">ZIP archive uploaded as multipart/form-data.</param>
        /// <param name="dryRun">
        /// When <c>true</c>, validate the archive and return a preview without writing to the database.
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("import")]
        [ProducesResponseType(typeof(TemplateImportPreviewDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(TemplateImportResultDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Import(
            IFormFile file,
            [FromQuery] bool dryRun = false,
            CancellationToken ct = default)
        {
            if (!IsAdmin())
                return Forbid();

            if (file is null || file.Length == 0)
                return BadRequest(new { error = "No file uploaded." });

            if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Uploaded file must be a .zip archive." });

            int userId = GetUserId();
            var errors = new List<string>();

            using var stream = file.OpenReadStream();
            using var zip    = new ZipArchive(stream, ZipArchiveMode.Read);

            // ── Parse manifest ────────────────────────────────────────────────
            ZipArchiveEntry? indexEntry = zip.GetEntry("index.json");
            if (indexEntry is null)
                return BadRequest(new { error = "Invalid archive: missing index.json." });

            using var indexReader = new StreamReader(indexEntry.Open(), Encoding.UTF8);
            string indexJson = await indexReader.ReadToEndAsync(ct).ConfigureAwait(false);

            TemplateExportManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(
                    indexJson, TemplateExportJsonContext.Default.TemplateExportManifest);
            }
            catch (JsonException ex)
            {
                return BadRequest(new { error = $"Invalid archive: index.json is malformed. {ex.Message}" });
            }

            if (manifest is null)
                return BadRequest(new { error = "Invalid archive: index.json parsed as null." });

            // ── Pre-scan: read all HTML content and classify as create / update ──
            // Reads every file now so the ZipArchive can be disposed before we write
            // to the database, and so that the dry-run and actual paths share the
            // same scan logic without reading the stream twice.
            var toCreate        = new List<(string Name, string Html, ImportPreviewItem Meta)>();
            var toUpdate        = new List<(Template Existing, string Html, ImportPreviewItem Meta)>();

            foreach (TemplateExportEntry entry in manifest.Templates ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.File))
                {
                    errors.Add("Skipped entry with missing name or file field.");
                    continue;
                }

                ZipArchiveEntry? htmlEntry = zip.GetEntry(entry.File);
                if (htmlEntry is null)
                {
                    errors.Add($"'{entry.Name}': file '{entry.File}' not found in archive.");
                    continue;
                }

                using var htmlReader = new StreamReader(htmlEntry.Open(), Encoding.UTF8);
                string html = await htmlReader.ReadToEndAsync(ct).ConfigureAwait(false);

                var meta = new ImportPreviewItem(
                    Name:     entry.Name,
                    Class:    ExtractVowAttr(html, "class",    "A"),
                    Category: ExtractVowAttr(html, "category", "other"));

                Template? existing = await _templates.GetByNameAsync(entry.Name, ct).ConfigureAwait(false);
                if (existing is null)
                    toCreate.Add((entry.Name, html, meta));
                else
                    toUpdate.Add((existing, html, meta));
            }

            // ── Dry-run: return preview without touching the database ─────────
            if (dryRun)
            {
                return Ok(new TemplateImportPreviewDto(
                    WouldCreate: toCreate.Select(static x => x.Meta).ToList(),
                    WouldUpdate: toUpdate.Select(static x => x.Meta).ToList(),
                    Errors: errors));
            }

            // ── Upsert categories (idempotent, outside the template transaction) ─
            foreach (CategoryExportEntry cat in manifest.Categories ?? [])
            {
                if (string.IsNullOrWhiteSpace(cat.Slug))
                    continue;

                bool exists = await _db.TemplateCategories
                    .AnyAsync(c => c.Slug == cat.Slug, ct)
                    .ConfigureAwait(false);

                if (!exists)
                {
                    _db.TemplateCategories.Add(new TemplateCategory
                    {
                        Label     = string.IsNullOrWhiteSpace(cat.Label) ? cat.Slug : cat.Label,
                        Slug      = cat.Slug,
                        IsSystem  = false,
                        CreatedAt = DateTime.UtcNow,
                    });
                }
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            // ── Upsert templates in a single transaction ──────────────────────
            // A failure mid-way rolls back all template changes; categories are
            // already committed above and are safe to keep even on template failure.
            await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                foreach ((string name, string html, _) in toCreate)
                {
                    await _templates.CreateAsync(
                        new Template { Name = name, HtmlContent = html, UserId = userId },
                        ct).ConfigureAwait(false);
                }

                foreach ((Template existing, string html, _) in toUpdate)
                {
                    existing.HtmlContent = html;
                    await _templates.UpdateAsync(existing, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }

            return Ok(new TemplateImportResultDto(toCreate.Count, toUpdate.Count, errors));
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private int GetUserId()
        {
            string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            return int.TryParse(sub, out int id) ? id : 0;
        }

        /// <summary>
        /// Converts a template name to a safe filename, appending a numeric suffix
        /// if the resulting name is already taken in the current archive.
        /// </summary>
        private static string MakeUniqueFilename(string name, HashSet<string> used)
        {
            // Keep only safe chars; replace everything else with a dash.
            string safe = System.Text.RegularExpressions.Regex
                .Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9\-_]+", "-")
                .Trim('-');

            if (string.IsNullOrEmpty(safe))
                safe = "template";

            string candidate = safe + ".html";
            int n = 2;
            while (used.Contains(candidate))
                candidate = $"{safe}-{n++}.html";

            return candidate;
        }

        private bool IsAdmin() =>
            User.FindFirstValue(ClaimTypes.Role) == "Admin"
            || User.IsInRole("Admin");

        /// <summary>
        /// Extracts a single <c>data-vow-{attr}</c> value from raw HTML.
        /// Returns <paramref name="fallback"/> when the attribute is absent or empty.
        /// </summary>
        private static string ExtractVowAttr(string html, string attr, string fallback)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                html,
                @$"data-vow-{attr}=""([^""]+)""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string val = m.Success ? m.Groups[1].Value.Trim() : string.Empty;
            return string.IsNullOrEmpty(val) ? fallback : val;
        }

        /// <summary>
        /// Converts a frame buffer (any supported format) to a PNG-encoded byte array.
        /// Handles: legacy raw BGR565, raw+flag (0x00), palette+RLE (0x01), palette+LZ4 (0x03).
        /// Bit layout per pixel: bits[15:11]=R5, bits[10:5]=G6, bits[4:0]=B5.
        /// </summary>
        private static byte[] DecodeBgr565ToPng(byte[] data, int width, int height)
        {
            int pixelCount = width * height;
            byte[] raw = DecompressFrameToRaw(data, pixelCount);

            using var image = new Image<Rgb24>(width, height);
            int count = Math.Min(raw.Length / 2, pixelCount);

            for (int i = 0; i < count; i++)
            {
                // Each pixel: 2 bytes big-endian. bits[15:11]=R5, bits[10:5]=G6, bits[4:0]=B5.
                ushort v = (ushort)((raw[i * 2] << 8) | raw[i * 2 + 1]);
                byte r5 = (byte)((v >> 11) & 0x1F);
                byte g6 = (byte)((v >>  5) & 0x3F);
                byte b5 = (byte)( v        & 0x1F);

                // Scale to 8-bit by replicating MSBs into the vacated LSBs.
                byte r = (byte)(r5 << 3 | r5 >> 2);
                byte g = (byte)(g6 << 2 | g6 >> 4);
                byte b = (byte)(b5 << 3 | b5 >> 2);

                image[i % width, i / width] = new Rgb24(r, g, b);
            }

            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Decompresses any supported frame format to raw big-endian BGR565 (2 bytes per pixel).
        /// Handles: legacy raw (no flag), raw+flag (0x00), palette+RLE (0x01), palette+LZ4 (0x03).
        /// </summary>
        private static byte[] DecompressFrameToRaw(byte[] data, int pixelCount)
        {
            if (data.Length == 0)
                return data;

            // Legacy raw: exact pixel buffer with no flag prefix.
            if (data.Length == pixelCount * 2)
                return data;

            return data[0] switch
            {
                0x00 => data[1..],                              // raw with flag — skip flag byte
                0x01 => DecompressPaletteRle(data, pixelCount),
                0x03 => DecompressPaletteLz4(data, pixelCount),
                _    => data,                                   // unknown — pass through as-is
            };
        }

        /// <summary>
        /// Decompresses a palette+RLE frame (flag 0x01) to raw big-endian BGR565.
        /// Format: [0x01][palLen:u8][palette: palLen×2 big-endian][RLE: count:u8, idx:u8 pairs].
        /// </summary>
        private static byte[] DecompressPaletteRle(byte[] data, int pixelCount)
        {
            int palLen     = data[1];
            int paletteOff = 2;
            int rlePos     = paletteOff + palLen * 2;
            byte[] output  = new byte[pixelCount * 2];
            int outPos     = 0;

            while (outPos < output.Length && rlePos + 1 < data.Length)
            {
                int count = data[rlePos];
                int idx   = data[rlePos + 1];
                rlePos += 2;

                int palEntryOff = paletteOff + idx * 2;
                byte hi = data[palEntryOff];
                byte lo = data[palEntryOff + 1];

                for (int k = 0; k < count && outPos < output.Length; k++)
                {
                    output[outPos++] = hi;
                    output[outPos++] = lo;
                }
            }

            return output;
        }

        /// <summary>
        /// Decompresses a palette+LZ4 frame (flag 0x03) to raw big-endian BGR565.
        /// Format: [0x03][palLen:u8][palette: palLen×2 big-endian][block_count:uint16 LE][blocks].
        /// Each block: [size_field:uint16 LE][payload]; bit15 of size_field = stored (raw copy).
        /// </summary>
        private static byte[] DecompressPaletteLz4(byte[] data, int pixelCount)
        {
            const int lz4BlockSize = 4096;  // must match FrameCompressor.LZ4BlockIndexSize

            int palLen     = data[1];
            int paletteOff = 2;
            int pos        = paletteOff + palLen * 2;

            // block_count: uint16 little-endian
            int blockCount = data[pos] | (data[pos + 1] << 8);
            pos += 2;

            byte[] indices   = new byte[pixelCount];
            int    indicePos = 0;

            for (int b = 0; b < blockCount && indicePos < pixelCount; b++)
            {
                ushort sizeField  = (ushort)(data[pos] | (data[pos + 1] << 8));
                pos += 2;
                bool stored      = (sizeField & 0x8000) != 0;
                int  payloadSize = sizeField & 0x7FFF;
                int  targetSize  = Math.Min(lz4BlockSize, pixelCount - indicePos);

                if (stored)
                {
                    Buffer.BlockCopy(data, pos, indices, indicePos, payloadSize);
                    indicePos += payloadSize;
                }
                else
                {
                    int decoded = LZ4Codec.Decode(data, pos, payloadSize, indices, indicePos, targetSize);
                    if (decoded > 0)
                        indicePos += decoded;
                }

                pos += payloadSize;
            }

            // Map palette indices to raw big-endian BGR565 output.
            byte[] output = new byte[pixelCount * 2];
            for (int i = 0; i < pixelCount; i++)
            {
                int palEntryOff   = paletteOff + indices[i] * 2;
                output[i * 2]     = data[palEntryOff];
                output[i * 2 + 1] = data[palEntryOff + 1];
            }

            return output;
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Template list item (HTML content omitted for list efficiency).
    /// <c>VowClass</c> and <c>Category</c> are pre-extracted by the controller
    /// so the client can filter by class/category without fetching each template detail.
    /// </summary>
    public sealed record TemplateDto(int Id, string Name, int UserId, DateTime CreatedAt, string VowClass, string Category)
    {
        /// <summary>
        /// Convenience factory used by Create / Update / SignalR paths where
        /// the HTML is available on the entity. The list endpoint (<c>GetAll</c>)
        /// projects via <c>ExtractVowAttr</c> directly so it can reuse the shared
        /// controller helper without duplicating the regex.
        /// </summary>
        public static TemplateDto From(Template t)
        {
            ArgumentNullException.ThrowIfNull(t);
            // Inline the same regex used by ExtractVowAttr so Create/Update/SignalR
            // responses also carry accurate metadata.
            static string extract(string html, string attr, string fallback)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    html,
                    @$"data-vow-{attr}=""([^""]+)""",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string val = m.Success ? m.Groups[1].Value.Trim() : string.Empty;
                return string.IsNullOrEmpty(val) ? fallback : val;
            }
            string html = t.HtmlContent ?? string.Empty;
            return new(t.Id, t.Name, t.UserId, t.CreatedAt,
                extract(html, "class",    "A"),
                extract(html, "category", "other"));
        }
    }

    /// <summary>Template detail response including full HTML content.</summary>
    public sealed record TemplateDetailDto(int Id, string Name, int UserId, string HtmlContent, DateTime CreatedAt, bool IsPublic)
    {
        /// <summary>Maps a <see cref="Template"/> entity to a <see cref="TemplateDetailDto"/>.</summary>
        public static TemplateDetailDto From(Template t)
        {
            ArgumentNullException.ThrowIfNull(t);
            return new(t.Id, t.Name, t.UserId, t.HtmlContent, t.CreatedAt, t.IsPublic);
        }
    }

    /// <summary>Create-template request body.</summary>
    public sealed record CreateTemplateRequestDto(string Name, string HtmlContent);

    /// <summary>Request body for <c>PATCH /api/templates/{id}/public</c>.</summary>
    public sealed record SetPublicRequestDto(bool IsPublic);

    /// <summary>Update-template request body.</summary>
    public sealed record UpdateTemplateRequestDto(string Name, string HtmlContent);

    /// <summary>On-demand grab request body. All fields are optional.</summary>
    public sealed record GrabRequestDto(int? Width, int? Height);

    /// <summary>Grab log entry response.</summary>
    public sealed record GrabLogDto(
        int Id,
        int TemplateId,
        Guid TokenGuid,
        DateTime RequestedAt,
        DateTime? CompletedAt,
        bool? Success,
        string Message,
        int Width,
        int Height)
    {
        /// <summary>Maps a <see cref="GrabLog"/> entity to a <see cref="GrabLogDto"/>.</summary>
        public static GrabLogDto From(GrabLog g)
        {
            ArgumentNullException.ThrowIfNull(g);
            return new(g.Id, g.TemplateId, g.TokenGuid, g.RequestedAt,
                       g.CompletedAt, g.Success, g.Message, g.Width, g.Height);
        }
    }

    /// <summary>Template grab and delivery statistics.</summary>
    public sealed record TemplateStatsDto(
        int TemplateId,
        int GrabCount,
        DateTime? LastGrabbed,
        int DeliveryCount,
        DateTime? LastDelivered);

    // ── Import / Export DTOs ─────────────────────────────────────────────────

    /// <summary>Top-level manifest written to index.json inside the export ZIP.</summary>
    public sealed record TemplateExportManifest(
        int Version,
        string ExportedAt,
        IReadOnlyList<CategoryExportEntry> Categories,
        IReadOnlyList<TemplateExportEntry> Templates);

    /// <summary>Per-category entry in the export manifest.</summary>
    public sealed record CategoryExportEntry(string Slug, string Label);

    /// <summary>Per-template entry in the export manifest.</summary>
    public sealed record TemplateExportEntry(string Name, string File, string Class, string Category);

    /// <summary>Response returned by <c>POST /api/templates/import</c>.</summary>
    public sealed record TemplateImportResultDto(
        int Created,
        int Updated,
        IReadOnlyList<string> Errors);

    /// <summary>Per-template summary item inside <see cref="TemplateImportPreviewDto"/>.</summary>
    public sealed record ImportPreviewItem(string Name, string Class, string Category);

    /// <summary>
    /// Dry-run preview returned by <c>POST /api/templates/import?dryRun=true</c>.
    /// Describes what the actual import <em>would</em> do without writing anything.
    /// </summary>
    public sealed record TemplateImportPreviewDto(
        IReadOnlyList<ImportPreviewItem> WouldCreate,
        IReadOnlyList<ImportPreviewItem> WouldUpdate,
        IReadOnlyList<string> Errors);

    /// <summary>JSON source-generation context for export / import payloads.</summary>
    [System.Text.Json.Serialization.JsonSerializable(typeof(TemplateExportManifest))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TemplateImportResultDto))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(TemplateImportPreviewDto))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(ImportPreviewItem))]
    internal sealed partial class TemplateExportJsonContext
        : System.Text.Json.Serialization.JsonSerializerContext { }
}
