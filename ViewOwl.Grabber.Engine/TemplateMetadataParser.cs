using System.Text.RegularExpressions;

namespace ViewOwl.Grabber.Engine
{
    /// <summary>
    /// Parsed <c>data-vow-*</c> metadata extracted from an HTML template's
    /// <c>&lt;body&gt;</c> tag.
    /// </summary>
    public sealed record TemplateVowMetadata
    {
        /// <summary>
        /// Template class: 'A' = single-frame live (default), 'C' = multi-frame flash loop.
        /// </summary>
        public char VowClass { get; init; } = 'A';

        /// <summary>Human-readable display name from <c>data-vow-name</c>.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Category slug from <c>data-vow-category</c> (e.g. "scifi", "weather").</summary>
        public string Category { get; init; } = "other";

        /// <summary>Short description from <c>data-vow-description</c>.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// Number of animation frames from <c>data-vow-frames</c>.
        /// Clamped to [1, 32] on parse (must match BatchStartPayload.MaxFrames).
        /// </summary>
        public int FrameCount { get; init; } = 1;

        /// <summary>
        /// Playback speed from <c>data-vow-fps</c>.
        /// Clamped to [1, 30] on parse.
        /// </summary>
        public int Fps { get; init; } = 10;

        /// <summary>
        /// Auto-refresh interval in minutes from <c>data-vow-refresh</c>.
        /// 0 means "never re-grab automatically".
        /// </summary>
        public int RefreshMinutes { get; init; } = 5;

        /// <summary>
        /// Output frame format requested by the template via <c>data-vow-render</c>,
        /// expressed as an <c>IImageConverter.FormatId</c>
        /// ("mono1bit" for <c>mono</c>; "mono4gray" for <c>mono-CGA</c>/<c>4gray</c>).
        /// <c>null</c> when the attribute is absent or its value is unrecognised, meaning
        /// "no override" — the device's default display-type format is used.
        /// </summary>
        public string? RenderFormat { get; init; }
    }

    /// <summary>
    /// Parses <c>data-vow-*</c> attributes from the <c>&lt;body&gt;</c> tag of an HTML template.
    /// Uses lightweight regex extraction — no full HTML parser dependency.
    /// </summary>
    public static class TemplateMetadataParser
    {
        // Matches: data-vow-<name>="<value>"  or  data-vow-<name>='<value>'
        private static readonly Regex AttributeRegex = new(
            @"data-vow-(?<name>[\w-]+)\s*=\s*[""'](?<value>[^""']*)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

        /// <summary>
        /// Extracts all <c>data-vow-*</c> attributes from <paramref name="htmlContent"/>
        /// and returns a populated <see cref="TemplateVowMetadata"/>.
        /// Returns default (Class A) metadata when no attributes are present.
        /// </summary>
        /// <param name="htmlContent">Raw HTML string of the template.</param>
        public static TemplateVowMetadata Parse(string htmlContent)
        {
            if (string.IsNullOrWhiteSpace(htmlContent))
                return new TemplateVowMetadata();

            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in AttributeRegex.Matches(htmlContent))
                attrs[m.Groups["name"].Value] = m.Groups["value"].Value;

            char vowClass = 'A';
            if (attrs.TryGetValue("class", out string? classVal)
                && classVal.Length == 1
                && char.IsLetter(classVal[0]))
            {
                vowClass = char.ToUpperInvariant(classVal[0]);
            }

            int frameCount = 1;
            if (attrs.TryGetValue("frames", out string? framesVal)
                && int.TryParse(framesVal, out int fc))
            {
                // Upper bound must match BatchStartPayload.MaxFrames / BATCH_MAX_FRAMES (packet.h).
                frameCount = Math.Clamp(fc, 1, 32);
            }

            int fps = 10;
            if (attrs.TryGetValue("fps", out string? fpsVal)
                && int.TryParse(fpsVal, out int f))
            {
                fps = Math.Clamp(f, 1, 30);
            }

            int refreshMinutes = 5;
            if (attrs.TryGetValue("refresh", out string? refreshVal)
                && int.TryParse(refreshVal, out int r))
            {
                refreshMinutes = Math.Max(0, r);
            }

            string? renderFormat = null;
            if (attrs.TryGetValue("render", out string? renderVal))
            {
                renderFormat = MapRenderProfile(renderVal);
            }

            return new TemplateVowMetadata
            {
                VowClass       = vowClass,
                Name           = attrs.TryGetValue("name", out string? n) ? n : string.Empty,
                Category       = attrs.TryGetValue("category", out string? cat) ? cat : "other",
                Description    = attrs.TryGetValue("description", out string? desc) ? desc : string.Empty,
                FrameCount     = frameCount,
                Fps            = fps,
                RefreshMinutes = refreshMinutes,
                RenderFormat   = renderFormat,
            };
        }

        /// <summary>
        /// Maps a <c>data-vow-render</c> profile value to an <c>IImageConverter.FormatId</c>.
        /// <c>mono</c> → "mono1bit"; <c>mono-CGA</c> or <c>4gray</c> → "mono4gray".
        /// Returns <c>null</c> for absent or unrecognised values (no format override).
        /// </summary>
        /// <param name="value">Raw attribute value (case-insensitive).</param>
        private static string? MapRenderProfile(string? value) =>
            value?.Trim().ToUpperInvariant() switch
            {
                "MONO"     => "mono1bit",
                "MONO-CGA" => "mono4gray",
                "4GRAY"    => "mono4gray",
                _          => null,
            };
    }
}
