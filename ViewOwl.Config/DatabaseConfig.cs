namespace ViewOwl.Config
{
    /// <summary>
    /// Database configuration bound from <c>appsettings.json → Database</c>.
    /// </summary>
    public sealed class DatabaseConfig
    {
        /// <summary>
        /// Path to the SQLite database file.
        /// Relative paths are resolved from the application's working directory.
        /// Default: <c>viewowl.db</c> (next to the executable).
        /// </summary>
        public string Path { get; set; } = "viewowl.db";
    }
}
