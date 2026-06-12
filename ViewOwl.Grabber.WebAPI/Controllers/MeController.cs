using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Security.Services;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Returns the authenticated user's profile and active session list.
    /// Used by the dashboard on load to determine available menu items.
    /// </summary>
    [ApiController]
    [Route("api/me")]
    [Authorize]
    public sealed class MeController : ControllerBase
    {
        private readonly IUserRepository _users;
        private readonly ISessionFingerprintService _sessions;

        /// <summary>
        /// Initialises the controller with user data access and session service.
        /// </summary>
        /// <param name="users">User repository.</param>
        /// <param name="sessions">Session fingerprint service.</param>
        public MeController(IUserRepository users, ISessionFingerprintService sessions)
        {
            _users    = users;
            _sessions = sessions;
        }

        /// <summary>
        /// Returns the current user's id, login, role, and sandbox flag.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet]
        [ProducesResponseType(typeof(MeResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetMe(CancellationToken ct = default)
        {
            string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

            if (!int.TryParse(sub, out int userId))
                return Unauthorized();

            var user = await _users.GetByIdAsync(userId, ct).ConfigureAwait(false);
            if (user is null)
                return Unauthorized();

            // Refresh the session fingerprint (creates it if missing, updates LastSeenAt otherwise).
            string userIdStr = sub!;
            string jti = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)
                      ?? User.FindFirstValue("jti")
                      ?? string.Empty;

            if (!string.IsNullOrEmpty(jti))
                await _sessions.RecordAsync(userIdStr, jti, HttpContext).ConfigureAwait(false);

            return Ok(new MeResponseDto(user.Id, user.Login, user.Role.ToString(), user.SandboxEnabled));
        }

        /// <summary>
        /// Returns all active sessions for the current user (for the Sessions dashboard page).
        /// </summary>
        [HttpGet("sessions")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetSessions()
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var list = await _sessions.GetActiveSessionsAsync(userId).ConfigureAwait(false);
            string currentJti = User.FindFirstValue("jti") ?? string.Empty;

            var result = list.Select(s => new SessionDto(
                s.SessionId,
                s.IpAddress,
                s.UserAgent,
                s.CreatedAt,
                s.LastSeenAt,
                s.SessionId == currentJti
            )).ToList();

            return Ok(result);
        }

        /// <summary>
        /// Revokes a specific session by its JWT jti value.
        /// Users may only revoke their own sessions.
        /// </summary>
        /// <param name="sessionId">The JWT jti claim of the session to revoke.</param>
        [HttpDelete("sessions/{sessionId}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> RevokeSession(string sessionId)
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            await _sessions.RevokeAsync(userId, sessionId).ConfigureAwait(false);
            return NoContent();
        }

        /// <summary>
        /// Revokes all sessions for the current user except the currently active one.
        /// Equivalent to "log out all other devices".
        /// </summary>
        [HttpDelete("sessions")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> RevokeAllOtherSessions()
        {
            string? userId     = User.FindFirstValue(ClaimTypes.NameIdentifier)
                              ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            string? currentJti = User.FindFirstValue("jti");

            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            await _sessions.RevokeAllAsync(userId, currentJti ?? string.Empty)
                .ConfigureAwait(false);
            return NoContent();
        }
    }

    /// <summary>Current-user profile response.</summary>
    public sealed record MeResponseDto(int Id, string Login, string Role, bool SandboxEnabled);

    /// <summary>Active session summary returned to the user.</summary>
    public sealed record SessionDto(
        string SessionId,
        string IpAddress,
        string UserAgent,
        DateTime CreatedAt,
        DateTime LastSeenAt,
        bool IsCurrent);
}
