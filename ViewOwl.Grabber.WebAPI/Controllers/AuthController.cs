using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ViewOwl.Config;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Security.Services;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Handles authentication — issues JWT Bearer tokens on successful login.
    /// </summary>
    [ApiController]
    [Route("api/auth")]
    public sealed class AuthController : ControllerBase
    {
        private readonly IUserRepository _users;
        private readonly JwtConfig _jwt;
        private readonly ISessionFingerprintService _sessions;

        /// <summary>
        /// Initialises the controller with its dependencies.
        /// </summary>
        /// <param name="users">User data access.</param>
        /// <param name="jwtOptions">JWT configuration.</param>
        /// <param name="sessions">Session fingerprint service.</param>
        public AuthController(
            IUserRepository users,
            IOptions<JwtConfig> jwtOptions,
            ISessionFingerprintService sessions)
        {
            ArgumentNullException.ThrowIfNull(jwtOptions);
            _users    = users;
            _jwt      = jwtOptions.Value;
            _sessions = sessions;
        }

        /// <summary>
        /// Authenticates a user and returns a signed JWT Bearer token.
        /// Returns 401 on any credential failure — no details are disclosed.
        /// </summary>
        /// <param name="request">Login credentials.</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("login")]
        [ProducesResponseType(typeof(LoginResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Login(
            [FromBody] LoginRequestDto request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var user = await _users.GetByLoginAsync(request.Login, ct).ConfigureAwait(false);

            // Intentionally no detail: whether the login doesn't exist or the password is wrong,
            // the caller receives only 401 to prevent user enumeration.
            if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return Unauthorized();

            string userId = user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string jti    = Guid.NewGuid().ToString();

            string token = BuildToken(userId, user.Login, user.Role.ToString(), jti);

            // Record the session fingerprint so it appears in "My Sessions".
            await _sessions.RecordAsync(userId, jti, HttpContext).ConfigureAwait(false);

            return Ok(new LoginResponseDto(token));
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private string BuildToken(string userId, string login, string role, string jti)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(_jwt.Secret);
            var key         = new SymmetricSecurityKey(keyBytes);
            var creds       = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            Claim[] claims =
            [
                new(JwtRegisteredClaimNames.Sub,  userId),
                new(JwtRegisteredClaimNames.Name, login),
                new(ClaimTypes.Role,              role),
                new(JwtRegisteredClaimNames.Jti,  jti)
            ];

            var token = new JwtSecurityToken(
                issuer:             _jwt.Issuer,
                audience:           _jwt.Audience,
                claims:             claims,
                expires:            DateTime.UtcNow.AddMinutes(_jwt.ExpiryMinutes),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    // ── Request / response DTOs (local — not shared across projects) ──────────

    /// <summary>Login request body.</summary>
    public sealed record LoginRequestDto(string Login, string Password);

    /// <summary>Successful login response containing the Bearer token.</summary>
    public sealed record LoginResponseDto(string Token);
}
