using ViewOwl.Security.Models;

namespace ViewOwl.Security.Services
{
    /// <summary>
    /// Manages the list of blocked IP addresses and evaluates auto-block thresholds.
    /// </summary>
    public interface IIpBlockService
    {
        /// <summary>
        /// Returns <c>true</c> if the IP is currently blocked (either permanent or not yet expired).
        /// </summary>
        /// <param name="ip">IPv4 or IPv6 address string.</param>
        Task<bool> IsBlockedAsync(string ip);

        /// <summary>
        /// Adds or refreshes a block for the given IP.
        /// If the IP is already blocked the existing record is updated.
        /// </summary>
        /// <param name="ip">IP address to block.</param>
        /// <param name="reason">Human-readable reason stored with the block record.</param>
        /// <param name="duration">
        /// How long the block should last; <c>null</c> creates a permanent block.
        /// </param>
        /// <param name="isManual"><c>true</c> for admin-initiated blocks.</param>
        Task BlockAsync(string ip, string reason, TimeSpan? duration = null, bool isManual = false);

        /// <summary>
        /// Lifts an active block for the given IP by setting <c>UnblockedAt</c> and moving
        /// <c>BlockedUntil</c> to the past so that <see cref="IsBlockedAsync"/> returns <c>false</c>.
        /// </summary>
        /// <param name="ip">IP address to unblock.</param>
        Task UnblockAsync(string ip);

        /// <summary>Returns all IP records where the block is currently active.</summary>
        Task<List<BlockedIp>> GetActiveBlocksAsync();

        /// <summary>
        /// Evaluates recent event counts for the IP and triggers an automatic block when
        /// any threshold is exceeded.  Does nothing if the IP is already blocked.
        /// </summary>
        /// <param name="ip">IP address to evaluate.</param>
        /// <param name="eventService">
        /// Event service used to query recent counts; passed in to avoid circular DI.
        /// </param>
        Task AutoBlockIfNeededAsync(string ip, ISecurityEventService eventService);
    }
}
