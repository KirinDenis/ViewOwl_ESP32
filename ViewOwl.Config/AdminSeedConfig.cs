namespace ViewOwl.Config
{
    /// <summary>
    /// Credentials for the initial Admin user created on first startup.
    /// Bound from <c>appsettings.json → AdminSeed</c>.
    /// </summary>
    public sealed class AdminSeedConfig
    {
        /// <summary>Login name for the seeded admin account.</summary>
        public string Login { get; set; } = "admin";

        /// <summary>
        /// Plaintext password used only during the one-time seed.
        /// Stored as BCrypt hash; this value is never written to the database.
        /// No default on purpose — a well-known fallback would silently create
        /// a default admin account on an unconfigured install. The startup seed
        /// throws when this is left empty.
        /// </summary>
        public string Password { get; set; } = string.Empty;
    }
}
