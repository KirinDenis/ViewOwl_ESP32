using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Grabber.WebAPI.DTOs;
using ViewOwl.Grabber.WebAPI.Hubs;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// REST API for managing Template ↔ Device connections (flow links).
    /// All endpoints are user-scoped: users can only act on their own resources.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public sealed class ConnectionsController : ControllerBase
    {
        private readonly IConnectionRepository _connectionRepo;
        private readonly IDeviceRepository _deviceRepo;
        private readonly ITemplateRepository _templateRepo;
        private readonly IHubContext<ViewOwlHub> _hub;
        private readonly ILogger<ConnectionsController> _logger;

        /// <summary>
        /// Initialises the controller with its repositories.
        /// </summary>
        public ConnectionsController(
            IConnectionRepository connectionRepo,
            IDeviceRepository deviceRepo,
            ITemplateRepository templateRepo,
            IHubContext<ViewOwlHub> hub,
            ILogger<ConnectionsController> logger)
        {
            ArgumentNullException.ThrowIfNull(connectionRepo);
            ArgumentNullException.ThrowIfNull(deviceRepo);
            ArgumentNullException.ThrowIfNull(templateRepo);
            ArgumentNullException.ThrowIfNull(hub);
            ArgumentNullException.ThrowIfNull(logger);

            _connectionRepo = connectionRepo;
            _deviceRepo     = deviceRepo;
            _templateRepo   = templateRepo;
            _hub            = hub;
            _logger         = logger;
        }

        /// <summary>Returns all connections owned by the current user.</summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet]
        [ProducesResponseType(typeof(IReadOnlyList<ConnectionResponseDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetConnections(CancellationToken ct = default)
        {
            int userId = GetCurrentUserId();
            var connections = await _connectionRepo.GetByUserAsync(userId, ct).ConfigureAwait(false);
            return Ok(connections.Select(ConnectionResponseDto.From).ToList());
        }

        /// <summary>Returns a single connection by ID.</summary>
        /// <param name="id">Connection ID.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{id:int}")]
        [ProducesResponseType(typeof(ConnectionResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ConnectionErrorDto), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ConnectionErrorDto), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetConnection(int id, CancellationToken ct = default)
        {
            int userId = GetCurrentUserId();

            Connection? connection = await _connectionRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (connection is null)
                return NotFound(new ConnectionErrorDto("Connection not found", $"Connection with ID {id} does not exist."));

            if (connection.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new ConnectionErrorDto("Forbidden", "Cannot access a connection you don't own."));

            return Ok(ConnectionResponseDto.From(connection));
        }

        /// <summary>
        /// Creates a connection between a template and a device.
        /// Both resources must belong to the current user.
        /// Returns 409 Conflict if the connection already exists.
        /// </summary>
        /// <param name="request">Template and device IDs.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost]
        [ProducesResponseType(typeof(ConnectionResponseDto), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ConnectionErrorDto), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ConnectionErrorDto), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ConnectionErrorDto), StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CreateConnection(
            [FromBody] CreateConnectionRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            int userId = GetCurrentUserId();

            // Validate template ownership
            var template = await _templateRepo.GetByIdAsync(request.TemplateId, ct).ConfigureAwait(false);
            if (template is null)
                return BadRequest(new ConnectionErrorDto(
                    "Template not found",
                    $"Template with ID {request.TemplateId} does not exist."));

            if (template.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new ConnectionErrorDto("Forbidden", "Cannot connect to a template you don't own."));

            // Validate device ownership
            var device = await _deviceRepo.GetByIdAsync(request.DeviceId, ct).ConfigureAwait(false);
            if (device is null)
                return BadRequest(new ConnectionErrorDto(
                    "Device not found",
                    $"Device with ID {request.DeviceId} does not exist."));

            if (device.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new ConnectionErrorDto("Forbidden", "Cannot connect to a device you don't own."));

            // Enforce one-device-one-widget rule: remove any existing connections for this device
            // before creating the new one so the device always has exactly one active widget.
            var existing = await _connectionRepo.GetByDeviceAsync(request.DeviceId, ct).ConfigureAwait(false);
            foreach (Connection old in existing)
            {
                await _connectionRepo.DeleteAsync(old.Id, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Connection replaced: removed Id={OldId} (Template {OldTemplateId} → Device {DeviceId})",
                    old.Id, old.TemplateId, old.DeviceId);
            }

            var connection = new Connection
            {
                UserId     = userId,
                TemplateId = request.TemplateId,
                DeviceId   = request.DeviceId,
                CreatedAt  = DateTime.UtcNow
            };

            connection = await _connectionRepo.CreateAsync(connection, ct).ConfigureAwait(false);

            // Sync the device's active template so it starts receiving the new widget's frames
            await _deviceRepo.SetActiveTemplateAsync(request.DeviceId, request.TemplateId, ct)
                              .ConfigureAwait(false);

            _logger.LogInformation(
                "Connection created: Id={ConnectionId}, TemplateId={TemplateId}, DeviceId={DeviceId}, UserId={UserId}",
                connection.Id, connection.TemplateId, connection.DeviceId, userId);

            // Broadcast the updated connection list so all open sessions rebuild their edges.
            await BroadcastConnectionsAsync(userId, ct).ConfigureAwait(false);

            return CreatedAtAction(
                nameof(GetConnection),
                new { id = connection.Id },
                ConnectionResponseDto.From(connection));
        }

        /// <summary>Deletes a connection. Only the owner may delete.</summary>
        /// <param name="id">Connection ID.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpDelete("{id:int}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(ConnectionErrorDto), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ConnectionErrorDto), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteConnection(int id, CancellationToken ct = default)
        {
            int userId = GetCurrentUserId();

            Connection? connection = await _connectionRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (connection is null)
                return NotFound(new ConnectionErrorDto("Connection not found", $"Connection with ID {id} does not exist."));

            if (connection.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new ConnectionErrorDto("Forbidden", "Cannot delete a connection you don't own."));

            await _connectionRepo.DeleteAsync(id, ct).ConfigureAwait(false);

            // Clear the device's active template so it stops receiving frames for this widget
            await _deviceRepo.SetActiveTemplateAsync(connection.DeviceId, null, ct)
                              .ConfigureAwait(false);

            _logger.LogInformation(
                "Connection deleted: Id={ConnectionId}, TemplateId={TemplateId}, DeviceId={DeviceId}, UserId={UserId}",
                id, connection.TemplateId, connection.DeviceId, userId);

            // Broadcast the updated connection list so all open sessions rebuild their edges.
            await BroadcastConnectionsAsync(userId, ct).ConfigureAwait(false);

            return NoContent();
        }

        /// <summary>Returns all connections for a specific device owned by the current user.</summary>
        /// <param name="deviceId">Device ID.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("device/{deviceId:int}")]
        [ProducesResponseType(typeof(IReadOnlyList<ConnectionResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ConnectionErrorDto), StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetConnectionsByDevice(int deviceId, CancellationToken ct = default)
        {
            int userId = GetCurrentUserId();

            var device = await _deviceRepo.GetByIdAsync(deviceId, ct).ConfigureAwait(false);
            if (device is null || device.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new ConnectionErrorDto("Forbidden", "Cannot access connections for a device you don't own."));

            var connections = await _connectionRepo.GetByDeviceAsync(deviceId, ct).ConfigureAwait(false);
            return Ok(connections.Select(ConnectionResponseDto.From).ToList());
        }

        /// <summary>
        /// Loads all connections for the user and broadcasts the full list to every
        /// active session in the same user group so their edge sets stay in sync.
        /// </summary>
        private async Task BroadcastConnectionsAsync(int userId, CancellationToken ct)
        {
            var all = await _connectionRepo.GetByUserAsync(userId, ct).ConfigureAwait(false);
            var payload = new ConnectionsChangedEvent(all.Select(ConnectionResponseDto.From).ToList());
            await _hub.Clients
                      .Group(ViewOwlHub.UserGroup(userId))
                      .SendAsync("ConnectionsChanged", payload, ct)
                      .ConfigureAwait(false);
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
