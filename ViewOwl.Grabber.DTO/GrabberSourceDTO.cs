namespace ViewOwl.DTO
{
    /// <summary>
    /// Describes a single display source in <c>GrabbedList.json</c>.
    /// </summary>
    public class GrabberSourceDTO
    {
        /// <summary>
        /// HTTP/HTTPS URL or local template path (e.g. <c>"SitesTemplates/mypage.html"</c>).
        /// Local paths are served via <c>SiteTemplateController</c> to work around
        /// Chromium ARM64's <c>file://</c> restriction.
        /// </summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Reserved — no longer populated at runtime.
        /// <c>GrabberWorker</c> now opens ephemeral tabs per cycle rather than storing
        /// persistent tab URLs in this field.  Kept for JSON round-trip compatibility.
        /// </summary>
        public string TabName { get; set; } = string.Empty;

        /// <summary>Unique identifier for the output files in ExchangeFolder.</summary>
        public Guid TokenGuid { get; set; }

        /// <summary>Target display width in pixels.</summary>
        public uint Width { get; set; } = 480;

        /// <summary>Target display height in pixels.</summary>
        public uint Height { get; set; } = 320;

        /// <summary>
        /// Pixel-format converter to apply when capturing this source.
        /// Defaults to <c>"bgr565"</c>. Must match a registered <c>IImageConverter.FormatId</c>.
        /// </summary>
        public string ConverterId { get; set; } = "bgr565";

        /// <summary>
        /// Optional list of output targets when the same source must be captured
        /// at multiple resolutions (e.g. 480x320 and 320x240 from one HTML template).
        /// When non-empty, <see cref="TokenGuid"/>, <see cref="Width"/>,
        /// <see cref="Height"/>, and <see cref="ConverterId"/> are ignored —
        /// each target defines its own output parameters.
        /// </summary>
        public List<GrabTargetDTO> Targets { get; set; }
    }
}
