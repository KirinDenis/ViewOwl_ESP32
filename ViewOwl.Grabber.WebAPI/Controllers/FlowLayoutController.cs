using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Grabber.WebAPI.DTOs;
using ViewOwl.Grabber.WebAPI.Hubs;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// REST API for persisting and broadcasting React Flow canvas node positions.
    /// All positions are scoped to the current user; a PUT broadcasts to all
    /// active sessions in the same user group so the canvas stays in sync.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/flow-layout")]
    public sealed class FlowLayoutController : ControllerBase
    {
        private readonly IFlowLayoutRepository _repo;
        private readonly IHubContext<ViewOwlHub> _hub;
        private readonly ILogger<FlowLayoutController> _logger;

        private static readonly JsonSerializerOptions _jsonOptions =
            new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>
        /// Initialises the controller with its collaborators.
        /// </summary>
        public FlowLayoutController(
            IFlowLayoutRepository repo,
            IHubContext<ViewOwlHub> hub,
            ILogger<FlowLayoutController> logger)
        {
            ArgumentNullException.ThrowIfNull(repo);
            ArgumentNullException.ThrowIfNull(hub);
            ArgumentNullException.ThrowIfNull(logger);

            _repo   = repo;
            _hub    = hub;
            _logger = logger;
        }

        /// <summary>
        /// Returns the saved node positions for the current user.
        /// Returns an empty positions map when no layout has been saved yet.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet]
        [ProducesResponseType(typeof(FlowLayoutResponseDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetLayout(CancellationToken ct = default)
        {
            int userId = GetCurrentUserId();
            var layout = await _repo.GetByUserAsync(userId, ct).ConfigureAwait(false);

            IReadOnlyDictionary<string, NodePositionDto> positions =
                layout is not null
                    ? JsonSerializer.Deserialize<Dictionary<string, NodePositionDto>>(
                          layout.PositionsJson, _jsonOptions)
                      ?? new Dictionary<string, NodePositionDto>()
                    : new Dictionary<string, NodePositionDto>();

            return Ok(new FlowLayoutResponseDto(positions));
        }

        /// <summary>
        /// Saves new node positions for the current user and broadcasts
        /// <c>FlowLayoutChanged</c> to all other active sessions in the same user group.
        /// </summary>
        /// <param name="request">Map of node ID → position.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPut]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> PutLayout(
            [FromBody] FlowLayoutRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            int userId = GetCurrentUserId();

            string json = JsonSerializer.Serialize(request.Positions, _jsonOptions);
            await _repo.UpsertAsync(userId, json, ct).ConfigureAwait(false);

            // Convert to the broadcast payload type (identical shape, different record type)
            var broadcastPositions = request.Positions.ToDictionary(
                kv => kv.Key,
                kv => new NodePositionPayload(kv.Value.X, kv.Value.Y));

            await _hub.Clients
                      .Group(ViewOwlHub.UserGroup(userId))
                      .SendAsync("FlowLayoutChanged", new FlowLayoutChangedEvent(broadcastPositions), ct)
                      .ConfigureAwait(false);

            _logger.LogDebug(
                "[FlowLayout] Saved {Count} positions for UserId={UserId}",
                request.Positions.Count, userId);

            return NoContent();
        }

        private int GetCurrentUserId()
        {
            string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");
            return int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                ? id
                : 0;
        }
    }
}
