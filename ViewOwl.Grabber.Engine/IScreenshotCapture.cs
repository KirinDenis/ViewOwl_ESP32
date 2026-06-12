using ViewOwl.DTO;

namespace ViewOwl.Grabber.Engine
{
    /// <summary>
    /// Internal contract for capturing screenshots from open browser tabs.
    /// This interface is intentionally separate from <see cref="IGrabber"/>:
    /// screenshot capture is an implementation detail consumed only by
    /// <c>GrabWorker</c>, not exposed as part of the public grabber API.
    /// </summary>
    public interface IScreenshotCapture
    {
        /// <summary>
        /// Takes a screenshot of the browser tab identified by
        /// <see cref="ScreenShotParamDTO.TabName"/>, converts the image using the
        /// converter specified by <see cref="ScreenShotParamDTO.ConverterId"/>,
        /// and writes the raw binary to the exchange folder.
        /// </summary>
        /// <param name="param">
        /// Screenshot parameters: tab URL, output dimensions, token GUID, and converter id.
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        Task TakeScreenshotAsync(ScreenShotParamDTO param, CancellationToken ct = default);
    }
}
