using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViewOwl.Config
{
    /// <summary>
    /// Per-family firmware target resolved for a given display type.
    /// </summary>
    /// <param name="Family">Firmware family key (e.g. "classic", "c3-round").</param>
    /// <param name="RecommendedFirmware">Firmware version tested and recommended for this family.</param>
    /// <param name="MinMajor">Minimum firmware MAJOR version accepted for this family.</param>
    /// <param name="MinMinor">Minimum firmware MINOR version accepted for this family.</param>
    public sealed record FirmwareTarget(string Family, string RecommendedFirmware, byte MinMajor, byte MinMinor);

    /// <summary>
    /// Loads <c>display-types.json</c> — the single source of truth for supported ESP32 display
    /// types — and answers per-display-type firmware lookups.
    /// </summary>
    /// <remarks>
    /// The registry is read ONCE at construction from <see cref="AppContext.BaseDirectory"/>.
    /// If the file is missing or fails to parse the catalog stays empty and lookups return
    /// <c>null</c>; callers fall back to the global <see cref="SharedConfig"/> defaults.
    /// </remarks>
    public sealed class DisplayTypeCatalog
    {
        /// <summary>File name of the registry, expected next to the running binary.</summary>
        private const string RegistryFileName = "display-types.json";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>Default frame format used when a display type does not declare one.</summary>
        private const string DefaultFrameFormat = "bgr565";

        // Display type id -> resolved firmware target. Empty when the registry is absent or invalid.
        private readonly Dictionary<byte, FirmwareTarget> _firmwareTargets;

        // protocolName (case-insensitive) -> frameFormat. Empty when the registry is absent or invalid.
        private readonly Dictionary<string, string> _frameFormats;

        // protocolName (case-insensitive) -> set of supported output formats (case-insensitive).
        // Empty when the registry is absent or invalid.
        private readonly Dictionary<string, HashSet<string>> _supportedFormats;

        // (width, height) -> protocolName. Lets clients that report only a resolution (guest
        // devices) resolve a display type. First declaration wins when several types share a
        // resolution; such types share a frame format, so the choice never changes format lookups.
        private readonly Dictionary<(int Width, int Height), string> _protocolByResolution;

        /// <summary>
        /// Reads and parses the registry into an O(1) lookup table.
        /// Never throws — any failure leaves the catalog empty.
        /// </summary>
        public DisplayTypeCatalog()
        {
            (_firmwareTargets, _frameFormats, _supportedFormats, _protocolByResolution) = Load();
        }

        /// <summary>
        /// Returns the firmware target for the given display type id, or <c>null</c> when the id is
        /// unknown or the registry could not be loaded.
        /// </summary>
        /// <param name="displayTypeId">Display type id as reported by the device (see <c>DisplayType</c>).</param>
        /// <returns>The family firmware target, or <c>null</c> when not found.</returns>
        public FirmwareTarget? GetFirmwareTarget(byte displayTypeId) =>
            _firmwareTargets.TryGetValue(displayTypeId, out FirmwareTarget? target) ? target : null;

        /// <summary>
        /// Returns the output frame format (an <c>IImageConverter.FormatId</c>) declared for the
        /// display type whose protocol name equals <paramref name="displayTypeName"/>.
        /// Falls back to <c>"bgr565"</c> when the name is null, empty, or unknown.
        /// </summary>
        /// <param name="displayTypeName">
        /// Display controller name as stored on the device (e.g. "ILI9341", "EPD").
        /// Matched case-insensitively against the registry's <c>protocolName</c>.
        /// </param>
        /// <returns>The declared frame format, or <c>"bgr565"</c> when not found.</returns>
        public string GetFrameFormat(string? displayTypeName)
        {
            if (string.IsNullOrEmpty(displayTypeName))
            {
                return DefaultFrameFormat;
            }

            return _frameFormats.TryGetValue(displayTypeName, out string? format)
                ? format
                : DefaultFrameFormat;
        }

        /// <summary>
        /// Returns <c>true</c> when the display type identified by <paramref name="displayTypeName"/>
        /// can accept frames in the given <paramref name="format"/>.
        /// </summary>
        /// <remarks>
        /// A format is supported when it appears in the display type's <c>supportedFormats</c>
        /// list, OR it equals the type's default <c>frameFormat</c> — so display types that do
        /// not declare an explicit list still accept their default format.
        /// Never throws; returns <c>false</c> for null/empty arguments or unknown types.
        /// </remarks>
        /// <param name="displayTypeName">
        /// Display controller name as stored on the device (e.g. "ILI9341", "EPD").
        /// Matched case-insensitively against the registry's <c>protocolName</c>.
        /// </param>
        /// <param name="format">Candidate output format (an <c>IImageConverter.FormatId</c>).</param>
        /// <returns><c>true</c> when the format is supported by the named display type.</returns>
        public bool IsFormatSupported(string? displayTypeName, string? format)
        {
            if (string.IsNullOrEmpty(displayTypeName) || string.IsNullOrEmpty(format))
            {
                return false;
            }

            if (_supportedFormats.TryGetValue(displayTypeName, out HashSet<string>? formats)
                && formats.Contains(format))
            {
                return true;
            }

            // Fall back to the declared default frame format so types without an explicit
            // supportedFormats list still accept their default.
            return _frameFormats.TryGetValue(displayTypeName, out string? defaultFormat)
                && string.Equals(defaultFormat, format, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves a display controller protocol name from a pixel resolution, for clients that
        /// report only their resolution (e.g. guest devices) and not a stored display-type name.
        /// </summary>
        /// <remarks>
        /// When several display types share a resolution the first registered wins; such types
        /// share a frame format, so the choice never affects <see cref="GetFrameFormat"/> or
        /// <see cref="IsFormatSupported"/>. Returns <c>null</c> when no display type matches.
        /// </remarks>
        /// <param name="width">Display width in pixels.</param>
        /// <param name="height">Display height in pixels.</param>
        /// <returns>The matching protocol name, or <c>null</c> when no display type has that resolution.</returns>
        public string? GetProtocolNameByResolution(int width, int height) =>
            _protocolByResolution.TryGetValue((width, height), out string? name) ? name : null;

        /// <summary>
        /// Builds the display-type-to-firmware-target map, the protocol-name-to-frame-format
        /// map, and the protocol-name-to-supported-formats map. Returns empty maps on any I/O
        /// or parse failure — there is no logger at this layer, so failures are silent by design.
        /// </summary>
        private static (
            Dictionary<byte, FirmwareTarget> FirmwareTargets,
            Dictionary<string, string> FrameFormats,
            Dictionary<string, HashSet<string>> SupportedFormats,
            Dictionary<(int Width, int Height), string> ProtocolByResolution) Load()
        {
            var map = new Dictionary<byte, FirmwareTarget>();
            var formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var supported = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var byResolution = new Dictionary<(int Width, int Height), string>();

            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, RegistryFileName);
                if (!File.Exists(path))
                {
                    return (map, formats, supported, byResolution);
                }

                string json = File.ReadAllText(path);
                DisplayRegistryModel? registry =
                    JsonSerializer.Deserialize<DisplayRegistryModel>(json, SerializerOptions);

                if (registry?.DisplayTypes is null || registry.FirmwareFamilies is null)
                {
                    return (map, formats, supported, byResolution);
                }

                foreach (DisplayTypeModel displayType in registry.DisplayTypes)
                {
                    // Map protocol name -> frame format independently of the firmware family,
                    // so a display type without a resolvable family still contributes its format.
                    if (!string.IsNullOrEmpty(displayType.ProtocolName)
                        && !string.IsNullOrEmpty(displayType.FrameFormat))
                    {
                        formats[displayType.ProtocolName] = displayType.FrameFormat;
                    }

                    // Index by resolution so a resolution-only client (a guest device) can
                    // resolve a display type. First declaration wins for shared resolutions.
                    if (!string.IsNullOrEmpty(displayType.ProtocolName)
                        && displayType.Width > 0 && displayType.Height > 0)
                    {
                        _ = byResolution.TryAdd(
                            (displayType.Width, displayType.Height), displayType.ProtocolName);
                    }

                    // Map protocol name -> the set of explicitly supported output formats.
                    if (!string.IsNullOrEmpty(displayType.ProtocolName)
                        && displayType.SupportedFormats is { Count: > 0 })
                    {
                        supported[displayType.ProtocolName] = new HashSet<string>(
                            displayType.SupportedFormats, StringComparer.OrdinalIgnoreCase);
                    }

                    if (displayType.Family is null)
                    {
                        continue;
                    }

                    if (!registry.FirmwareFamilies.TryGetValue(displayType.Family, out FirmwareFamilyModel? family)
                        || family is null
                        || family.RecommendedFirmware is null)
                    {
                        continue;
                    }

                    map[displayType.Id] = new FirmwareTarget(
                        displayType.Family,
                        family.RecommendedFirmware,
                        family.MinMajor,
                        family.MinMinor);
                }
            }
            catch (IOException)
            {
                // Registry unreadable — fall back to the global SharedConfig defaults.
                return EmptyMaps();
            }
            catch (JsonException)
            {
                // Malformed registry — fall back to the global SharedConfig defaults.
                return EmptyMaps();
            }
            catch (UnauthorizedAccessException)
            {
                // Permission denied — fall back to the global SharedConfig defaults.
                return EmptyMaps();
            }

            return (map, formats, supported, byResolution);
        }

        /// <summary>Returns a fully-empty set of lookup maps used as the silent failure result.</summary>
        private static (
            Dictionary<byte, FirmwareTarget> FirmwareTargets,
            Dictionary<string, string> FrameFormats,
            Dictionary<string, HashSet<string>> SupportedFormats,
            Dictionary<(int Width, int Height), string> ProtocolByResolution) EmptyMaps() =>
            (
                new Dictionary<byte, FirmwareTarget>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<(int Width, int Height), string>());

        // ── Deserialization models (internal — shape mirrors display-types.json) ──

        /// <summary>Root model of <c>display-types.json</c>.</summary>
        private sealed class DisplayRegistryModel
        {
            /// <summary>Firmware families keyed by family name.</summary>
            [JsonPropertyName("firmwareFamilies")]
            public Dictionary<string, FirmwareFamilyModel>? FirmwareFamilies { get; set; }

            /// <summary>All supported display types.</summary>
            [JsonPropertyName("displayTypes")]
            public List<DisplayTypeModel>? DisplayTypes { get; set; }
        }

        /// <summary>A single firmware family descriptor.</summary>
        private sealed class FirmwareFamilyModel
        {
            /// <summary>Firmware version recommended for this family.</summary>
            [JsonPropertyName("recommendedFirmware")]
            public string? RecommendedFirmware { get; set; }

            /// <summary>Minimum firmware MAJOR version accepted for this family.</summary>
            [JsonPropertyName("minMajor")]
            public byte MinMajor { get; set; }

            /// <summary>Minimum firmware MINOR version accepted for this family.</summary>
            [JsonPropertyName("minMinor")]
            public byte MinMinor { get; set; }
        }

        /// <summary>A single display type descriptor.</summary>
        private sealed class DisplayTypeModel
        {
            /// <summary>Numeric display type id reported by the device.</summary>
            [JsonPropertyName("id")]
            public byte Id { get; set; }

            /// <summary>Firmware family this display type belongs to.</summary>
            [JsonPropertyName("family")]
            public string? Family { get; set; }

            /// <summary>
            /// Display controller protocol name (e.g. "ILI9341", "EPD") — must match the
            /// string stored on the device by <c>HelloPacketHelper.DisplayTypeToString</c>.
            /// </summary>
            [JsonPropertyName("protocolName")]
            public string? ProtocolName { get; set; }

            /// <summary>
            /// Output pixel format for this display type — an <c>IImageConverter.FormatId</c>
            /// such as "bgr565" or "mono1bit".
            /// </summary>
            [JsonPropertyName("frameFormat")]
            public string? FrameFormat { get; set; }

            /// <summary>
            /// Optional list of all output formats this display type can render
            /// (each an <c>IImageConverter.FormatId</c>, e.g. "mono1bit", "mono4gray").
            /// When absent, only the default <see cref="FrameFormat"/> is accepted.
            /// </summary>
            [JsonPropertyName("supportedFormats")]
            public List<string>? SupportedFormats { get; set; }

            /// <summary>Display width in pixels — used to resolve a display type by resolution.</summary>
            [JsonPropertyName("width")]
            public int Width { get; set; }

            /// <summary>Display height in pixels — used to resolve a display type by resolution.</summary>
            [JsonPropertyName("height")]
            public int Height { get; set; }
        }
    }
}
