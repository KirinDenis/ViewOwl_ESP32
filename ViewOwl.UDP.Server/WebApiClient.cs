using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ViewOwl.UDP.Utils;

namespace ViewOwl.UDP.Server
{
    /// <summary>
    /// HTTP client that bridges the UDP Server with the WebAPI internal endpoints.
    /// All methods are intentionally synchronous because the UDP Server uses a
    /// blocking socket loop and cannot await on the hot path.
    /// </summary>
    internal sealed class WebApiClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _http;
        private readonly ILogger<WebApiClient> _logger;

        /// <summary>
        /// Initialises the client. The <paramref name="http"/> instance must have
        /// <see cref="HttpClient.BaseAddress"/> set to the WebAPI base URL.
        /// </summary>
        /// <param name="http">Pre-configured HTTP client.</param>
        /// <param name="logger">Logger.</param>
        public WebApiClient(HttpClient http, ILogger<WebApiClient> logger)
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(logger);
            _http   = http;
            _logger = logger;
        }

        /// <summary>
        /// Resolves a permanent device token to the frame binary that should be streamed over UDP.
        /// </summary>
        /// <param name="deviceToken">Value from the HELLO packet payload.</param>
        /// <returns>
        /// A <see cref="FrameResult"/> where <c>Frame</c> is non-null on success, or
        /// <c>Frame</c> is null with <c>RejectCode</c> set to the matching
        /// <see cref="ErrorCodes"/> value when the device, template, or frame is absent.
        /// </returns>
        public FrameResult GetFrame(string deviceToken)
        {
            try
            {
                var requestUri = new Uri(
                    _http.BaseAddress!,
                    $"api/internal/device/{Uri.EscapeDataString(deviceToken)}/frame");

                // 5-second per-call timeout prevents the UDP listen loop from
                // blocking for the default 100 s when the WebAPI is starting up.
                // On timeout the outer catch returns NoFrame and the device retries.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using HttpResponseMessage response =
                    _http.GetAsync(requestUri, cts.Token).GetAwaiter().GetResult();

                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    // The WebAPI embeds a rejectCode integer in the 404 body so we can forward
                    // the exact reason to the device instead of collapsing all failures to NoFrame.
                    byte rejectCode = (byte)ErrorCodes.NoFrame;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("rejectCode", out var prop))
                            rejectCode = prop.GetByte();
                    }
                    catch (JsonException)
                    {
                        /* Body is plain text — fall back to NoFrame. */
                    }

                    _logger.LogWarning(
                        "GetFrame({Token}): WebAPI returned {Status}, rejectCode={Code}",
                        deviceToken, response.StatusCode, rejectCode);

                    return new FrameResult(null, rejectCode);
                }

                FrameInfo? frame = JsonSerializer.Deserialize<FrameInfo>(body, JsonOptions);
                return new FrameResult(frame, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetFrame({Token}) failed", deviceToken);
                return new FrameResult(null, (byte)ErrorCodes.NoFrame);
            }
        }

        /// <summary>
        /// Notifies the WebAPI when a device sends a HELLO packet (first connection).
        /// Updates device status, IP, and — when an extended HELLO is available — display
        /// type, dimensions, and firmware version.
        /// Failures are logged but never re-thrown — a failed HELLO notification must not
        /// prevent the UDP session from starting.
        /// </summary>
        /// <param name="deviceToken">Permanent device token (GUID string, "N" format).</param>
        /// <param name="endpoint">Remote endpoint the HELLO was received from.</param>
        /// <param name="extended">Optional extended device capabilities from the HELLO struct.</param>
        public void NotifyHello(
            string deviceToken,
            IPEndPoint endpoint,
            HelloPacketPayload? extended,
            string? macAddress = null,
            uint clientFrameCrc = 0)
        {
            try
            {
                string? displayType     = null;
                int?    displayWidth    = null;
                int?    displayHeight   = null;
                string? firmwareVersion = null;

                if (extended.HasValue)
                {
                    HelloPacketPayload d = extended.Value;
                    displayType     = HelloPacketHelper.DisplayTypeToString((DisplayType)d.DisplayTypeId);
                    displayWidth    = d.DisplayWidth;
                    displayHeight   = d.DisplayHeight;
                    firmwareVersion = HelloPacketHelper.FormatFirmwareVersion(
                        d.FirmwareVersionMajor, d.FirmwareVersionMinor, d.FirmwareVersionPatch);
                }

                string json = JsonSerializer.Serialize(new
                {
                    token           = deviceToken,
                    displayType,
                    displayWidth,
                    displayHeight,
                    firmwareVersion,
                    ipAddress       = endpoint.Address.ToString(),
                    uptime          = (int?)null,
                    freeHeap        = (int?)null,
                    wifiRssi        = (int?)null,
                    macAddress,
                    // CRC32 of the last frame the device rendered (0 = no frame yet).
                    clientFrameCrc  = clientFrameCrc != 0 ? (uint?)clientFrameCrc : null,
                }, JsonOptions);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response =
                    _http.PostAsync(new Uri(_http.BaseAddress!, "api/internal/device-heartbeat"), content)
                         .GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "NotifyHello({Token}): WebAPI returned {Status}",
                        deviceToken, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NotifyHello({Token}) failed", deviceToken);
            }
        }

        /// <summary>
        /// Notifies the WebAPI about a PING heartbeat received from a device.
        /// Updates the device's online status, IP address, and health metrics in the database.
        /// Failures are logged but never re-thrown — a missed heartbeat is non-fatal.
        /// </summary>
        /// <param name="deviceToken">Permanent device token (from the active session).</param>
        /// <param name="endpoint">Remote endpoint the PING was received from.</param>
        /// <param name="ping">Parsed PING payload with health metrics.</param>
        public void NotifyHeartbeat(string deviceToken, IPEndPoint endpoint, PingPacketPayload ping)
        {
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    token           = deviceToken,
                    displayType     = (string?)null,  // not carried in PING — only in HELLO
                    displayWidth    = (int?)null,
                    displayHeight   = (int?)null,
                    firmwareVersion = (string?)null,
                    ipAddress       = endpoint.Address.ToString(),
                    uptime          = (int)ping.Uptime,
                    freeHeap        = (int)ping.FreeHeap,
                    // WiFi RSSI is always negative; 0 means esp_wifi_sta_get_ap_info failed — treat as null.
                    wifiRssi        = ping.WifiRssi != 0 ? (int?)ping.WifiRssi : null,
                }, JsonOptions);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response =
                    _http.PostAsync(new Uri(_http.BaseAddress!, "api/internal/device-heartbeat"), content)
                         .GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "NotifyHeartbeat({Token}): WebAPI returned {Status}",
                        deviceToken, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NotifyHeartbeat({Token}) failed", deviceToken);
            }
        }

        /// <summary>
        /// Notifies the WebAPI that a device sent a HELLO but could not be authenticated.
        /// Triggers a <c>DeviceKnocking</c> SignalR event so the dashboard can show the attempt.
        /// When reason is <c>"no-frame"</c>, also writes a <c>udp_no_frame</c> pipeline event.
        /// Failures are logged but never re-thrown.
        /// </summary>
        /// <param name="endpoint">Remote endpoint the HELLO was received from.</param>
        /// <param name="rawToken">Token string extracted from the HELLO payload ("?" if unparseable).</param>
        /// <param name="reason">Short reason code: "bad-token" | "no-device" | "no-template" | "no-frame".</param>
        /// <param name="deviceId">DB device id when known (used for pipeline event logging).</param>
        /// <param name="templateId">DB template id when known (used for pipeline event logging).</param>
        public void NotifyKnocking(
            IPEndPoint endpoint,
            string     rawToken,
            string     reason,
            int?       deviceId   = null,
            int?       templateId = null)
        {
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    token     = rawToken,
                    ipAddress = endpoint.Address.ToString(),
                    reason,
                }, JsonOptions);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response =
                    _http.PostAsync(new Uri(_http.BaseAddress!, "api/internal/device-knocking"), content)
                         .GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "NotifyKnocking({Token}): WebAPI returned {Status}",
                        rawToken, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NotifyKnocking({Token}) failed", rawToken);
            }

            // When a device with a valid token asks for a frame that doesn't exist yet,
            // log a udp_no_frame pipeline event so the operator knows to trigger a grab.
            if (string.Equals(reason, "no-frame", StringComparison.Ordinal))
            {
                LogPipelineEvent(
                    "udp_no_frame",
                    deviceId:   deviceId,
                    templateId: templateId,
                    success:    false,
                    errorMessage: "Device requested a frame that does not exist yet");
            }
        }

        /// <summary>
        /// Asks the WebAPI whether there is a pending reboot command or a pending
        /// frame-refresh for the device identified by <paramref name="deviceToken"/>.
        /// Pending restart commands are marked as <c>sent</c>; pending frame-refreshes
        /// are consumed atomically from the in-memory queue.
        /// Failures are logged but never re-thrown — a missed check is non-fatal.
        /// </summary>
        /// <param name="deviceToken">Permanent device token (GUID "N" format).</param>
        /// <returns>
        /// A tuple where <c>ShouldRestart</c> signals a pending reboot and
        /// <c>ShouldRefresh</c> signals that a new frame is ready to be fetched.
        /// Both are <c>false</c> on network or parse errors.
        /// </returns>
        public (bool ShouldRestart, bool ShouldRefresh) ConsumeRestart(string deviceToken)
        {
            try
            {
                var requestUri = new Uri(
                    _http.BaseAddress!,
                    $"api/internal/device/{Uri.EscapeDataString(deviceToken)}/consume-restart");

                using HttpResponseMessage response =
                    _http.PostAsync(requestUri, content: null).GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "ConsumeRestart({Token}): WebAPI returned {Status}",
                        deviceToken, response.StatusCode);
                    return (false, false);
                }

                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = System.Text.Json.JsonDocument.Parse(body);

                bool shouldRestart = doc.RootElement.TryGetProperty("hasPendingRestart", out var restartProp)
                    && restartProp.GetBoolean();

                bool shouldRefresh = doc.RootElement.TryGetProperty("hasPendingFrameRefresh", out var refreshProp)
                    && refreshProp.GetBoolean();

                return (shouldRestart, shouldRefresh);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ConsumeRestart({Token}) failed", deviceToken);
                return (false, false);
            }
        }

        /// <summary>
        /// Notifies the WebAPI that a data transfer to a device has started (AUTH sent,
        /// DATA loop beginning). Triggers a <c>DeviceTransferStarted</c> SignalR event and
        /// writes a <c>udp_session_opened</c> pipeline event.
        /// Failures are logged but never re-thrown — a missed notification is non-fatal.
        /// </summary>
        /// <param name="deviceToken">Permanent device token.</param>
        /// <param name="templateId">Active template id for this transfer.</param>
        /// <param name="imageSizeBytes">Frame file size in bytes.</param>
        /// <param name="fileFlag">Frame compression flag byte (0x00=Raw, 0x03=LZ4+palette, 0x04=Delta).</param>
        /// <param name="deviceId">DB device id (used for pipeline event logging).</param>
        /// <param name="sessionId">UDP session id assigned at AUTH time (used for pipeline event logging).</param>
        public void NotifyTransferStarted(
            string  deviceToken,
            int     templateId,
            int     imageSizeBytes,
            int?    fileFlag  = null,
            int?    deviceId  = null,
            string? sessionId = null)
        {
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    token = deviceToken,
                    templateId,
                    imageSizeBytes,
                    fileFlag,
                }, JsonOptions);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response =
                    _http.PostAsync(new Uri(_http.BaseAddress!, "api/internal/device-transfer-started"), content)
                         .GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "NotifyTransferStarted({Token}): WebAPI returned {Status}",
                        deviceToken, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NotifyTransferStarted({Token}) failed", deviceToken);
            }

            // Log udp_session_opened pipeline event (fire-and-forget — failures are non-fatal).
            string? openPayload = JsonSerializer.Serialize(new
            {
                imageSizeBytes,
                fileFlag,
            }, JsonOptions);
            LogPipelineEvent(
                "udp_session_opened",
                deviceId:   deviceId,
                templateId: templateId,
                sessionId:  sessionId,
                success:    true,
                payload:    openPayload);
        }

        /// <summary>
        /// Notifies the WebAPI that a UDP transfer session has completed.
        /// Triggers a <c>DeviceGrabCompleted</c> SignalR event with outcome and duration,
        /// and writes the matching pipeline event (<c>udp_done</c>, <c>udp_not_modified</c>,
        /// or <c>udp_failed</c>).
        /// Failures are logged but never re-thrown.
        /// </summary>
        /// <param name="deviceToken">Permanent device token.</param>
        /// <param name="templateId">Active template id.</param>
        /// <param name="success"><c>true</c> when all DATA chunks were delivered.</param>
        /// <param name="imageSizeBytes">Frame file size in bytes.</param>
        /// <param name="durationMs">Transfer duration in milliseconds.</param>
        /// <param name="errorMessage">Optional failure reason.</param>
        /// <param name="outcome">"success" | "not_modified" | "fail"</param>
        /// <param name="retries">Total retransmit count during this transfer.</param>
        /// <param name="throughputKBps">Average throughput in KB/s, or null when not applicable.</param>
        /// <param name="deviceId">DB device id (used for pipeline event logging).</param>
        /// <param name="sessionId">UDP session id (used for pipeline event logging).</param>
        public void NotifyGrabComplete(
            string  deviceToken,
            int     templateId,
            bool    success,
            int     imageSizeBytes,
            long    durationMs,
            string? errorMessage,
            string? outcome      = null,
            int     retries      = 0,
            int?    throughputKBps = null,
            int?    deviceId     = null,
            string? sessionId    = null)
        {
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    token = deviceToken,
                    templateId,
                    success,
                    errorMessage,
                    imageSizeBytes,
                    durationMs = (int)Math.Min(durationMs, int.MaxValue),
                    outcome,
                    retries,
                    throughputKBps,
                }, JsonOptions);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response =
                    _http.PostAsync(new Uri(_http.BaseAddress!, "api/internal/device-grab-complete"), content)
                         .GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "NotifyGrabComplete({Token}, success={Success}): WebAPI returned {Status}",
                        deviceToken, success, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NotifyGrabComplete({Token}) failed", deviceToken);
            }

            // Map outcome to pipeline event type and log it (fire-and-forget).
            string pipelineEventType = outcome switch
            {
                "not_modified" => "udp_not_modified",
                "success"      => "udp_done",
                _              => success ? "udp_done" : "udp_failed",
            };

            string? donePayload = JsonSerializer.Serialize(new
            {
                imageSizeBytes,
                retries,
                throughputKBps,
            }, JsonOptions);

            LogPipelineEvent(
                pipelineEventType,
                deviceId:     deviceId,
                templateId:   templateId,
                sessionId:    sessionId,
                success:      success,
                durationMs:   (int)Math.Min(durationMs, int.MaxValue),
                payload:      donePayload,
                errorMessage: errorMessage);
        }

        /// <summary>
        /// Notifies the WebAPI of live progress during an active UDP transfer.
        /// Called every W=8 ACKed chunks. Failures are logged but never re-thrown.
        /// </summary>
        /// <param name="deviceToken">Permanent device token.</param>
        /// <param name="chunksSent">Number of chunks ACKed so far.</param>
        /// <param name="totalChunks">Total number of chunks in this transfer.</param>
        /// <param name="elapsedMs">Elapsed transfer time in milliseconds.</param>
        /// <param name="retries">Cumulative retransmit count so far.</param>
        public void NotifyTransferProgress(string deviceToken, int chunksSent, int totalChunks, int elapsedMs, int retries)
        {
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    token = deviceToken,
                    chunksSent,
                    totalChunks,
                    elapsedMs,
                    retries,
                }, JsonOptions);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response =
                    _http.PostAsync(new Uri(_http.BaseAddress!, "api/internal/device-transfer-progress"), content)
                         .GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "NotifyTransferProgress({Token}): WebAPI returned {Status}",
                        deviceToken, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NotifyTransferProgress({Token}) failed", deviceToken);
            }
        }

        /// <summary>
        /// Appends a pipeline state-transition event to the server's <c>pipeline_events</c> table.
        /// Called at key UDP lifecycle points so the dashboard gains end-to-end observability.
        /// Failures are logged but never re-thrown — pipeline event logging must never disrupt the
        /// UDP session.
        /// </summary>
        /// <param name="eventType">One of the <c>PipelineEventTypes.*</c> string constants.</param>
        /// <param name="deviceId">DB device id (null for grab-only events).</param>
        /// <param name="templateId">DB template id (null for device-only events).</param>
        /// <param name="sessionId">UDP session id (null for non-UDP events).</param>
        /// <param name="success">Outcome of the operation (null when not applicable).</param>
        /// <param name="durationMs">Operation duration in milliseconds (null when not applicable).</param>
        /// <param name="payload">JSON string with event-specific detail fields (null when not needed).</param>
        /// <param name="errorMessage">Human-readable failure description (null on success).</param>
        public void LogPipelineEvent(
            string  eventType,
            int?    deviceId    = null,
            int?    templateId  = null,
            string? sessionId   = null,
            bool?   success     = null,
            int?    durationMs  = null,
            string? payload     = null,
            string? errorMessage = null)
        {
            // Fire-and-forget — pipeline logging must never block the UDP session thread.
            Task.Run(() =>
            {
                try
                {
                    string json = JsonSerializer.Serialize(new
                    {
                        eventType,
                        deviceId,
                        templateId,
                        sessionId,
                        success,
                        durationMs,
                        payload,
                        errorMessage,
                    }, JsonOptions);

                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response =
                        _http.PostAsync(new Uri(_http.BaseAddress!, "api/pipeline/event"), content)
                             .GetAwaiter().GetResult();

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug(
                            "LogPipelineEvent({Type}): WebAPI returned {Status}",
                            eventType, response.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "LogPipelineEvent({Type}) failed", eventType);
                }
            });
        }

        /// <summary>
        /// Records the outcome of a completed UDP session as a device ping.
        /// Failures are logged but never re-thrown — the UDP session is already finished.
        /// </summary>
        /// <param name="deviceToken">Permanent device token.</param>
        /// <param name="success"><c>true</c> when all DATA chunks were delivered successfully.</param>
        public void RecordPing(string deviceToken, bool success)
        {
            try
            {
                string json  = JsonSerializer.Serialize(new { success }, JsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var requestUri = new Uri(
                    _http.BaseAddress!,
                    $"api/internal/device/{Uri.EscapeDataString(deviceToken)}/ping");

                using HttpResponseMessage response =
                    _http.PostAsync(requestUri, content).GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "RecordPing({Token}, success={Success}): WebAPI returned {Status}",
                        deviceToken, success, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                // Non-fatal — ping recording must never disrupt the UDP session lifecycle.
                _logger.LogWarning(ex, "RecordPing({Token}) failed", deviceToken);
            }
        }
    }

    /// <summary>
    /// Frame resolution result deserialized from <c>GET /api/internal/device/{token}/frame</c>.
    /// </summary>
    internal sealed class FrameInfo
    {
        /// <summary>Registered device id.</summary>
        [JsonPropertyName("deviceId")]
        public int DeviceId { get; set; }

        /// <summary>Active template id.</summary>
        [JsonPropertyName("templateId")]
        public int TemplateId { get; set; }

        /// <summary>TokenGuid of the last successful grab; the bin file is named <c>{TokenGuid}.bin</c>.</summary>
        [JsonPropertyName("tokenGuid")]
        public Guid TokenGuid { get; set; }

        /// <summary>Absolute path to the binary frame file on disk.</summary>
        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Absolute path to the batch manifest JSON sidecar when this is a Class-C multi-frame
        /// template (<c>{guid}_batch.json</c>).  <c>null</c> for regular single-frame templates.
        /// When non-null the UDP Server should use <c>StartBatchSender</c> instead of
        /// <c>StartSender</c>.
        /// </summary>
        [JsonPropertyName("batchManifestPath")]
        public string? BatchManifestPath { get; set; }
    }

    /// <summary>
    /// Typed result returned by <see cref="WebApiClient.GetFrame"/>.
    /// </summary>
    /// <param name="Frame">Resolved frame info, or <c>null</c> when auth should be rejected.</param>
    /// <param name="RejectCode">
    /// One of the <see cref="ErrorCodes"/> byte values describing why the frame could not be
    /// resolved. Meaningful only when <paramref name="Frame"/> is <c>null</c>.
    /// </param>
    internal sealed record FrameResult(FrameInfo? Frame, byte RejectCode)
    {
        /// <summary><c>true</c> when a frame was successfully resolved.</summary>
        public bool Success => Frame is not null;
    }
}
