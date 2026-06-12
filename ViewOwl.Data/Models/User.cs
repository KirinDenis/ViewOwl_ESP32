namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Application user with authentication credentials and role-based access.
    /// </summary>
    public sealed class User
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>Unique login name used for authentication.</summary>
        public string Login { get; set; } = string.Empty;

        /// <summary>BCrypt-hashed password. Never stored in plaintext.</summary>
        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>Role controlling which API endpoints the user may call.</summary>
        public UserRole Role { get; set; } = UserRole.User;

        /// <summary>
        /// When <c>true</c>, HTML template previews for this user's devices are rendered
        /// inside a sandboxed iframe; when <c>false</c>, the iframe is unsandboxed.
        /// </summary>
        public bool SandboxEnabled { get; set; } = true;

        /// <summary>UTC timestamp when the record was created.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>Devices owned by this user.</summary>
        public ICollection<Device> Devices { get; private set; } = [];

        /// <summary>Templates created by this user.</summary>
        public ICollection<Template> Templates { get; private set; } = [];
    }
}
