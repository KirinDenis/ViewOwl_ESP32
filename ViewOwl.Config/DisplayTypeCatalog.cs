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

        // Display type id -> resolved firmware target. Empty when the registry is absent or invalid.
        private readonly Dictionary<byte, FirmwareTarget> _firmwareTargets;

        /// <summary>
        /// Reads and parses the registry into an O(1) lookup table.
        /// Never throws — any failure leaves the catalog empty.
        /// </summary>
        public DisplayTypeCatalog()
        {
            _firmwareTargets = Load();
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
        /// Builds the display-type-to-firmware-target map. Returns an empty map on any I/O or
        /// parse failure — there is no logger at this layer, so failures are silent by design.
        /// </summary>
        private static Dictionary<byte, FirmwareTarget> Load()
        {
            var map = new Dictionary<byte, FirmwareTarget>();

            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, RegistryFileName);
                if (!File.Exists(path))
                {
                    return map;
                }

                string json = File.ReadAllText(path);
                DisplayRegistryModel? registry =
                    JsonSerializer.Deserialize<DisplayRegistryModel>(json, SerializerOptions);

                if (registry?.DisplayTypes is null || registry.FirmwareFamilies is null)
                {
                    return map;
                }

                foreach (DisplayTypeModel displayType in registry.DisplayTypes)
                {
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
                return new Dictionary<byte, FirmwareTarget>();
            }
            catch (JsonException)
            {
                // Malformed registry — fall back to the global SharedConfig defaults.
                return new Dictionary<byte, FirmwareTarget>();
            }
            catch (UnauthorizedAccessException)
            {
                // Permission denied — fall back to the global SharedConfig defaults.
                return new Dictionary<byte, FirmwareTarget>();
            }

            return map;
        }

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
        }
    }
}
