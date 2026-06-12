namespace ViewOwl.Security.Models
{
    /// <summary>
    /// Records a single security-relevant HTTP event (blocked IP, rate limit hit, scanner probe, etc.).
    /// </summary>
    public sealed class SecurityEvent
    {
        // ── Well-known EventType constants ────────────────────────────────────

        /// <summary>Multiple failed login attempts from the same IP.</summary>
        public const string EventBruteForce = "BruteForce";

        /// <summary>Request rejected by a rate-limiting policy.</summary>
        public const string EventRateLimit = "RateLimit";

        /// <summary>Request targeting a known vulnerability scanner path.</summary>
        public const string EventScannerProbe = "ScannerProbe";

        /// <summary>Request presented an invalid or expired JWT token.</summary>
        public const string EventInvalidToken = "InvalidToken";

        /// <summary>Request was rejected because the source IP is on the block list.</summary>
        public const string EventBlocked = "Blocked";

        /// <summary>Anomalous behaviour that does not fit another category.</summary>
        public const string EventSuspiciousActivity = "SuspiciousActivity";

        // ── Properties ────────────────────────────────────────────────────────

        /// <summary>Primary key.</summary>
        public int Id { get; set; }

        /// <summary>Originating IP address (may be extracted from proxy headers).</summary>
        public string IpAddress { get; set; } = string.Empty;

        /// <summary>HTTP User-Agent header value; null when absent.</summary>
        public string? UserAgent { get; set; }

        /// <summary>Request path and query string.</summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>HTTP method (GET, POST, …).</summary>
        public string Method { get; set; } = string.Empty;

        /// <summary>
        /// Category of the security event.
        /// Use the <c>Event*</c> constants defined on this class.
        /// </summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>HTTP status code returned to the client.</summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// First 500 characters of the request body for POST analysis; null for other methods
        /// or when the body was empty.
        /// </summary>
        public string? RequestBody { get; set; }

        /// <summary>UTC timestamp when the event was recorded.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Authenticated user's ID, if the request carried a valid token.</summary>
        public string? UserId { get; set; }

        /// <summary>JWT <c>jti</c> claim value identifying the session, if present.</summary>
        public string? SessionId { get; set; }

        /// <summary>Optional free-text detail or context for this event.</summary>
        public string? Details { get; set; }
    }
}
