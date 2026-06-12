using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ViewOwl.Grabber.WebAPI.Hubs
{
    /// <summary>
    /// SignalR hub for real-time dashboard updates.
    /// Authenticated clients connect and are placed in a per-user group
    /// (<c>user:{id}</c>) that matches the group format used by
    /// <see cref="ViewOwlHub"/> so server-side notifications reach all connected hubs.
    /// Maps to <c>/hubs/dashboard</c>.
    /// </summary>
    [Authorize]
    public sealed class DashboardHub : Hub
    {
        private readonly ILogger<DashboardHub> _logger;

        /// <summary>
        /// Initialises the hub with a logger.
        /// </summary>
        public DashboardHub(ILogger<DashboardHub> logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            _logger = logger;
        }

        /// <inheritdoc/>
        public override async Task OnConnectedAsync()
        {
            string? userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? Context.User?.FindFirstValue(
                                 System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

            if (!string.IsNullOrEmpty(userId))
            {
                // Same group format as ViewOwlHub so notifications reach clients on either hub
                await Groups.AddToGroupAsync(Context.ConnectionId, ViewOwlHub.UserGroup(int.Parse(userId, System.Globalization.CultureInfo.InvariantCulture)))
                            .ConfigureAwait(false);

                _logger.LogInformation(
                    "[DashboardHub] Connected: ConnectionId={ConnectionId}, UserId={UserId}",
                    Context.ConnectionId, userId);
            }

            await base.OnConnectedAsync().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string? userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? Context.User?.FindFirstValue(
                                 System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

            if (!string.IsNullOrEmpty(userId))
            {
                _logger.LogInformation(
                    "[DashboardHub] Disconnected: ConnectionId={ConnectionId}, UserId={UserId}",
                    Context.ConnectionId, userId);
            }

            await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
        }
    }
}
