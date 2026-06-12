namespace ViewOwl.DTO
{
    /// <summary>
    /// Describes a single output target (device resolution and pixel format)
    /// within a multi-target grab source. Multiple targets allow a single
    /// Chrome screenshot to produce .bin files for displays of different sizes
    /// (e.g. 480x320 TFT and 320x240 TFT from the same HTML template).
    /// </summary>
    public class GrabTargetDTO
    {
        /// <summary>Unique identifier for the output files in ExchangeFolder.</summary>
        public Guid TokenGuid { get; set; }

        /// <summary>Target display width in pixels.</summary>
        public int Width { get; set; }

        /// <summary>Target display height in pixels.</summary>
        public int Height { get; set; }

        /// <summary>
        /// Pixel-format converter to apply.
        /// Must match a registered <c>IImageConverter.FormatId</c>.
        /// Defaults to <c>"bgr565"</c>.
        /// </summary>
        public string ConverterId { get; set; } = "bgr565";
    }
}
