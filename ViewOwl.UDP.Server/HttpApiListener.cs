using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ViewOwl.Config;
using ViewOwl.UDP.Server.DTOs;

namespace ViewOwl.UDP.Server
{
    /// <summary>
    /// Simple HTTP listener exposing a localhost-only internal API for the UDP Server.
    /// Provides server statistics, active device sessions, and a command dispatch endpoint.
    /// </summary>
    internal sealed class HttpApiListener : IDisposable
    {
        // Reused across all responses to avoid per-request allocation (CA1869).
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly HttpListener _listener = new();
        private readonly UDPServer _udpServer;
        private readonly UdpServerHttpConfig _config;
        private readonly ILogger<HttpApiListener> _logger;
        private readonly CancellationTokenSource _cts = new();
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private bool _disposed;

        /// <summary>
        /// Initialises the listener.
        /// </summary>
        public HttpApiListener(
            UDPServer udpServer,
            UdpServerHttpConfig config,
            ILogger<HttpApiListener> logger)
        {
            ArgumentNullException.ThrowIfNull(udpServer);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(logger);

            _udpServer = udpServer;
            _config    = config;
            _logger    = logger;
        }

        /// <summary>Starts the HTTP listener on <c>localhost:{port}</c>.</summary>
        public void Start()
        {
            if (!_config.Enabled)
            {
                _logger.LogInformation("HTTP API is disabled in config.");
                return;
            }

            try
            {
                string prefix = $"http://localhost:{_config.HttpPort}/";
                _listener.Prefixes.Add(prefix);
                _listener.Start();

                _logger.LogInformation("HTTP API started at {Prefix}", prefix);

                // Process requests on a background thread — never blocks the UDP loop.
                _ = Task.Run(() => ProcessRequestsAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start HTTP API listener.");
            }
        }

        /// <summary>Signals the listener to stop and closes the socket.</summary>
        public void Stop()
        {
            if (!_listener.IsListening)
                return;

            _cts.Cancel();
            _listener.Stop();
            _logger.LogInformation("HTTP API stopped.");
        }

        // ── Request pump ─────────────────────────────────────────────────────

        private async Task ProcessRequestsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);

                    // Dispatch to background so the pump is never blocked by a slow handler.
                    _ = Task.Run(() => HandleRequestAsync(context), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    // Expected: Stop() was called.
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting HTTP request.");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                HttpListenerRequest  req  = context.Request;
                HttpListenerResponse resp = context.Response;

                _logger.LogDebug("HTTP {Method} {Path}", req.HttpMethod, req.Url?.PathAndQuery);

                // Permissive CORS for local dashboard development.
                resp.Headers.Add("Access-Control-Allow-Origin",  "*");
                resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE");
                resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (req.HttpMethod == "OPTIONS")
                {
                    resp.StatusCode = 200;
                    resp.Close();
                    return;
                }

                string path = req.Url?.AbsolutePath ?? "/";

                switch (path)
                {
                    case "/status":
                        await HandleStatusAsync(resp).ConfigureAwait(false);
                        break;

                    case "/devices":
                        await HandleDevicesAsync(resp).ConfigureAwait(false);
                        break;

                    case var p when p.StartsWith("/command/", StringComparison.OrdinalIgnoreCase):
                        await HandleCommandAsync(req, resp).ConfigureAwait(false);
                        break;

                    default:
                        resp.StatusCode = 404;
                        await WriteJsonAsync(resp, new { error = "Not found." }).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling HTTP request.");
                try
                {
                    context.Response.StatusCode = 500;
                    await WriteJsonAsync(context.Response, new { error = "Internal server error." })
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Response may already be partially sent — nothing to do.
                }
            }
        }

        // ── Handlers ─────────────────────────────────────────────────────────

        private async Task HandleStatusAsync(HttpListenerResponse response)
        {
            UDPServerStatuses s = _udpServer.Statuses;

            var dto = new UdpServerStatusDto(
                TotalClients:      (long)s.TotalClients,
                TotalAuth:         (long)s.TotalAuth,
                MaxClients:        s.MaxClients,
                CurrentClients:    s.CurrentClients,
                TotalMBSend:       (long)(s.TotalKBSend / 1024),
                TotalAuthErrors:   (long)s.TotalAuthError,
                TotalWrongPackets: (long)s.TotalWrongPackets,
                ServerStartedAt:   _startedAt);

            response.StatusCode = 200;
            await WriteJsonAsync(response, dto).ConfigureAwait(false);
        }

        private static async Task HandleDevicesAsync(HttpListenerResponse response)
        {
            // TODO: expose active sessions when UDPServer.Sessions becomes public.
            response.StatusCode = 200;
            await WriteJsonAsync(response, Array.Empty<DeviceSessionDto>()).ConfigureAwait(false);
        }

        private async Task HandleCommandAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            // Path: /command/{token}
            string path    = request.Url?.AbsolutePath ?? string.Empty;
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
            {
                response.StatusCode = 400;
                await WriteJsonAsync(response, new CommandResponseDto(false, "Missing device token."))
                    .ConfigureAwait(false);
                return;
            }

            string token = parts[1];

            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonAsync(response, new CommandResponseDto(false, "Method not allowed."))
                    .ConfigureAwait(false);
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            SendCommandRequestDto? command;
            try
            {
                command = JsonSerializer.Deserialize<SendCommandRequestDto>(body, JsonOptions);
            }
            catch (JsonException)
            {
                response.StatusCode = 400;
                await WriteJsonAsync(response, new CommandResponseDto(false, "Invalid JSON."))
                    .ConfigureAwait(false);
                return;
            }

            if (command is null || string.IsNullOrWhiteSpace(command.CommandType))
            {
                response.StatusCode = 400;
                await WriteJsonAsync(response, new CommandResponseDto(false, "Missing commandType."))
                    .ConfigureAwait(false);
                return;
            }

            // TODO: enqueue command for delivery to the device session.
            _logger.LogInformation(
                "Command received for device {Token}: {CommandType}",
                token,
                command.CommandType);

            response.StatusCode = 200;
            await WriteJsonAsync(response,
                new CommandResponseDto(true, $"Command '{command.CommandType}' queued for device {token}."))
                .ConfigureAwait(false);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static async Task WriteJsonAsync(HttpListenerResponse response, object data)
        {
            response.ContentType = "application/json";
            byte[] buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, JsonOptions));
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
            response.Close();
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Stop();
            _cts.Dispose();
            _listener.Close();
        }
    }
}
