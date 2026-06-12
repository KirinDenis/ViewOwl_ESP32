using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ViewOwl.Config;
using ViewOwl.UDP.Utils;

namespace ViewOwl.UDP.Server
{
    /// <summary>
    /// UDP Server working-process monitoring counters (thread-safe).
    /// </summary>
    internal sealed class UDPServerStatuses
    {
        private ulong _totalClients;
        private ulong _totalAuth;
        private int   _maxClients;
        private int   _currentClients;
        private ulong _totalAuthError;
        private ulong _totalKBSend;
        private ulong _totalWrongPackets;

        public ulong TotalClients     => (ulong)Interlocked.Read(ref Unsafe.As<ulong, long>(ref _totalClients));
        public ulong TotalAuth        => (ulong)Interlocked.Read(ref Unsafe.As<ulong, long>(ref _totalAuth));
        public int   MaxClients       => Interlocked.CompareExchange(ref _maxClients, 0, 0);
        public int   CurrentClients   => Interlocked.CompareExchange(ref _currentClients, 0, 0);
        public ulong TotalAuthError   => (ulong)Interlocked.Read(ref Unsafe.As<ulong, long>(ref _totalAuthError));
        public ulong TotalKBSend      => (ulong)Interlocked.Read(ref Unsafe.As<ulong, long>(ref _totalKBSend));
        public ulong TotalWrongPackets => (ulong)Interlocked.Read(ref Unsafe.As<ulong, long>(ref _totalWrongPackets));

        public void IncrementTotalClients()     => Interlocked.Increment(ref _totalClients);
        public void IncrementTotalAuth()        => Interlocked.Increment(ref _totalAuth);
        public void SetMaxClients(int value)    => Interlocked.Exchange(ref _maxClients, value);
        public void SetCurrentClients(int v)    => Interlocked.Exchange(ref _currentClients, v);
        public void DecrementCurrentClients()   => Interlocked.Decrement(ref _currentClients);
        public void IncrementTotalAuthError()   => Interlocked.Increment(ref _totalAuthError);
        public void AddKBSent(ulong kb)         => Interlocked.Add(ref _totalKBSend, kb);
        public void IncrementWrongPackets()     => Interlocked.Increment(ref _totalWrongPackets);
    }

    /// <summary>Event data for status / join / done notifications.</summary>
    internal sealed class StatusChangedEventArgs : EventArgs
    {
        /// <summary>Human-readable status message.</summary>
        public string Message { get; }

        /// <summary>Remote endpoint involved in the event, if any.</summary>
        public IPEndPoint? EndPoint { get; }

        /// <summary>Initialises the event args.</summary>
        public StatusChangedEventArgs(string message, IPEndPoint? endPoint = null)
        {
            Message  = message;
            EndPoint = endPoint;
        }
    }

    /// <summary>
    /// UDP Server — listens on a single socket, authorises devices via the WebAPI,
    /// streams BGR565 frames in DATA packets, and records per-device pings.
    /// </summary>
    /// <remarks>
    /// Single-reader design: <c>socket.ReceiveFrom()</c> is synchronous; each authorised
    /// device gets its own <see cref="UDPServerSession"/> background thread for sending.
    /// NOT_MODIFIED optimisation: if the device CRC matches the manifest batch CRC, the
    /// server skips retransmission entirely.
    /// See <see href="../docs/architecture.md">docs/architecture.md</see> for the full
    /// protocol description and <see href="../docs/hardware.md">docs/hardware.md</see>
    /// for packet size rationale.
    /// </remarks>
    internal sealed class UDPServer : IDisposable
    {
        // ── Dependencies ──────────────────────────────────────────────────────

        private readonly ILogger<UDPServer> _logger;
        private readonly WebApiClient _webApiClient;
        private readonly SharedConfig _sharedConfig;

        // ── Networking ────────────────────────────────────────────────────────

        private readonly IPEndPoint _bind;
        private readonly Socket _socket;
        private readonly byte[] _recvBuf = new byte[UDPServerConfig.PacketSize];
        private EndPoint _remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        private readonly ConcurrentDictionary<IPEndPoint, UDPServerSession> _sessions = new();
        private UInt16 _sessionsCount;

