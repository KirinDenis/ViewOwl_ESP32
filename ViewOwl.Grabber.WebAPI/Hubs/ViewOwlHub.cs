using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ViewOwl.Grabber.WebAPI.Hubs
{
    /// <summary>
    /// SignalR hub for real-time dashboard notifications.
    /// Authenticated clients connect and are automatically placed in a
    /// per-user group (<c>user:{id}</c>).  Admin users also join the
    /// <c>admin</c> group so future admin-scoped broadcasts are possible.
    /// </summary>
    [Authorize]
    public sealed class ViewOwlHub : Hub
    {
        /// <inheritdoc/>
        public override async Task OnConnectedAsync()
        {
            int userId = GetUserId();
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId))
                        .ConfigureAwait(false);

            string? role = GetRole();
            if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, "admin")
                            .ConfigureAwait(false);
            }

            await base.OnConnectedAsync().ConfigureAwait(false);
        }

        /// <summary>Returns the SignalR group name for the given user id.</summary>
        /// <param name="userId">The user's database id.</param>
        public static string UserGroup(int userId) => $"user:{userId}";

        // ── Private helpers ───────────────────────────────────────────────────

        private int GetUserId()
        {
            string? sub = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? Context.User?.FindFirstValue(
                              System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            return int.TryParse(sub, out int id) ? id : 0;
        }

        private string? GetRole()
            => Context.User?.FindFirstValue(ClaimTypes.Role)
            ?? Context.User?.FindFirstValue(
                   "http://schemas.microsoft.com/ws/2008/06/identity/claims/role");
    }
}
