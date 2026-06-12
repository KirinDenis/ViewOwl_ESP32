using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ViewOwl.Grabber.WebAPI.DTOs;
using ViewOwl.Grabber.WebAPI.Services;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Pipeline health endpoints for the ISA-101 calm dashboard.
    /// All routes require a valid JWT — authenticated user's scope only.
    /// </summary>
    [ApiController]
    [Route("api/health")]
    [Authorize]
    public sealed class HealthController : ControllerBase
    {
        private readonly ITemplateHealthService _health;
        private readonly ILogger<HealthController> _logger;

        /// <summary>
        /// Initialises the controller.
        /// </summary>
        /// <param name="health">Template health computation service.</param>
        /// <param name="logger">Logger.</param>
        public HealthController(
            ITemplateHealthService health,
            ILogger<HealthController> logger)
        {
            ArgumentNullException.ThrowIfNull(health);
            ArgumentNullException.ThrowIfNull(logger);
            _health = health;
            _logger = logger;
        }

        /// <summary>
        /// Returns ISA-101 health status for every template owned by the authenticated user.
        /// </summary>
        /// <remarks>
        /// Health states:
        /// <list type="bullet">
        ///   <item><c>healthy</c> — grabbed within the last 10 minutes, last attempt succeeded.</item>
        ///   <item><c>overdue</c> — last successful grab is older than 10 minutes.</item>
        ///   <item><c>failed</c> — most recent grab attempt failed.</item>
        ///   <item><c>never</c> — no grab has ever been attempted for this template.</item>
        ///   <item><c>unassigned</c> — no device has this template set as its active template.</item>
        /// </list>
        /// Results are returned in a single database round-trip (no N+1).
        /// </remarks>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("templates")]
        [ProducesResponseType(typeof(IReadOnlyList<TemplateHealthDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetTemplateHealth(CancellationToken ct = default)
        {
            int userId = GetUserId();
            if (userId <= 0)
                return Unauthorized();

            IReadOnlyList<TemplateHealthDto> rows =
                await _health.GetAllAsync(userId, ct).ConfigureAwait(false);

            return Ok(rows);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private int GetUserId()
        {
            string? claim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue("sub");

            return int.TryParse(claim, out int id) ? id : 0;
        }
    }
}
