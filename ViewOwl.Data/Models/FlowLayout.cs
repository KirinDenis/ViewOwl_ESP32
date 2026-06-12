namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Stores the React Flow canvas node positions for a user's dashboard.
    /// One row per user; positions are serialised as a JSON object keyed by node ID.
    /// </summary>
    public sealed class FlowLayout
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>FK — the user who owns these positions.</summary>
        public int UserId { get; set; }

        /// <summary>
        /// JSON object mapping node ID → <c>{ x, y }</c> coordinates.
        /// Example: <c>{ "template-1": { "x": 100, "y": 200 } }</c>
        /// </summary>
        public string PositionsJson { get; set; } = "{}";

        /// <summary>UTC timestamp of the last update.</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>Owner.</summary>
        public User User { get; set; } = null!;
    }
}