        /// <summary>
        /// Maps remote endpoints to their permanent device tokens even when no active session
        /// exists — populated on AUTH and used to route out-of-session PING heartbeats.
        /// </summary>
        private readonly ConcurrentDictionary<IPEndPoint, string> _knownDevices = new();

        // ── Batch CRC cache ───────────────────────────────────────────────────

        /// <summary>
        /// Maps device tokens to the CRC32 of the last successfully committed batch so that
        /// reconnecting devices can receive NOT_MODIFIED when nothing has changed, avoiding a
        /// full re-download every 5–10 seconds.
        /// CRC32 uses init=0, no finalXOR (matches <c>esp_rom_crc32_le(0, ...)</c>).
        /// </summary>
        private readonly ConcurrentDictionary<string, uint> _deviceBatchCrc = new();

        // ── Frame cache ───────────────────────────────────────────────────────

        /// <summary>
        /// Per-token cache of <see cref="FrameResult"/> from <c>GET /api/internal/device/{token}/frame</c>.
        /// Avoids a loopback HTTP round-trip on every HELLO during the normal CRC-dedup reconnect cycle.
        /// Success entries live for <see cref="FrameCacheSuccessTtl"/>; failures expire faster so a newly
        /// assigned template is picked up without waiting the full window.
        /// </summary>
        private readonly ConcurrentDictionary<string, (FrameResult Result, DateTime Expiry)> _frameCache = new();

        // Short TTL so template reassignments show up within 30 s even without a push trigger.
        private static readonly TimeSpan FrameCacheSuccessTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FrameCacheFailureTtl = TimeSpan.FromSeconds(30);

        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        // ── Statistics ────────────────────────────────────────────────────────

        private readonly UDPServerStatuses _statuses = new();

        /// <summary>Live server statistics (thread-safe reads).</summary>
        public UDPServerStatuses Statuses => _statuses;

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired when the server starts, stops, or hits a fatal socket error.</summary>
        public event EventHandler<StatusChangedEventArgs>? StatusChanged;

        /// <summary>Fired when a device is authorised and a session starts.</summary>
        public event EventHandler<StatusChangedEventArgs>? Join;

