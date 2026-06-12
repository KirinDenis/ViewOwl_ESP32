namespace ViewOwl.Security.Models
{
    /// <summary>
    /// Tracks a user session's network identity so that sudden IP + User-Agent changes
    /// can be flagged as potential session hijacking.
    /// </summary>
    public sealed class SessionFingerprint
    {
        /// <summary>Primary key.</summary>
        public int Id { get; set; }

        /// <summary>Owning user's ID (string form of the integer PK from the main DB).</summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>JWT <c>jti</c> claim that uniquely identifies this session.</summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>IP address recorded at session creation (may be updated on IP-only changes).</summary>
        public string IpAddress { get; set; } = string.Empty;

        /// <summary>Raw User-Agent string recorded at session creation.</summary>
        public string UserAgent { get; set; } = string.Empty;

        /// <summary>SHA-256 hex digest of <see cref="UserAgent"/> for fast comparison.</summary>
        public string UserAgentHash { get; set; } = string.Empty;

        /// <summary>UTC timestamp when this fingerprint record was first created.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>UTC timestamp of the most recent validated request on this session.</summary>
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

        /// <summary><c>true</c> while the session is valid; <c>false</c> after revocation.</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>UTC timestamp when the session was revoked; <c>null</c> if still active.</summary>
        public DateTime? RevokedAt { get; set; }
    }
}
