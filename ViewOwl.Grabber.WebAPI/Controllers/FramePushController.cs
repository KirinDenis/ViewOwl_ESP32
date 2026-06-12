using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Grabber.WebAPI.Services;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Accepts raw BGR565 frame data pushed directly from the browser dashboard.
    /// Writes the frame atomically to the ExchangeFolder so the UDP Server
    /// serves it on the next HELLO from any device assigned to that template.
    /// </summary>
    [ApiController]
    [Route("api/templates")]
    [Authorize]
    public sealed class FramePushController : ControllerBase
    {
        // Server-side ceiling: 16 ms ≈ 60 fps absolute max.
        // Protects against accidental tight loops without restricting any real use-case.
        private const int MinIntervalMs = 16;

        // 1 MB covers raw BGR565 (307 200 B) plus generous headroom for future formats.
        private const long MaxBodyBytes = 1_048_576;

        private readonly ITemplateRepository _templates;
        private readonly IGrabLogRepository _grabLogs;
        private readonly SharedConfig _sharedConfig;
        private readonly IFramePushThrottle _throttle;
        private readonly IDeviceRepository _devices;
        private readonly IFrameRefreshQueue _refreshQueue;
        private readonly ILogger<FramePushController> _logger;

        /// <summary>Initialises the controller with its dependencies.</summary>
        public FramePushController(
            ITemplateRepository templates,
            IGrabLogRepository grabLogs,
            IOptions<SharedConfig> sharedOptions,
            IFramePushThrottle throttle,
            IDeviceRepository devices,
            IFrameRefreshQueue refreshQueue,
            ILogger<FramePushController> logger)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            _templates    = templates;
            _grabLogs     = grabLogs;
            _sharedConfig = sharedOptions.Value;
            _throttle     = throttle;
            _devices      = devices;
            _refreshQueue = refreshQueue;
            _logger       = logger;
        }

        /// <summary>
        /// Accepts a raw BGR565 (or RLE-compressed) frame and atomically writes it to
        /// <c>ExchangeFolder/{TokenGuid}.bin</c> for immediate UDP delivery.
        /// </summary>
        /// <remarks>
        /// Rate-limited to <c>60 fps</c> server-side. The browser controls the actual rate.
        /// <para>
        /// Accepted frame formats (matching <c>FrameCompressor</c> flags):
        /// <c>0x00</c> raw BGR565 (flag + pixels), <c>0x01</c> palette+RLE,
        /// <c>0x03</c> palette+LZ4 independent blocks.
        /// Legacy bare raw (no flag byte, exact <c>width × height × 2</c> bytes) is also accepted.
        /// </para>
        /// </remarks>
        /// <param name="id">Template id (must be owned by the authenticated user).</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpPost("{id:int}/frame")]
        [Consumes("application/octet-stream")]
        [RequestSizeLimit(MaxBodyBytes)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> PushFrame(
            int id,
            [FromQuery] int? w,
            [FromQuery] int? h,
            CancellationToken ct = default)
        {
            int userId = GetUserId();

            var template = await _templates.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (template is null)
                return NotFound();

            if (template.UserId != userId)
                return Forbid();

            // Fail fast before reading the body — avoids buffering when rate-limited.
            if (!_throttle.TryAcquire(id, MinIntervalMs))
                return StatusCode(StatusCodes.Status429TooManyRequests);

            // Resolve the bin file path from the last successful grab.
            // The browser push overwrites this file so the UDP Server picks it up immediately
            // without any DB change — the TokenGuid stays the same.
            // When the browser passes ?w=&h= (push resolution), use the resolution-aware
            // overload so the frame lands in the correct .bin for that display size.
            // Falls back to resolution-agnostic for legacy callers without query params.
            var lastLog = (w.HasValue && h.HasValue)
                ? await _grabLogs.GetLastSuccessfulAsync(id, w.Value, h.Value, ct).ConfigureAwait(false)
                : await _grabLogs.GetLastSuccessfulAsync(id, ct).ConfigureAwait(false);
            if (lastLog is null)
            {
                return UnprocessableEntity(
                    "No server-side grab exists for this template yet. " +
                    "Trigger one grab from the server first so the output file is initialised.");
            }

            // Buffer the body (limited by RequestSizeLimit above).
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            byte[] frameBytes = ms.ToArray();

            // Validate: must be a recognised frame format.
            //   0x00 = raw BGR565 fallback (flag byte + width×height×2 raw bytes)
            //   0x01 = palette+RLE   (FRAME_FLAG_PALETTE)
            //   0x03 = palette+LZ4   (FRAME_FLAG_LZ4_PALETTE)
            int  expectedRaw  = lastLog.Width * lastLog.Height * 2;
            bool isLegacyRaw  = frameBytes.Length == expectedRaw;     // no flag byte (legacy)
            bool isKnownFlag  = frameBytes.Length >= 2 &&
                                (frameBytes[0] == 0x00 ||              // raw with flag
                                 frameBytes[0] == 0x01 ||              // palette+RLE
                                 frameBytes[0] == 0x03);               // palette+LZ4

            if (!isLegacyRaw && !isKnownFlag)
            {
                return BadRequest(
                    $"Invalid frame: got {frameBytes.Length} bytes, flag=0x{(frameBytes.Length > 0 ? frameBytes[0] : 0):X2}. " +
                    $"Expected {expectedRaw} bytes raw BGR565 or a compressed frame with flag 0x00/0x01/0x03.");
            }

            // Atomic write: write to a .tmp file, then rename over the target.
            // On Linux ext4 (production target) File.Move is a single rename(2) syscall —
            // the UDP Server either reads the old file or the new one, never a partial write.
            string binPath = Path.Combine(_sharedConfig.ExchangeFolder, $"{lastLog.TokenGuid}.bin");
            string tmpPath = binPath + ".tmp";

            await System.IO.File.WriteAllBytesAsync(tmpPath, frameBytes, ct).ConfigureAwait(false);
            System.IO.File.Move(tmpPath, binPath, overwrite: true);

            _logger.LogDebug(
                "Browser push: template={Id} user={UserId} bytes={Bytes} path={Path}",
                id, userId, frameBytes.Length, binPath);

            // Notify all devices currently showing this template so the UDP server
            // sends them an early AUTH trigger on their next PING heartbeat.
            var devices = await _devices.GetByActiveTemplateIdAsync(id, ct).ConfigureAwait(false);
            foreach (var device in devices)
                _refreshQueue.ScheduleRefresh(device.Id);

            return NoContent();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private int GetUserId()
        {
            string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            return int.TryParse(sub, out int id) ? id : 0;
        }
    }
}
