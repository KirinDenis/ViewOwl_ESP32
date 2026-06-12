namespace ViewOwl.Security.Models
{
    /// <summary>
    /// Represents an IP address that has been blocked, either manually by an admin
    /// or automatically by the security system.
    /// </summary>
    public sealed class BlockedIp
    {
        /// <summary>Primary key.</summary>
        public int Id { get; set; }

        /// <summary>The blocked IPv4 or IPv6 address.</summary>
        public string IpAddress { get; set; } = string.Empty;

        /// <summary>Human-readable reason for the block (e.g. "AutoBlock:BruteForce").</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>UTC timestamp when the block was created.</summary>
        public DateTime BlockedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC timestamp when the block expires.
        /// <c>null</c> means the block is permanent.
        /// </summary>
        public DateTime? BlockedUntil { get; set; }

        /// <summary>
        /// <c>true</c> if blocked by an admin action; <c>false</c> if auto-blocked
        /// by the security system.
        /// </summary>
        public bool IsManual { get; set; }

        /// <summary>
        /// UTC timestamp when the block was lifted; <c>null</c> if still active.
        /// When set, <see cref="BlockedUntil"/> is also moved to the past so that
        /// <see cref="Services.IIpBlockService.IsBlockedAsync"/> returns <c>false</c>.
        /// </summary>
        public DateTime? UnblockedAt { get; set; }
    }
}
