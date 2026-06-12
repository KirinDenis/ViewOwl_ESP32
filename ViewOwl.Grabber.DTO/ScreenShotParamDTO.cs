namespace ViewOwl.DTO
{
    /// <summary>
    /// Parameters for a single screenshot capture request queued via <c>GrabQueue</c>.
    /// </summary>
    public class ScreenShotParamDTO
    {
        /// <summary>
        /// Identifies the output files in ExchangeFolder:
        /// <c>{TokenGuid}.bin</c> (pixel data) and <c>{TokenGuid}_TEST.png</c> (debug).
        /// </summary>
        public Guid TokenGuid { get; set; }

        /// <summary>Final URL of the browser tab to capture, as returned by <c>AddUrlAsync</c>.</summary>
        public string TabName { get; set; } = string.Empty;

        /// <summary>Target pixel width for the captured frame.</summary>
        public int Width { get; set; }

        /// <summary>Target pixel height for the captured frame.</summary>
        public int Height { get; set; }

        /// <summary>
        /// Selects the pixel-format converter to apply.
        /// Must match <c>IImageConverter.FormatId</c> of a registered converter.
        /// Defaults to <c>"bgr565"</c> (TFT/ILI9341 compatible).
        /// </summary>
        public string ConverterId { get; set; } = "bgr565";

        /// <summary>
        /// Optional <c>GrabLog.Id</c> to update with the outcome of this screenshot.
        /// <c>null</c> for periodic background captures (no log row to update).
        /// </summary>
        public int? GrabLogId { get; set; }

        /// <summary>
        /// When <c>true</c> the grabber removes the browser tab after the screenshot completes
        /// (used for on-demand grabs so ephemeral tabs do not accumulate).
        /// </summary>
        public bool CloseTabAfterCapture { get; set; }

        /// <summary>Template id — used to route SignalR completion events.</summary>
        public int TemplateId { get; set; }

        /// <summary>Owner user id — used to route SignalR notifications to the correct group.</summary>
        public int UserId { get; set; }

        /// <summary>
        /// When <c>true</c> the grabber injects <c>filter: grayscale(1)</c> before capture.
        /// Mirrors <see cref="ViewOwl.Data.Models.Template.Monochrome"/>.
        /// </summary>
        public bool Monochrome { get; set; }

        /// <summary>
        /// When non-empty, a single Chrome screenshot is resized and converted once
        /// per target, producing multiple .bin files for displays of different sizes.
        /// The top-level <see cref="TokenGuid"/>, <see cref="Width"/>, <see cref="Height"/>,
        /// and <see cref="ConverterId"/> are ignored when this list is populated.
        /// </summary>
        public List<GrabTargetDTO> Targets { get; set; }
    }
}
