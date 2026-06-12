using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Admin-only endpoints for user management.
    /// All routes require the <c>Admin</c> role.
    /// </summary>
    [ApiController]
    [Route("api/admin/users")]
    [Authorize(Roles = "Admin")]
    public sealed class AdminUsersController : ControllerBase
    {
        private readonly IUserRepository _users;

        /// <summary>
        /// Initialises the controller with user data access.
        /// </summary>
        /// <param name="users">User repository.</param>
        public AdminUsersController(IUserRepository users)
        {
            _users = users;
        }

        /// <summary>Returns all users.</summary>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet]
        [ProducesResponseType(typeof(IReadOnlyList<UserDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAll(CancellationToken ct = default)
        {
            var users = await _users.GetAllAsync(ct).ConfigureAwait(false);
            return Ok(users.Select(UserDto.From));
        }

        /// <summary>Creates a new user.</summary>
        /// <param name="request">New user details.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost]
        [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Create(
            [FromBody] CreateUserRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Reject if login already taken
            var existing = await _users.GetByLoginAsync(request.Login, ct).ConfigureAwait(false);
            if (existing is not null)
                return Conflict("Login already exists.");

            if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
                return BadRequest("Invalid role. Use 'Admin' or 'User'.");

            var user = new User
            {
                // Normalize to lower-invariant so login lookup is always case-insensitive
                Login          = request.Login.ToLowerInvariant(),
                PasswordHash   = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Role           = role,
                SandboxEnabled = request.SandboxEnabled
            };

            var created = await _users.CreateAsync(user, ct).ConfigureAwait(false);
            return CreatedAtAction(nameof(GetAll), UserDto.From(created));
        }

        /// <summary>Deletes a user by id.</summary>
        /// <param name="id">User id.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpDelete("{id:int}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> Delete(int id, CancellationToken ct = default)
        {
            await _users.DeleteAsync(id, ct).ConfigureAwait(false);
            return NoContent();
        }

        /// <summary>Toggles the sandbox flag for a user.</summary>
        /// <param name="id">User id.</param>
        /// <param name="request">Sandbox state.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPatch("{id:int}/sandbox")]
        [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> SetSandbox(
            int id,
            [FromBody] SetSandboxRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var user = await _users.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (user is null)
                return NotFound();

            // Re-attach for update (GetByIdAsync uses AsNoTracking)
            user.SandboxEnabled = request.Enabled;
            await _users.UpdateAsync(user, ct).ConfigureAwait(false);
            return Ok(UserDto.From(user));
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    /// <summary>User response shape.</summary>
    public sealed record UserDto(int Id, string Login, string Role, bool SandboxEnabled, DateTime CreatedAt)
    {
        /// <summary>Maps a <see cref="User"/> entity to a <see cref="UserDto"/>.</summary>
        public static UserDto From(User u)
        {
            ArgumentNullException.ThrowIfNull(u);
            return new(u.Id, u.Login, u.Role.ToString(), u.SandboxEnabled, u.CreatedAt);
        }
    }

    /// <summary>Create-user request body.</summary>
    public sealed record CreateUserRequestDto(
        string Login,
        string Password,
        string Role = "User",
        bool SandboxEnabled = true);

    /// <summary>Sandbox toggle request body.</summary>
    public sealed record SetSandboxRequestDto(bool Enabled);
}
