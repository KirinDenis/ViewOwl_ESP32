using Microsoft.AspNetCore.Http;
using ViewOwl.Security.Models;

namespace ViewOwl.Security.Services
{
    /// <summary>
    /// Manages session fingerprints to detect potential session hijacking.
    /// A fingerprint captures the IP address and User-Agent at login time; subsequent
    /// requests are validated against that baseline.
    /// </summary>
    public interface ISessionFingerprintService
    {
        /// <summary>
        /// Creates or refreshes the fingerprint record for a user session.
        /// Call this immediately after a successful login.
        /// </summary>
        /// <param name="userId">Authenticated user's ID.</param>
        /// <param name="sessionId">JWT <c>jti</c> claim uniquely identifying the session.</param>
        /// <param name="ctx">The HTTP context of the login request.</param>
        Task RecordAsync(string userId, string sessionId, HttpContext ctx);

        /// <summary>
        /// Validates an ongoing request against the stored fingerprint.
        /// Returns <c>false</c> (and logs a <c>SuspiciousActivity</c> event) when both
        /// the IP and User-Agent have changed simultaneously, which indicates a possible
        /// token theft.  A pure IP change is normal (mobile users) and is silently updated.
        /// </summary>
        /// <param name="userId">Authenticated user's ID from the JWT.</param>
        /// <param name="sessionId">JWT <c>jti</c> claim.</param>
        /// <param name="ctx">The current HTTP context.</param>
        Task<bool> ValidateAsync(string userId, string sessionId, HttpContext ctx);

        /// <summary>Returns all active (non-revoked) sessions for the specified user.</summary>
        /// <param name="userId">User whose sessions to retrieve.</param>
        Task<List<SessionFingerprint>> GetActiveSessionsAsync(string userId);

        /// <summary>Revokes a single session (marks it inactive).</summary>
        /// <param name="userId">Owning user's ID.</param>
        /// <param name="sessionId">JWT <c>jti</c> claim of the session to revoke.</param>
        Task RevokeAsync(string userId, string sessionId);

        /// <summary>
        /// Revokes all sessions for a user except the current one.
        /// Useful for "log out all other devices".
        /// </summary>
        /// <param name="userId">Owning user's ID.</param>
        /// <param name="exceptSessionId">JWT <c>jti</c> of the session to keep.</param>
        Task RevokeAllAsync(string userId, string exceptSessionId);
    }
}
