using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Unauthenticated endpoints that power the public landing page gallery.
    /// Only templates with <see cref="Template.IsPublic"/> set to <c>true</c>
    /// are exposed here.
    /// </summary>
    [ApiController]
    [Route("api/public-templates")]
    public sealed class PublicTemplatesController : ControllerBase
    {
        // Extracts data-vow-{attr}="value" from template HTML.
        private static readonly Regex VowAttrRegex = new(
            @"data-vow-(?<attr>[a-z]+)=""(?<val>[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ITemplateRepository _templates;
        private readonly IGrabLogRepository _grabLogs;
        private readonly SharedConfig _sharedConfig;
        private readonly ILogger<PublicTemplatesController> _logger;

        /// <summary>
        /// Initialises the controller with its dependencies.
        /// </summary>
        public PublicTemplatesController(
            ITemplateRepository templates,
            IGrabLogRepository grabLogs,
            IOptions<SharedConfig> sharedConfig,
            ILogger<PublicTemplatesController> logger)
        {
            ArgumentNullException.ThrowIfNull(sharedConfig);
            _templates    = templates;
            _grabLogs     = grabLogs;
            _sharedConfig = sharedConfig.Value;
            _logger       = logger;
        }

        /// <summary>
        /// Returns all public templates as a compact list for the landing page gallery.
        /// <c>cls</c> and <c>category</c> are extracted from <c>data-vow-*</c> attributes
        /// in the template HTML — no separate storage required.
        /// No authentication required.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(IReadOnlyList<PublicTemplateDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetPublic(CancellationToken ct = default)
        {
            IReadOnlyList<Template> templates =
                await _templates.GetPublicAsync(ct).ConfigureAwait(false);

            var result = templates.Select(t =>
            {
                string html     = t.HtmlContent ?? string.Empty;
                string cls      = ExtractVow(html, "class",    "a");
                string category = ExtractVow(html, "category", "other");
                return new PublicTemplateDto(t.Id, t.Name, cls.ToLowerInvariant(), category.ToLowerInvariant());
            }).ToList();

            return Ok(result);
        }

        /// <summary>
        /// Returns the most recent grabbed PNG preview for the given template.
        /// No authentication required — used by the landing page gallery thumbnails.
        /// Returns <c>404</c> when no successful grab exists yet.
        /// </summary>
        /// <param name="id">Template primary key.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{id:int}/preview")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetPreview(int id, CancellationToken ct = default)
        {
            GrabLog? log = await _grabLogs.GetLastSuccessfulAsync(id, ct).ConfigureAwait(false);
            if (log is null)
                return NotFound("No successful grab yet.");

            string pngPath = Path.Combine(_sharedConfig.ExchangeFolder, $"{log.TokenGuid}.png");
            if (!System.IO.File.Exists(pngPath))
            {
                _logger.LogWarning("PublicTemplates: PNG not found for template {Id}, guid {Guid}", id, log.TokenGuid);
                return NotFound("Preview image not found on disk.");
            }

            byte[] png = await System.IO.File.ReadAllBytesAsync(pngPath, ct).ConfigureAwait(false);
            return File(png, "image/png");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string ExtractVow(string html, string attr, string fallback)
        {
            foreach (Match m in VowAttrRegex.Matches(html))
            {
                if (m.Groups["attr"].Value.Equals(attr, StringComparison.OrdinalIgnoreCase))
                {
                    string val = m.Groups["val"].Value.Trim();
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            return fallback;
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compact template descriptor returned by <c>GET /api/public-templates</c>.
    /// Carries enough metadata for the landing page gallery to filter and display
    /// the template without fetching the full HTML content.
    /// </summary>
    /// <param name="Id">Template primary key — used to build iframe/preview URLs.</param>
    /// <param name="Name">Human-readable template name.</param>
    /// <param name="Cls">Template class letter: <c>a</c>, <c>b</c>, or <c>c</c>.</param>
    /// <param name="Category">Template category slug, e.g. <c>weather</c>, <c>scifi</c>.</param>
    public sealed record PublicTemplateDto(int Id, string Name, string Cls, string Category);
}
