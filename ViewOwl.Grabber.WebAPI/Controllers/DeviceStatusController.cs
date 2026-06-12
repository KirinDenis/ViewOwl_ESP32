using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ViewOwl.Config;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    /// <summary>
    /// Provides live device status data consumed by the <c>device_status.html</c> template
    /// running inside headless Chromium on the server. No JWT is required — the device token
    /// in the URL serves as the identifier (the template has the token baked in at creation time).
    /// </summary>
    [ApiController]
    [Route("api/device")]
    public sealed class DeviceStatusController : ControllerBase
    {
        private readonly IDeviceRepository _devices;
        private readonly ITemplateRepository _templates;
        private readonly IGrabLogRepository _grabLogs;
        private readonly IDevicePingRepository _pings;
        private readonly SharedConfig _sharedConfig;

        /// <summary>
        /// Initialises the controller.
        /// </summary>
        public DeviceStatusController(
            IDeviceRepository devices,
            ITemplateRepository templates,
            IGrabLogRepository grabLogs,
            IDevicePingRepository pings,
            IOptions<SharedConfig> sharedOptions)
        {
            ArgumentNullException.ThrowIfNull(sharedOptions);
            _devices      = devices;
            _templates    = templates;
            _grabLogs     = grabLogs;
            _pings        = pings;
            _sharedConfig = sharedOptions.Value;
        }

        /// <summary>
        /// Returns a flat JSON object representing the live status of the device identified
        /// by <paramref name="token"/>. The shape mirrors the <c>DEV</c> object used by
        /// <c>device_status.html</c> so the template can assign the response directly.
        /// </summary>
        /// <param name="token">Permanent device token (32-char "N" format GUID).</param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("{token}/status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetStatus(string token, CancellationToken ct = default)
        {
            Device? device = await _devices.GetByTokenAsync(token, ct).ConfigureAwait(false);
            if (device is null)
                return NotFound();

            // ── Grab stats ───────────────────────────────────────────────────────────

            string activeTemplateName = "NONE";
            double? lastGrabSeconds   = null;
            int grabsToday            = 0;
            double successRate        = 1.0;

            if (device.ActiveTemplateId.HasValue)
            {
                Template? tpl = await _templates
                    .GetByIdAsync(device.ActiveTemplateId.Value, ct)
                    .ConfigureAwait(false);

                if (tpl is not null)
                    activeTemplateName = tpl.Name;

                GrabLog? lastGrab = await _grabLogs
                    .GetLastSuccessfulAsync(device.ActiveTemplateId.Value, ct)
                    .ConfigureAwait(false);

                if (lastGrab?.CompletedAt is not null)
                    lastGrabSeconds = (DateTime.UtcNow - lastGrab.CompletedAt.Value).TotalSeconds;

                var todayStats = await _grabLogs
                    .GetTodayStatsAsync(device.ActiveTemplateId.Value, ct)
                    .ConfigureAwait(false);

                grabsToday  = todayStats.Successful;
                successRate = todayStats.Total > 0
                    ? (double)todayStats.Successful / todayStats.Total
                    : 1.0;
            }

            // ── Session count today ───────────────────────────────────────────────────

            int sessionsToday = await _pings
                .GetTodayCountAsync(device.Id, ct)
                .ConfigureAwait(false);

            // ── Derived fields ────────────────────────────────────────────────────────

            // Format token as two display segments: "07BC·AEFB" and "728B·41E1"
            string tokenA = FormatTokenSegment(device.Token, 0);
            string tokenB = FormatTokenSegment(device.Token, 8);

            int displayW = device.DisplayWidth  ?? 480;
            int displayH = device.DisplayHeight ?? 320;
            int frameBytes = displayW * displayH * 2;   // BGR565 = 2 bytes per pixel
            string frameSizeLabel = FormatBytes(frameBytes);

            // Server hostname from WebApiBaseUrl config (strip scheme, use host only).
            string serverHost = ExtractHost(_sharedConfig.WebApiBaseUrl);

            return Ok(new
            {
                mac              = device.MacAddress ?? "--",
                tokenA,
                tokenB,
                display          = $"{displayW}×{displayH}",
                driver           = device.DisplayType ?? "---",
                firmware         = device.FirmwareVersion is not null
                                       ? $"v{device.FirmwareVersion}"
                                       : "v?.?.?",
                ip               = device.IpAddress ?? "---.---.---",
                rssi             = device.WifiRssi,
                pingMs           = (int?)null,          // not tracked; reserved for future
                server           = serverHost,
                online           = device.Status == DeviceStatus.Online,
                lastGrabS        = lastGrabSeconds.HasValue ? (int)lastGrabSeconds.Value : 0,
                refreshS         = 300,
                template         = activeTemplateName,
                grabsToday,
                successRate      = Math.Round(successRate, 4),
                sessionsToday,
                // Compute live uptime from boot time so it keeps counting between PINGs.
                // Fall back to the raw stored value when LastBootAt is not yet known.
                uptimeS          = device.LastBootAt.HasValue
                                       ? (int)(DateTime.UtcNow - device.LastBootAt.Value).TotalSeconds
                                       : device.UptimeSeconds,
                cpuPct           = (int?)null,           // not tracked; requires firmware extension
                ramFreeKB        = device.FreeHeap.HasValue ? device.FreeHeap / 1024 : (int?)null,
                ramTotalKB       = (int?)null,           // not reported by firmware
                flashMB          = (double?)null,        // not reported by firmware
                // CRC32 reported by device in last HELLO — shows what it is currently rendering.
                crc              = device.LastFrameCrc.HasValue
                                       ? $"0x{device.LastFrameCrc.Value:X4}·{device.LastFrameCrc.Value & 0xFFFF:X4}"
                                       : (string?)null,
                crcMatch         = true,
                frameSizeLabel,
                frameSent        = device.FramesSentCount,
                frameAgoS        = lastGrabSeconds.HasValue ? (int)lastGrabSeconds.Value : 0,
                packets          = (int?)null,
                retransmits      = (int?)null,
                quality          = (double?)null,
            });
        }

        // ── Private helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Formats 8 hex characters from <paramref name="token"/> starting at
        /// <paramref name="offset"/> as "XXXX·XXXX" for display on the LCD.
        /// </summary>
        private static string FormatTokenSegment(string token, int offset)
        {
            if (token.Length < offset + 8)
                return "----·----";

            string raw = token.Substring(offset, 8).ToUpperInvariant();
            return $"{raw[..4]}·{raw[4..]}";
        }

        /// <summary>Formats a byte count as "NNN NNN B" with thousands separator.</summary>
        private static string FormatBytes(int bytes)
        {
            string s = bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Insert spaces every 3 digits from the right (e.g., "307200" → "307 200")
            var chars = new System.Text.StringBuilder();
            int rem = s.Length % 3;
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && (i - rem) % 3 == 0)
                    chars.Append(' ');
                chars.Append(s[i]);
            }

            return chars.Append(" B").ToString();
        }

        /// <summary>Extracts the host (and port if non-standard) from a URL string.</summary>
        private static string ExtractHost(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "---";

            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return uri.Host;

            return url;
        }
    }
}
