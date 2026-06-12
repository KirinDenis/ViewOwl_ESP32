using Microsoft.AspNetCore.Http;
using ViewOwl.Security.Models;

namespace ViewOwl.Security.Services
{
    /// <summary>
    /// Records security events and provides query methods for dashboard and auto-block logic.
    /// </summary>
    public interface ISecurityEventService
    {
        /// <summary>
        /// Records a security event by extracting context from the current HTTP request.
        /// IP extraction honours X-Forwarded-For and X-Real-IP proxy headers.
        /// </summary>
        /// <param name="ctx">The active HTTP context.</param>
        /// <param name="eventType">One of the <c>SecurityEvent.Event*</c> constants.</param>
        /// <param name="details">Optional free-text detail string appended to the record.</param>
        Task LogAsync(HttpContext ctx, string eventType, string? details = null);

        /// <summary>Returns the most recent security events, newest first.</summary>
        /// <param name="count">Maximum number of records to return.</param>
        Task<List<SecurityEvent>> GetRecentAsync(int count = 100);

        /// <summary>Returns events originating from the specified IP within a time window.</summary>
        /// <param name="ip">Source IP address.</param>
        /// <param name="hours">How many hours back to look.</param>
        Task<List<SecurityEvent>> GetByIpAsync(string ip, int hours = 24);

        /// <summary>
        /// Counts events from an IP within a recent time window, optionally filtered by type.
        /// Used by the auto-block threshold checks.
        /// </summary>
        /// <param name="ip">Source IP address.</param>
        /// <param name="eventType">Optional event type filter; <c>null</c> counts all types.</param>
        /// <param name="minutes">How many minutes back to look.</param>
        Task<int> CountRecentByIpAsync(string ip, string? eventType = null, int minutes = 10);

        /// <summary>
        /// Counts all events from an IP regardless of time (used for scanner probe threshold).
        /// </summary>
        /// <param name="ip">Source IP address.</param>
        /// <param name="eventType">Optional event type filter.</param>
        Task<int> CountTotalByIpAsync(string ip, string? eventType = null);
    }
}