        /// <summary>Fired when a session ends (DONE or timeout).</summary>
        public event EventHandler<StatusChangedEventArgs>? Done;

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Initialises the UDP server.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="bind">Local endpoint to bind to.</param>
        /// <param name="webApiClient">Internal WebAPI client for device resolution and ping recording.</param>
        /// <param name="sharedConfig">Shared configuration — provides firmware compatibility bounds.</param>
        public UDPServer(
            ILogger<UDPServer> logger,
            IPEndPoint bind,
            WebApiClient webApiClient,
            SharedConfig sharedConfig)
        {
            _logger        = logger;
            _bind          = bind;
            _webApiClient  = webApiClient;
            _sharedConfig  = sharedConfig;
            _disposed      = false;

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // SIO_UDP_CONNRESET suppresses ICMP Port Unreachable errors on Windows
            if (OperatingSystem.IsWindows())
            {
                _socket.IOControl(
                    (IOControlCode)UDPServerConfig.SIO_UDP_CONNRESET,
                    new byte[] { 0 }, null);
            }

            _socket.SetSocketOption(SocketOptionLevel.IP,     SocketOptionName.DontFragment,  true);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 1000);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer,     UDPServerConfig.PacketSize);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer,  UDPServerConfig.PacketSize);
        }

        // ── Public control ────────────────────────────────────────────────────

        /// <summary>Binds the socket and starts the listen loop on a background thread.</summary>
        public bool Start()
        {
            try
            {
                _socket.Bind(_bind);

                Thread thread = new Thread(() => ListenLoop(_cts.Token)) { IsBackground = true };
                thread.Start();

                Thread pushThread = new Thread(() => PushLoop(_cts.Token)) { IsBackground = true };
                pushThread.Start();

                StatusChangedInvoke($"Server started at {_bind.Address}:{_bind.Port}");
                return true;
            }
            catch (Exception ex)
            {
                StatusChangedInvoke($"Cannot start server at {_bind.Address}:{_bind.Port} — {ex.Message}");
                return false;
            }
        }

        /// <summary>Signals the listen loop to stop and shuts down the socket.</summary>
        public void Stop()
        {
            _cts.Cancel();

            // UDP sockets on Linux do not support Shutdown — calling it throws SocketError 107
            // (ENOTCONN). Closing the socket is sufficient to unblock ReceiveFrom.
            try
            {
                _socket.Close();
            }
            catch (SocketException)
            {
                // Ignore — socket may already be in a closed state.
            }

            StatusChangedInvoke("Server shutdown");
        }

        // ── Push loop ─────────────────────────────────────────────────────────

        /// <summary>
        /// Background thread that proactively pushes an AUTH trigger to idle devices
        /// when a new frame is ready — without waiting for the device to send a PING.
        /// Polls every 500 ms; skips devices that have an active transfer session.
        /// </summary>
        private void PushLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(500);

                foreach (var (endpoint, token) in _knownDevices)
                {
                    // Skip devices currently receiving a frame — they will pick up the
                    // new file on the next HELLO after DONE.
                    if (_sessions.ContainsKey(endpoint))
                        continue;

                    try
                    {
                        var (_, shouldRefresh) = _webApiClient.ConsumeRestart(token);
                        if (!shouldRefresh)
                            continue;

                        _logger.LogInformation(
                            "PUSH: new frame available — sending AUTH trigger to {Endpoint} ({Token})",
                            endpoint, token);

                        byte[] trigger = PacketHelper.CreateAuthTriggerPacket();
                        _socket.SendTo(trigger, endpoint);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "PushLoop: failed to check/push for {Token}", token);
                    }
                }
            }
        }

        // ── Listen loop ───────────────────────────────────────────────────────

        private void ListenLoop(CancellationToken cancellationToken)
        {
            if (_socket == null) return;

            while (!cancellationToken.IsCancellationRequested)
            {
                int recvLen = SafeReceive();
                if (recvLen <= 0) continue;

                IPEndPoint remoteIPEndpoint = (IPEndPoint)_remoteEndPoint;

                byte[] recvPacket = new byte[recvLen];
                Buffer.BlockCopy(_recvBuf, 0, recvPacket, 0, recvLen);

                // Intercept PING packets before session lookup so they are handled both
                // during active transfers and during the 5-minute idle period when no session
                // exists in _sessions.
                PacketHeader incomingHeader = PacketHelper.ReadHeader(recvPacket);
                if (incomingHeader.PacketType == (byte)PacketTypes.PING)
                {
                    HandlePing(remoteIPEndpoint, recvPacket, incomingHeader);
                    continue;
                }

                if (_sessions.TryGetValue(remoteIPEndpoint, out var session))
                {
                    if (incomingHeader.PacketType == (byte)PacketTypes.HELLO)
                    {
                        // New HELLO from an endpoint that already has an active session.
                        // The device re-sent HELLO because its idle timer expired while a
                        // transfer was still in flight.  Abort the old session so its sender
                        // thread exits, then fall through to start a fresh one below.
                        _logger.LogInformation(
                            "HELLO from {Endpoint} while session active — aborting old session",
                            remoteIPEndpoint);
                        session.RequestAbort();
                        _sessions.TryRemove(remoteIPEndpoint, out _);
                        // fall through to Auth() below — no continue
                    }
                    else
                    {
                        // Route other packets to the active session's sender thread.
                        session.OnPacketFromRemote(recvPacket);
                        continue;
                    }
                }

                // New client (or re-HELLO after session abort) — attempt HELLO auth
                _statuses.IncrementTotalClients();

                if (Auth(remoteIPEndpoint, recvPacket, out session) && session != null)
                {
                    _sessions[remoteIPEndpoint] = session;
                    _statuses.SetCurrentClients(_sessions.Count);

                    if (_statuses.CurrentClients > _statuses.MaxClients)
                        _statuses.SetMaxClients(_statuses.CurrentClients);

                    _statuses.IncrementTotalAuth();

                    // For batch sessions Auth() already called StartBatchSender() which
                    // sent AUTH and started the background thread — do NOT call StartSender().
                    if (!session.IsBatchSession)
                        session.StartSender(_socket);

                    // Notify dashboard that a transfer has started (AUTH sent, DATA loop running).
                    // Skip when the device already had the frame (NOT_MODIFIED), file was
                    // unreadable, or this is a batch session (size reported differently).
                    if (!session.SessionNotModified && !session.IsBatchSession && session.FileSize > 0)
                    {
                        string tok4Transfer  = session.DeviceToken;
                        int    tpl4Transfer  = session.TemplateId;
                        int    sz4Transfer   = session.FileSize;
                        int?   flag4Transfer = session.FileFlagByte >= 0 ? session.FileFlagByte : null;
                        int    dev4Transfer  = session.DeviceId;
                        string sid4Transfer  = session.SessionIdStr;
                        Task.Run(() => _webApiClient.NotifyTransferStarted(
                            tok4Transfer, tpl4Transfer, sz4Transfer, flag4Transfer,
                            deviceId: dev4Transfer, sessionId: sid4Transfer));
                    }

                    JoinInvoke(remoteIPEndpoint);
                }
            }
        }

        /// <summary>
        /// Validates a HELLO packet and resolves the device via WebAPI.
        /// </summary>
        /// <remarks>
        /// The HELLO payload is the permanent <c>Device.Token</c> stored in the database.
        /// <c>WebApiClient.GetFrame</c> resolves it to the .bin path of the active template's
        /// last successful grab.
        /// </remarks>
        private bool Auth(
            IPEndPoint remoteIPEndpoint,
            byte[] recvPacket,
            out UDPServerSession? session)
        {
            PacketHeader header = PacketHelper.ReadHeader(recvPacket);

            if (header.PacketType != (byte)PacketTypes.HELLO)
            {
                session = null;
                return false;
            }

            ReadOnlySpan<byte> payload = PacketHelper.ReadPayload(recvPacket);

            // Support both legacy (GUID string) and extended (struct) HELLO formats.
            var (success, token, extendedData, macAddress) = HelloPacketHelper.ParseHello(payload);

            // Firmware compatibility check — MAJOR must match exactly, MINOR must match exactly.
            // PATCH differences are fine (bug-fix releases on either side).
            // Log a warning but still serve the device; hard rejection would silently brick
            // devices in the field during a partial upgrade.
            if (extendedData.HasValue)
            {
                byte fwMaj = extendedData.Value.FirmwareVersionMajor;
                byte fwMin = extendedData.Value.FirmwareVersionMinor;
                byte fwPat = extendedData.Value.FirmwareVersionPatch;

                if (fwMaj != _sharedConfig.MinFirmwareMajor || fwMin != _sharedConfig.MinFirmwareMinor)
                {
                    _logger.LogWarning(
                        "Firmware version mismatch: device reports {FwMaj}.{FwMin}.{FwPat}, " +
                        "server expects {ExpMaj}.{ExpMin}.x — upgrade firmware to avoid protocol issues. " +
                        "Recommended firmware: {Recommended}",
                        fwMaj, fwMin, fwPat,
                        _sharedConfig.MinFirmwareMajor, _sharedConfig.MinFirmwareMinor,
                        _sharedConfig.RecommendedFirmware);
                }
            }

            if (!success)
            {
                _logger.LogWarning(
                    "Auth rejected from {Endpoint} — could not parse HELLO token.",
                    remoteIPEndpoint);
                _statuses.IncrementTotalAuthError();

                // Tell the device its token is unreadable so it can log a meaningful error.
                _socket.SendTo(PacketHelper.CreateErrorPacket((byte)ErrorCodes.BadToken), remoteIPEndpoint);

                // Notify the dashboard — fire-and-forget.
                IPEndPoint ep4Knock = remoteIPEndpoint;
                Task.Run(() => _webApiClient.NotifyKnocking(ep4Knock, "?", "bad-token"));

                session = null;
                return false;
            }

            // Token is stored in the DB as a 32-char "N" format GUID string.
            string deviceToken = token.ToString("N");

            // Resolve device → active template → last successful grab → file path.
            // GetFrameCached avoids a loopback HTTP round-trip when the frame has not changed.
            FrameResult frameResult = GetFrameCached(deviceToken);

            if (!frameResult.Success)
            {
                _logger.LogWarning(
                    "Auth rejected for token '{Token}' from {Endpoint} — rejectCode={Code}.",
                    deviceToken, remoteIPEndpoint, frameResult.RejectCode);
                _statuses.IncrementTotalAuthError();

                // Forward the exact rejection reason so the device can log a meaningful message.
                _socket.SendTo(PacketHelper.CreateErrorPacket(frameResult.RejectCode), remoteIPEndpoint);

                // Map the numeric reject code to the reason string expected by KnockingPanel.
                string knockReason = frameResult.RejectCode switch
                {
                    (byte)ErrorCodes.UnknownDevice => "no-device",
                    (byte)ErrorCodes.NoTemplate    => "no-template",
                    _                              => "no-frame",
                };

                string tok4Knock = deviceToken;
                IPEndPoint ep4Knock2 = remoteIPEndpoint;
                Task.Run(() => _webApiClient.NotifyKnocking(ep4Knock2, tok4Knock, knockReason));

                session = null;
                return false;
            }

            FrameInfo frame = frameResult.Frame!;

            _logger.LogInformation(
                "Auth OK: device {DeviceId}, template {TemplateId}, file '{File}', from {Endpoint}, extended={HasExtended}",
                frame.DeviceId, frame.TemplateId, frame.FilePath, remoteIPEndpoint, extendedData.HasValue);

            // Register endpoint → token so out-of-session PINGs can be routed correctly.
            _knownDevices[remoteIPEndpoint] = deviceToken;

            // header.Seq carries the CRC32 of the last frame the device rendered.
            // Value 0 means the device has no frame yet — always send the full file.
            var newSession = new UDPServerSession(
                ++_sessionsCount,
                remoteIPEndpoint,
                deviceToken,
                frame.DeviceId,
                frame.TemplateId,
                frame.FilePath,
                clientFrameCrc: header.Seq);

            newSession.OnSessionEnd  = OnSessionEnd;
            newSession.OnSendFile    = OnSendFile;
            newSession.OnLostPacket  = OnLostPacket;

            // Wire up live progress notifications — capture token + template for the closure.
            string progressToken    = deviceToken;
            int    progressTemplate = frame.TemplateId;
            newSession.OnProgress = (chunksSent, totalChunks, elapsedMs, retries) =>
                Task.Run(() => _webApiClient.NotifyTransferProgress(
                    progressToken, chunksSent, totalChunks, elapsedMs, retries));

            // Notify WebAPI about the new connection with device capabilities.
            // Fire-and-forget — must not block the receive loop.
            string token4Capture        = deviceToken;
            HelloPacketPayload? ext4Capture = extendedData;
            string? mac4Capture         = macAddress;
            uint    crc4Capture         = header.Seq;
            Task.Run(() => _webApiClient.NotifyHello(token4Capture, remoteIPEndpoint, ext4Capture, mac4Capture, crc4Capture));

            // Class-C templates carry a batch manifest path.  Use the BATCH protocol so the
            // device stores all frames in its flash partition and plays them independently.
            if (!string.IsNullOrEmpty(frame.BatchManifestPath))
            {
                UdpBatchManifest? manifest = ReadBatchManifest(frame.BatchManifestPath);
                if (manifest is not null && manifest.FramePaths.Length > 0)
                {
                    // Short-circuit when the device already has the current batch.
                    // Fast path: compare clientFrameCrc against the in-memory cache that is
                    // populated after each successful BATCH_COMMIT.  Survives as long as the
                    // server process is running.
                    uint clientBatchCrc = header.Seq;

                    // Extract the cached CRC once so the diagnostic log and the if-condition
                    // both see exactly the same values (avoids a second concurrent read).
                    bool hasCachedCrc = _deviceBatchCrc.TryGetValue(deviceToken, out uint lastCommittedCrc);

                    // Diagnostic log — always emitted so we can trace NOT_MODIFIED decisions
                    // in production logs without needing a special debug build.
                    _logger.LogInformation(
                        "Auth: Class-C CRC check — clientCrc=0x{Client:X8} " +
                        "cacheHit={CacheHit} cachedCrc=0x{Cached:X8} manifestCrc=0x{Manifest:X8}",
                        clientBatchCrc, hasCachedCrc, lastCommittedCrc, manifest.BatchCrc);

                    if (clientBatchCrc != 0 && hasCachedCrc && clientBatchCrc == lastCommittedCrc)
                    {
                        _logger.LogInformation(
                            "Auth: batch CRC 0x{Crc:X8} matches in-memory cache — NOT_MODIFIED to {Endpoint}",
                            clientBatchCrc, remoteIPEndpoint);

                        newSession.StartBatchNotModified(_socket);
                        session = newSession;
                        return true;
                    }

                    // Slow path: compare against the CRC stored in the manifest JSON.
                    // This survives server restarts: the device boots with NVS-persisted CRC,
                    // sends it in HELLO, and the server confirms via the manifest without
                    // triggering a full 27-second BATCH re-download.
                    // Only manifests with CrcVersion >= 2 carry the standard CRC32 variant
                    // that the firmware actually computes; older ones are ignored here.
                    if (clientBatchCrc != 0
                        && manifest.BatchCrc != 0
                        && manifest.CrcVersion >= 2
                        && clientBatchCrc == manifest.BatchCrc)
                    {
                        _logger.LogInformation(
                            "Auth: batch CRC 0x{Crc:X8} matches manifest — NOT_MODIFIED to {Endpoint} (cache restored)",
                            clientBatchCrc, remoteIPEndpoint);

                        // Restore the in-memory cache so subsequent HELLOs hit the fast path.
                        _deviceBatchCrc[deviceToken] = manifest.BatchCrc;

                        newSession.StartBatchNotModified(_socket);
                        session = newSession;
                        return true;
                    }

                    _logger.LogInformation(
                        "Auth: Class-C batch — {FrameCount} frames, {Fps} fps, manifest='{Path}'",
                        manifest.FrameCount, manifest.Fps, frame.BatchManifestPath);

                    newSession.StartBatchSender(_socket, manifest.FramePaths, manifest.Fps);
                    session = newSession;
                    return true;
                }

                _logger.LogWarning(
                    "Auth: batch manifest '{Path}' unreadable — falling back to single-frame send",
                    frame.BatchManifestPath);
            }

            // Regular single-frame transfer.
            session = newSession;
            return true;
        }

        /// <summary>
        /// Reads and deserialises a batch manifest JSON file written by
        /// <c>DeviceTemplateRefreshWorker</c>.  Returns <c>null</c> on any read or parse error.
        /// </summary>
        private UdpBatchManifest? ReadBatchManifest(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<UdpBatchManifest>(json, UdpBatchManifest.JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReadBatchManifest: failed to read '{Path}'", path);
                return null;
            }
        }

        // ── PING handler ──────────────────────────────────────────────────────

        /// <summary>
        /// Handles an incoming PING packet from any known or unknown endpoint.
        /// Sends an ACK immediately, then notifies the WebAPI asynchronously.
        /// Works both during active sessions and during the idle period when no session exists.
        /// </summary>
        private void HandlePing(IPEndPoint remote, byte[] recvPacket, PacketHeader header)
        {
            // Resolve the device token — prefer the active session, fall back to _knownDevices.
            string? token = null;
            if (_sessions.TryGetValue(remote, out var activeSession))
                token = activeSession.DeviceToken;
            else
                _knownDevices.TryGetValue(remote, out token);

            if (token is null)
            {
                _logger.LogDebug(
                    "PING from unknown endpoint {Endpoint} — no matching device; ignoring.", remote);
                return;
            }

            ReadOnlySpan<byte> pingPayload = PacketHelper.ReadPayload(recvPacket);
            if (pingPayload.Length < Marshal.SizeOf<PingPacketPayload>())
            {
                _logger.LogWarning(
                    "PING from {Endpoint} has undersized payload ({Len} bytes); ignoring.",
                    remote, pingPayload.Length);
                return;
            }

            PingPacketPayload pingData = PacketHelper.ReadStruct<PingPacketPayload>(pingPayload);

            // Send ACK immediately on the hot path — never block on I/O here.
            byte[] ack = PacketHelper.CreatePingAck(header.SessionId);
            _socket.SendTo(ack, remote);

            // Notify WebAPI and check for pending restart asynchronously — must not block the receive loop.
            string tokenCapture  = token;
            IPEndPoint epCapture = remote;
            UInt16 sessionId     = header.SessionId;
            Task.Run(() =>
            {
                try
                {
                    _webApiClient.NotifyHeartbeat(tokenCapture, epCapture, pingData);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PING heartbeat notification failed for {Token}", tokenCapture);
                }

                // Check whether a restart command or a new frame is pending for this device.
                try
                {
                    var (shouldRestart, shouldRefresh) = _webApiClient.ConsumeRestart(tokenCapture);

                    if (shouldRestart)
                    {
                        _logger.LogInformation(
                            "Sending CONFIG FLAG_RESTART to {Endpoint} (token={Token})",
                            epCapture, tokenCapture);

                        byte[] cfg = PacketHelper.CreateConfigPacket(
                            sessionId,
                            new ConfigPacketPayload { PingInterval = 0, Reserved1 = 0, Reserved2 = 0, Reserved3 = 0 },
                            flags: (UInt16)PacketStatuses.RESTART);

                        _socket.SendTo(cfg, epCapture);
                    }
                    else if (shouldRefresh)
                    {
                        // A new frame was grabbed since the device last connected.
                        // Evict the cache so the next HELLO fetches the updated path.
                        _frameCache.TryRemove(tokenCapture, out _);

                        // Send an AUTH trigger so the device breaks out of its idle loop
                        // and immediately sends a new HELLO to start a fresh transfer.
                        _logger.LogInformation(
                            "Sending AUTH trigger (frame refresh) to {Endpoint} (token={Token})",
                            epCapture, tokenCapture);

                        byte[] authTrigger = PacketHelper.CreateAuthTriggerPacket();
                        _socket.SendTo(authTrigger, epCapture);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ConsumeRestart check failed for {Token}", tokenCapture);
                }
            });
        }

        // ── Session callbacks ─────────────────────────────────────────────────

        private void OnSessionEnd(UDPServerSession session)
        {
            if (_sessions.TryRemove(session.RemoteIPEndPoint, out var removed))
            {
                // Persist the batch CRC so the next HELLO from this device can short-circuit
                // when the frames have not changed since the last successful commit.
                if (session.IsBatchSession && session.SessionSucceeded && session.BatchCrc != 0)
                    _deviceBatchCrc[session.DeviceToken] = session.BatchCrc;

                // Record ping asynchronously — must not block the session thread.
                bool   succeeded    = session.SessionSucceeded;
                bool   notModified  = session.SessionNotModified;
                string token        = session.DeviceToken;
                int    deviceId     = session.DeviceId;
                int    templateId   = session.TemplateId;
                int    fileSize     = session.FileSize;
                long   durationMs   = session.TransferDurationMs;
                int    totalRetries = session.TotalRetries;
                string sessionIdStr = session.SessionIdStr;
                Task.Run(() => _webApiClient.RecordPing(token, succeeded || notModified));

                // Compute outcome and throughput for the dashboard notification.
                string outcome = notModified ? "not_modified" : succeeded ? "success" : "fail";
                int?   throughputKBps = (succeeded && durationMs > 0 && fileSize > 0)
                    ? (int?)((fileSize / 1024.0) / (durationMs / 1000.0))
                    : null;

                // Always report completion (including not_modified) so the dashboard can update.
                int size4Report = notModified ? 0 : fileSize;
                Task.Run(() => _webApiClient.NotifyGrabComplete(
                    token, templateId, succeeded || notModified,
                    size4Report, durationMs,
                    succeeded ? null : (notModified ? null : "ack-timeout"),
                    outcome, totalRetries, throughputKBps,
                    deviceId: deviceId, sessionId: sessionIdStr));

                removed.Dispose();
                DoneInvoke(session.RemoteIPEndPoint);
                _statuses.SetCurrentClients(_sessions.Count);
            }
            else
            {
                _logger.LogError(
                    "Failed to remove session for {Endpoint}", session.RemoteIPEndPoint);
            }
        }

        private void OnSendFile(ulong sizeKb)
        {
            _statuses.AddKBSent(sizeKb);
        }

        private void OnLostPacket(IPEndPoint _)
        {
            _statuses.IncrementWrongPackets();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a cached <see cref="FrameResult"/> for <paramref name="token"/>, falling back
        /// to a live <see cref="WebApiClient.GetFrame"/> call when the entry is absent or expired.
        /// Success results are cached for <see cref="FrameCacheSuccessTtl"/>; failures for
        /// <see cref="FrameCacheFailureTtl"/> to allow a recently-assigned template to surface quickly.
        /// </summary>
        private FrameResult GetFrameCached(string token)
        {
            if (_frameCache.TryGetValue(token, out var entry) && DateTime.UtcNow < entry.Expiry)
                return entry.Result;

            FrameResult fresh = _webApiClient.GetFrame(token);
            TimeSpan ttl = fresh.Success ? FrameCacheSuccessTtl : FrameCacheFailureTtl;
            _frameCache[token] = (fresh, DateTime.UtcNow + ttl);
            return fresh;
        }

        private int SafeReceive()
        {
            try
            {
                return _socket != null
                    ? _socket.ReceiveFrom(_recvBuf, ref _remoteEndPoint)
                    : 0;
            }
            catch (SocketException se)
                when (se.ErrorCode is UDPServerConfig.WSAETIMEDOUT or UDPServerConfig.WSAEINTR)
            {
                // Normal timeout or interrupt — not an error.
                return 0;
            }
            catch (SocketException se)
            {
                StatusChangedInvoke($"Socket exception {se.ErrorCode} — {se.Message}");
                return 0;
            }
            catch (Exception ex)
            {
                StatusChangedInvoke($"Unexpected receive error — {ex.Message}");
                return 0;
            }
        }

        private void StatusChangedInvoke(string message)
        {
            StatusChanged?.Invoke(this, new StatusChangedEventArgs(message));
            _logger.LogInformation("{Message}", message);
        }

        private void JoinInvoke(IPEndPoint ep)
            => Join?.Invoke(this, new StatusChangedEventArgs("JOIN", ep));

        private void DoneInvoke(IPEndPoint ep)
            => Done?.Invoke(this, new StatusChangedEventArgs("DONE", ep));

        // ── Dispose ───────────────────────────────────────────────────────────

        /// <summary>Stops the server and releases all resources.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _socket.Dispose();
            _cts.Dispose();

            foreach (UDPServerSession s in _sessions.Values)
                s.Dispose();

            _sessions.Clear();
            _knownDevices.Clear();
            _frameCache.Clear();
            _deviceBatchCrc.Clear();

            StatusChanged = null;
            Join          = null;
            Done          = null;

            GC.SuppressFinalize(this);
        }
    }

    // ── Batch manifest ────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the JSON written by <c>DeviceTemplateRefreshWorker.BatchManifest</c>.
    /// Deserialized by <see cref="UDPServer.ReadBatchManifest"/> so the server can call
    /// <see cref="UDPServerSession.StartBatchSender"/> for Class-C templates.
    /// </summary>
    internal sealed class UdpBatchManifest
    {
        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>Total number of animation frames.</summary>
        [JsonPropertyName("frameCount")]
        public int FrameCount { get; set; }

        /// <summary>Playback speed in frames per second.</summary>
        [JsonPropertyName("fps")]
        public byte Fps { get; set; }

        /// <summary>Absolute paths to the compressed frame bin files, in frame order.</summary>
        [JsonPropertyName("framePaths")]
        public string[] FramePaths { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Standard CRC32 (zlib variant) computed over the concatenated bytes of all frame
        /// files in transmission order — identical to the value accumulated by the ESP32
        /// firmware via <c>esp_rom_crc32_le</c> across every DATA chunk it receives.
        /// Used by <see cref="UDPServer"/> to issue NOT_MODIFIED after a server restart without
        /// re-reading all frame files.  0 in manifests written before this field was added —
        /// treated as "unknown" and falls back to a full BATCH transfer.
        /// </summary>
        [JsonPropertyName("batchCrc")]
        public uint BatchCrc { get; set; }

        /// <summary>
        /// CRC algorithm version.  0/1 = legacy init=0/no-finalXOR variant that never matches
        /// the firmware value; 2 = standard CRC32.  The NOT_MODIFIED slow path only trusts
        /// manifests with version 2 or higher; older manifests fall back to a full BATCH
        /// transfer until the refresh worker rewrites them.
        /// </summary>
        [JsonPropertyName("crcVersion")]
        public int CrcVersion { get; set; }
    }
}
