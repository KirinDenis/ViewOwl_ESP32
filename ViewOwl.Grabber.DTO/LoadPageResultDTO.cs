#nullable enable

namespace ViewOwl.DTO
{
    /// <summary>Result of <c>IGrabber.AddUrlAsync</c>.</summary>
    public class LoadPageResultDTO
    {
        /// <summary>True when the page was opened and navigated successfully.</summary>
        public bool Success { get; set; }

        /// <summary>True when a navigation response was received.</summary>
        public bool Navigated { get; set; }

        /// <summary>True when the page finished loading.</summary>
        public bool Loaded { get; set; }

        /// <summary>Original source URL passed to AddUrlAsync.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Final page URL after navigation (used as the tab lookup key).</summary>
        public string TabName { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable error description when <see cref="Success"/> is false.
        /// Populated from the caught exception message.
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}
