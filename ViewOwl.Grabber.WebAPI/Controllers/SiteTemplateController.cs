using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.DTO;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Serves local HTML templates to headless Chromium via HTTP.
    /// Chromium on ARM64 cannot open <c>file://</c> URLs; routing through this controller
    /// is the workaround.
    /// Also exposes a public <c>GET /SiteTemplate/preview</c> endpoint that returns the most
    /// recent grabbed frame as a PNG — used by the landing page gallery thumbnails.
    /// </summary>
    /// <remarks>
    /// See <see href="../docs/architecture.md">docs/architecture.md</see> — "Why HTTP instead
    /// of file://" for the full explanation of this design decision.
    /// </remarks>
    [ApiController]
    [Route("[controller]")]
    public class SiteTemplateController : ControllerBase
    {
        private const string SitesTemplatesFolder = "SitesTemplates";

        // Matches t{id}.html filenames written by the Grab endpoint.
        private static readonly Regex TemplateFilePattern = new(@"^t(\d+)\.html$", RegexOptions.Compiled);

        private readonly ILogger<SiteTemplateController> _logger;
        private readonly SharedConfig _sharedConfig;
        private readonly ITemplateRepository _templates;

        private static readonly JsonSerializerOptions _jsonOpts =
            new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Initialises the controller with its dependencies.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="sharedConfig">Shared configuration (ExchangeFolder path).</param>
        /// <param name="templates">Template repository — used as DB fallback when the cache file is missing.</param>
        public SiteTemplateController(
            ILogger<SiteTemplateController> logger,
            IOptions<SharedConfig> sharedConfig,
            ITemplateRepository templates)
        {
            ArgumentNullException.ThrowIfNull(sharedConfig);
            ArgumentNullException.ThrowIfNull(templates);
            _logger       = logger;
            _sharedConfig = sharedConfig.Value;
            _templates    = templates;
        }

        /// <summary>
        /// Returns an HTML file from ExchangeFolder/SitesTemplates/ as text/html.
        /// Chrome calls this instead of opening the file directly via file://.
        /// Example: GET /SiteTemplate?name=mypage.html
        /// <para>
        /// For user-created templates (<c>t{id}.html</c>): if the cache file is missing on disk
        /// (e.g. after a service restart or a failed grab), the HTML is read directly from the
        /// database so Chrome can still render the template. The cache file is also regenerated
        /// in that case so subsequent requests hit the disk.
        /// </para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string? name, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _logger.LogWarning("SiteTemplate: 'name' parameter is empty.");
                return BadRequest("Parameter 'name' is required.");
            }

            // Prevent path traversal: allow only the filename, no subdirectories
            var safeName = Path.GetFileName(name);
            if (string.IsNullOrEmpty(safeName) || safeName != name)
            {
                _logger.LogWarning("SiteTemplate: suspicious 'name' value: {Name}", name);
                return BadRequest("Invalid file name.");
            }

            var filePath = Path.Combine(_sharedConfig.ExchangeFolder, SitesTemplatesFolder, safeName);

            if (System.IO.File.Exists(filePath))
            {
                var content = await System.IO.File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                return Content(content, "text/html");
            }

            // File not found on disk — check whether this is a user template (t{id}.html)
            // and fall back to the DB. The disk file is just a cache; the DB is authoritative.
            var match = TemplateFilePattern.Match(safeName);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int templateId))
            {
                var template = await _templates.GetByIdAsync(templateId, ct).ConfigureAwait(false);
                if (template is not null && !string.IsNullOrEmpty(template.HtmlContent))
                {
                    _logger.LogWarning(
                        "SiteTemplate: cache file missing for template {Id}, serving from DB and regenerating.",
                        templateId);

                    // Regenerate the cache file so subsequent grabs work normally.
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                        await System.IO.File.WriteAllTextAsync(filePath, template.HtmlContent, ct)
                                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Non-fatal — Chrome still gets the HTML from the response.
                        _logger.LogError(ex, "SiteTemplate: failed to regenerate cache file {Path}.", filePath);
                    }

                    return Content(template.HtmlContent, "text/html");
                }
            }

            _logger.LogWarning("SiteTemplate: file not found and no DB record: {Path}", filePath);
            return NotFound($"Template '{safeName}' not found.");
        }

        /// <summary>
        /// Returns the most recent grabbed frame for a template as a PNG image.
        /// No authentication required — used by the public landing page gallery thumbnails.
        /// Returns <c>404</c> when no successful grab exists yet (browser shows placeholder).
        /// </summary>
        /// <param name="name">Template filename, e.g. <c>lw-airlock.html</c>.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("preview")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetPreview([FromQuery] string? name, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest("Parameter 'name' is required.");

            // Prevent path traversal — accept only the bare filename.
            var safeName = Path.GetFileName(name);
            if (string.IsNullOrEmpty(safeName) || safeName != name)
                return BadRequest("Invalid file name.");

            // Resolve GUID from GrabbedList.json — the source of truth for grabbed templates.
            // DB Template.Name is human-readable text, not the filename, so we cannot use it here.
            var grabbedListPath = Path.Combine(_sharedConfig.ExchangeFolder, "GrabbedList.json");
            if (!System.IO.File.Exists(grabbedListPath))
                return NotFound("GrabbedList not found.");

            var json = await System.IO.File.ReadAllTextAsync(grabbedListPath, ct).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<GrabberSourceDTO>>(json, _jsonOpts);

            var entry = list?.FirstOrDefault(e =>
                Path.GetFileName(e.Source).Equals(safeName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return NotFound();

            Guid guid;

            if (entry.Targets is { Count: > 0 })
            {
                // Multi-target entry — prefer 480×320, fall back to first available.
                var target = entry.Targets.FirstOrDefault(t => t.Width == 480) ?? entry.Targets[0];
                guid = target.TokenGuid;
            }
            else
            {
                // Legacy single-target entry.
                guid = entry.TokenGuid;
            }

            // Serve the PNG written alongside the .bin — no BGR565 decoding at request time.
            string pngPath = Path.Combine(_sharedConfig.ExchangeFolder, $"{guid}.png");
            if (!System.IO.File.Exists(pngPath))
                return NotFound("Not grabbed yet.");

            byte[] png = await System.IO.File.ReadAllBytesAsync(pngPath, ct).ConfigureAwait(false);
            return File(png, "image/png");
        }
    }
}
