namespace ViewOwl.Data.Models
{
    /// <summary>
    /// A display category used to group HTML templates (e.g. Weather, Clock, Menu).
    /// System categories (<see cref="IsSystem"/> = <c>true</c>) cannot be deleted.
    /// </summary>
    public sealed class TemplateCategory
    {
        /// <summary>Primary key (auto-increment).</summary>
        public int Id { get; set; }

        /// <summary>Human-readable display label, e.g. "Weather".</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// URL-safe lowercase slug embedded in template HTML as the
        /// <c>data-vow-category</c> attribute value, e.g. "weather".
        /// Immutable after creation — changing the slug would break all
        /// templates that reference it.
        /// </summary>
        public string Slug { get; set; } = string.Empty;

        /// <summary>
        /// When <c>true</c> this is a built-in system category that cannot
        /// be deleted. The "other" category is always a system category and
        /// acts as the fallback for uncategorised templates.
        /// </summary>
        public bool IsSystem { get; set; }

        /// <summary>UTC timestamp when the category was created.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
