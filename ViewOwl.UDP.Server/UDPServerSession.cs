using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ViewOwl.Config;
using ViewOwl.UDP.Utils;

namespace ViewOwl.UDP.Server
{
    /// <summary>
    /// Handles a single device session: sends AUTH, streams the frame in DATA chunks,
    /// waits for ACK on each chunk, then sends DONE. Runs on a dedicated background thread.
    /// </summary>
    internal sealed class UDPServerSession : IDisposable
    {
        // ── Callbacks set by UDPServer ────────────────────────────────────────

        /// <summary>Invoked when the session loop exits (success or failure).</summary>
        internal Action<UDPServerSession>? OnSessionEnd { get; set; }

        /// <summary>Invoked with the number of KB sent when DONE is transmitted.</summary>
        internal Action<ulong>? OnSendFile { get; set; }

        /// <summary>Invoked when a packet is lost (ACK timeout).</summary>
        internal Action<IPEndPoint>? OnLostPacket { get; set; }

        /// <summary>
        /// Invoked every <see cref="ProgressIntervalChunks"/> ACKed chunks with
        /// (chunksSent, totalChunks, elapsedMs, retries) for live dashboard progress.
        /// </summary>
        internal Action<int, int, int, int>? OnProgress { get; set; }

        /// <summary>Chunks ACKed between each <see cref="OnProgress"/> callback.</summary>
        private const int ProgressIntervalChunks = 8;

        // ── Session identity ──────────────────────────────────────────────────

        private readonly UInt16  _sessionId;
        private readonly string  _filePath;
        private readonly uint    _clientFrameCrc;

        /// <summary>The permanent device token from the HELLO payload (used for ping reporting).</summary>
        public string DeviceToken { get; }

        /// <summary>DB device id resolved during HELLO auth (used for pipeline event logging).</summary>
        public int DeviceId { get; }

        /// <summary>Active template id resolved during HELLO auth.</summary>
        public int TemplateId { get; }

        /// <summary>
        /// Short string form of the internal session id (used for pipeline event correlation).
        /// </summary>
        public string SessionIdStr => _sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Size of the frame file in bytes (available after <see cref="StartSender"/> returns).</summary>
        public int FileSize => _fileData?.Length ?? 0;

        /// <summary>
        /// Wall-clock duration of the DATA transfer loop in milliseconds.
        /// Set before <see cref="OnSessionEnd"/> is invoked; 0 when no transfer occurred.
        /// </summary>
        public long TransferDurationMs { get; private set; }

        /// <summary>
        /// Total retransmit count across the entire session loop.
        /// Set before <see cref="OnSessionEnd"/> is invoked.
        /// </summary>
        public int TotalRetries { get; private set; }

        /// <summary>
        /// First byte of the frame file — encodes the compression format
        /// (0x00=Raw, 0x03=LZ4+palette, 0x04=Delta).
        /// Available after <see cref="StartSender"/> reads the file; -1 when unknown.
        /// </summary>
        public int FileFlagByte { get; private set; } = -1;

        /// <summary>Remote endpoint of the device.</summary>
        public IPEndPoint RemoteIPEndPoint { get; }

        /// <summary>
        /// <c>true</c> when the DONE packet was successfully transmitted (all chunks ACKed).
        /// Set before <see cref="OnSessionEnd"/> is invoked.
        /// </summary>
        public bool SessionSucceeded { get; private set; }

        /// <summary>
        /// <c>true</c> when the server determined the device already has the current frame
        /// and sent NOT_MODIFIED instead of streaming data.
        /// </summary>
        public bool SessionNotModified { get; private set; }

        /// <summary>
        /// <c>true</c> when this session was started via <see cref="StartBatchSender"/>
        /// (Class-C multi-frame flash transfer).  The caller in <see cref="UDPServer"/>
        /// must NOT also call <see cref="StartSender"/> when this is set.
        /// </summary>
        public bool IsBatchSession { get; private set; }

        /// <summary>
        /// CRC32 (init=0, no finalXOR, polynomial 0xEDB88320) computed over the concatenated
        /// bytes of all batch frames in transmission order — identical to what the device
        /// accumulates via <c>esp_rom_crc32_le</c> across every DATA chunk.
        /// Set to a non-zero value only when <see cref="SessionSucceeded"/> is <c>true</c>
        /// for a batch session; used by <see cref="UDPServer"/> for the NOT_MODIFIED check.
        /// </summary>
        public uint BatchCrc { get; private set; }

        // ── Internal state ────────────────────────────────────────────────────

        private Socket? _socket;
        private readonly Queue<byte[]> _recvPackets = new();
        private readonly AutoResetEvent _recvEvent = new(false);
        private byte[]? _fileData;

        // Batch-mode fields (Class-C multi-frame transfer).
        private string[]? _framePaths;
        private byte      _batchFps;

        /// <summary>
        /// Set by <see cref="RequestAbort"/> to signal the session loop to exit early.
        /// Volatile ensures the write from the listen thread is visible to the sender thread.
        /// </summary>
        private volatile bool _abortRequested;

        /// <summary>
        /// Initialises a new session.
        /// </summary>
        /// <param name="sessionId">Unique session id assigned by <see cref="UDPServer"/>.</param>
        /// <param name="remoteIPEndPoint">Client endpoint.</param>
        /// <param name="deviceToken">Permanent device token (from HELLO payload).</param>
        /// <param name="deviceId">DB device id (used for pipeline event logging).</param>
        /// <param name="templateId">Active template DB id.</param>
        /// <param name="filePath">Absolute path to the binary frame file to stream.</param>
        /// <param name="clientFrameCrc">CRC32 of the last frame the device rendered (from HELLO hdr.Seq). 0 = no frame yet.</param>
        public UDPServerSession(
            UInt16     sessionId,
            IPEndPoint remoteIPEndPoint,
            string     deviceToken,
            int        deviceId,
            int        templateId,
            string     filePath,
            uint       clientFrameCrc = 0)
        {
            _sessionId       = sessionId;
            RemoteIPEndPoint = remoteIPEndPoint;
            DeviceToken      = deviceToken;
            DeviceId         = deviceId;
            TemplateId       = templateId;
            _filePath        = filePath;
            _clientFrameCrc  = clientFrameCrc;
        }

        // ── Session lifecycle ─────────────────────────────────────────────────

        /// <summary>
        /// Reads the frame file, sends AUTH, and starts the session loop on a background thread.
        /// Returns immediately if the file cannot be read.
        /// </summary>
        /// <param name="socket">Shared server socket used for sending.</param>
        public void StartSender(Socket? socket)
        {
            _socket = socket;

            try
            {
                ReadFile();
            }
            catch
            {
                // File not found or unreadable — session cannot proceed.
                return;
            }

            // Capture the frame's compression flag byte (first byte of the file).
            if (_fileData is { Length: > 0 })
                FileFlagByte = _fileData[0];

            // When the device already has this frame (CRC match), skip the transfer.
            uint fileCrc    = ComputeFileCrc32();   // init=0, no finalXOR — intended to match esp_rom_crc32_le(0,...)
            uint fileStdCrc = ComputeStdCrc32();    // init=0xFFFFFFFF, finalXOR=0xFFFFFFFF — standard zlib variant
            byte fileFlag   = _fileData is { Length: > 0 } ? _fileData[0] : (byte)0xFF;
            Console.WriteLine(
                $"[Session {_sessionId}] clientCrc=0x{_clientFrameCrc:X8} fileCrc=0x{fileCrc:X8} fileStdCrc=0x{fileStdCrc:X8} fileSize={_fileData?.Length ?? 0} fileFlag=0x{fileFlag:X2} match={_clientFrameCrc != 0 && _clientFrameCrc == fileStdCrc}");

            if (_clientFrameCrc != 0 && _clientFrameCrc == fileStdCrc)
            {
                _socket?.SendTo(PacketHelper.CreateNotModifiedPacket(), RemoteIPEndPoint);
                SessionNotModified = true;
                OnSessionEnd?.Invoke(this);
                return;
            }

            // If the device has a known previous frame, look for a CRC-named delta file:
            //   {guid}.delta.{clientFrameCrc_HEX8}.bin
            // The CRC is encoded in the filename so the server can do a direct stat()
            // instead of reading every delta file to check its baseCrc header.
            // Multiple delta files coexist (up to DeltaFileKeepCount) so a device that is
            // several grab-cycles behind can still find its matching delta.
            if (_clientFrameCrc != 0 && _fileData is { Length: > 0 })
            {
                // Build the expected delta filename from the device's reported CRC.
                string deltaPath = _filePath.Replace(
                    ".bin",
                    $".delta.{_clientFrameCrc:X8}.bin",
                    StringComparison.Ordinal);

                if (File.Exists(deltaPath))
                {
                    try
                    {
                        byte[] deltaCandidate = File.ReadAllBytes(deltaPath);

                        // Sanity-check: must start with FRAME_FLAG_DELTA (0x04) and have a
                        // valid minimum header (flag + baseCrc + newCrc + regionCount = 11 B).
                        if (deltaCandidate.Length >= 11 && deltaCandidate[0] == 0x04)
                        {
                            // Delta header layout (little-endian):
                            //   [0]     flag      = 0x04
                            //   [1..4]  base_crc  = CRC of the frame the device currently has
                            //   [5..8]  new_crc   = CRC of the .bin the device will have after applying the delta
                            //   [9..10] region_count
                            //
                            // Guard: new_crc must match the CRC of the current .bin file.
                            // If they differ, the .bin was replaced (e.g. by a browser push) after
                            // Chrome generated this delta.  Sending a delta that transitions to a
                            // stale frame would make the device display wrong/mixed pixel content.
                            // Fall back to the full file in that case.
                            uint deltaNewCrc = BitConverter.ToUInt32(deltaCandidate, 5);
                            if (deltaNewCrc == fileCrc)
                            {
                                int fullFrameSize = _fileData.Length;
                                _fileData = deltaCandidate;
                                Console.WriteLine(
                                    $"[Session {_sessionId}] Sending delta " +
                                    $"({deltaCandidate.Length} B vs {fullFrameSize} B full) " +
                                    $"baseCrc=0x{_clientFrameCrc:X8} newCrc=0x{deltaNewCrc:X8}");
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"[Session {_sessionId}] Delta new_crc=0x{deltaNewCrc:X8} " +
                                    $"!= fileCrc=0x{fileCrc:X8} — .bin was replaced after delta was " +
                                    $"generated (browser push?); sending full frame instead.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Delta unreadable — fall through and send the full frame.
                        Console.WriteLine($"[Session {_sessionId}] Delta load failed: {ex.Message}");
                    }
                }
            }

            // Send AUTH to confirm device is recognised and start the data transfer.
            // Seq carries the total frame size in bytes so the ESP32 can decide whether
            // to use the flash partition path before the first DATA packet arrives.
            PacketHeader authHeader = new PacketHeader()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (int)PacketTypes.AUTH,
                SessionId     = _sessionId,
                Seq           = (uint)(_fileData?.Length ?? 0),
                PayloadLength = 0,
                Flags         = 0
            };

            Span<byte> authBuf = stackalloc byte[Marshal.SizeOf<PacketHeader>()];
            PacketHelper.WriteHeader(authBuf, ref authHeader);
            _socket?.SendTo(authBuf, RemoteIPEndPoint);

            Thread thread = new Thread(SessionLoop) { IsBackground = true };
            thread.Start();
        }

        /// <summary>
        /// Signals the session loop to stop at the next ACK wait boundary.
        /// Called by <see cref="UDPServer"/> when a new HELLO arrives from the same endpoint
        /// while a transfer is already in progress, so the old session can be cleanly replaced.
        /// </summary>
        public void RequestAbort()
        {
            _abortRequested = true;
            // Wake WaitAck() immediately so the sender thread does not block until timeout.
            _recvEvent.Set();
        }

        /// <summary>Enqueues a packet received from the remote device for processing by the session loop.</summary>
        public void OnPacketFromRemote(byte[] recvPacket)
        {
            lock (_recvPackets)
            {
                _recvPackets.Enqueue(recvPacket);
            }
            _recvEvent.Set();
        }

        // ── Private implementation ────────────────────────────────────────────

        private void ReadFile()
        {
            using FileStream fs = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _fileData = new byte[fs.Length];
            // ReadExactly ensures all bytes are read even when the OS returns partial reads
            fs.ReadExactly(_fileData, 0, _fileData.Length);
        }

        /// <summary>
        /// Computes CRC32 matching <c>esp_rom_crc32_le(0, buf, len)</c> on ESP32.
        /// Uses init=0 and no finalXOR — the <c>crc</c> parameter is passed as 0
        /// to <c>esp_rom_crc32_le</c>, so no initial or final XOR with 0xFFFFFFFF occurs.
        /// Polynomial: 0xEDB88320 (reflected IEEE 802.3).
        /// </summary>
        private uint ComputeFileCrc32()
        {
            if (_fileData is null) return 0;

            uint crc = 0; // init=0, matches esp_rom_crc32_le(0, ...)
            foreach (byte b in _fileData)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return crc; // no finalXOR
        }

        /// <summary>
        /// Computes standard CRC32/ISO-HDLC of <c>_fileData</c> with init=0xFFFFFFFF and
        /// finalXOR=0xFFFFFFFF — identical to the value ESP32 firmware sends in the HELLO
        /// Seq field. Used for the NOT_MODIFIED short-circuit comparison.
        /// </summary>
        private uint ComputeStdCrc32()
        {
            if (_fileData is null) return 0;

            uint crc = 0xFFFFFFFF;
            foreach (byte b in _fileData)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>
        /// Computes standard CRC32 (zlib variant: init=0xFFFFFFFF, polynomial 0xEDB88320,
        /// finalXOR=0xFFFFFFFF) over the concatenated bytes of all <paramref name="frames"/>
        /// in order.  This equals the value the firmware accumulates by chaining
        /// <c>esp_rom_crc32_le(running, buf, len)</c> from seed 0 over every DATA chunk:
        /// the ROM helper inverts the seed on entry and the result on exit, so the chain
        /// collapses to one standard CRC32 over the concatenation.  The previous
        /// init=0/no-finalXOR variant never matched the firmware value, which made the
        /// NOT_MODIFIED short-circuit dead (full batch re-download every idle cycle).
        /// </summary>
        private static uint ComputeCrc32OverFrames(byte[][] frames)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte[] frame in frames)
            {
                foreach (byte b in frame)
                {
                    crc ^= b;
                    for (int i = 0; i < 8; i++)
                        crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }
            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>
        /// Starts a Class-C batch transfer: sends AUTH → BATCH_START → all frame DATA chunks
        /// → BATCH_COMMIT.  After BATCH_COMMIT is ACKed the device starts looped playback from
        /// its flash partition without further server involvement.
        /// </summary>
        /// <param name="socket">Shared server socket.</param>
        /// <param name="framePaths">Absolute paths to the compressed frame .bin files, in order.</param>
        /// <param name="fps">Playback FPS to send in BATCH_START.</param>
        public void StartBatchSender(Socket? socket, string[] framePaths, byte fps)
        {
            if (framePaths is null || framePaths.Length == 0
                || framePaths.Length > BatchStartPayload.MaxFrames)
                return;

            _socket        = socket;
            _framePaths    = framePaths;
            _batchFps      = fps;
            IsBatchSession = true;

            // Send AUTH so the device enters the data-receive state.
            PacketHeader authHeader = new()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (int)PacketTypes.AUTH,
                SessionId     = _sessionId,
                Seq           = 0,
                PayloadLength = 0,
                Flags         = 0
            };
            Span<byte> authBuf = stackalloc byte[Marshal.SizeOf<PacketHeader>()];
            PacketHelper.WriteHeader(authBuf, ref authHeader);
            _socket?.SendTo(authBuf, RemoteIPEndPoint);

            Thread thread = new Thread(BatchSessionLoop) { IsBackground = true };
            thread.Start();
        }

        /// <summary>
        /// Handles a NOT_MODIFIED outcome for a batch session: sends the NOT_MODIFIED packet
        /// to the device and fires <see cref="OnSessionEnd"/> on a background thread so that
        /// the caller can register the session in its dictionary before the callback fires —
        /// mirroring the async pattern of <see cref="StartBatchSender"/>.
        /// </summary>
        /// <param name="socket">Shared server socket used for sending.</param>
        public void StartBatchNotModified(Socket? socket)
        {
            _socket            = socket;
            IsBatchSession     = true;
            SessionNotModified = true;

            socket?.SendTo(PacketHelper.CreateNotModifiedPacket(), RemoteIPEndPoint);

            // Run on a background thread so _sessions[endpoint] = session is set
            // in the caller before OnSessionEnd removes it.
            Thread thread = new Thread(() => OnSessionEnd?.Invoke(this)) { IsBackground = true };
            thread.Start();
        }

        /// <summary>
        /// Reads all frame files into memory and streams them to the device using the
        /// BATCH_START → DATA×N → BATCH_COMMIT protocol.
        /// </summary>
        private void BatchSessionLoop()
        {
            if (_framePaths is null) return;

            // Read all frames into memory.
            byte[][] frames        = new byte[_framePaths.Length][];
            uint[]   frameSizesArr = new uint[BatchStartPayload.MaxFrames];

            for (int i = 0; i < _framePaths.Length; i++)
            {
                try
                {
                    using FileStream fs = new(_framePaths[i], FileMode.Open,
                                              FileAccess.Read, FileShare.ReadWrite);
                    frames[i] = new byte[fs.Length];
                    fs.ReadExactly(frames[i], 0, frames[i].Length);
                    frameSizesArr[i] = (uint)frames[i].Length;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[BatchSession {_sessionId}] Cannot read frame {i}: {ex.Message}");
                    OnSessionEnd?.Invoke(this);
                    return;
                }
            }

            // Pre-compute standard CRC32 over all frame bytes concatenated in transmission
            // order — identical to what the device accumulates across DATA chunks via
            // esp_rom_crc32_le(running_crc, buf, len) starting from 0.
            // Stored in BatchCrc after a successful BATCH_COMMIT so UDPServer can skip the
            // re-transfer when the device reconnects and sends the same CRC in its HELLO.
            uint computedBatchCrc = ComputeCrc32OverFrames(frames);

            // Send BATCH_START.
            BatchStartPayload bsp = new()
            {
                FrameCount = (byte)_framePaths.Length,
                Fps        = _batchFps,
                Reserved   = 0,
                FrameSizes = frameSizesArr,
            };

            _socket?.SendTo(PacketHelper.CreateBatchStartPacket(_sessionId, bsp), RemoteIPEndPoint);

            // Wait for ACK of BATCH_START (seq=0).
            if (!WaitAck(0, 8000))
            {
                Console.WriteLine($"[BatchSession {_sessionId}] BATCH_START not ACKed — aborting");
                OnSessionEnd?.Invoke(this);
                return;
            }

            // Stream all frames concatenated as DATA chunks (stop-and-wait per chunk).
            Span<byte> sendBuf = stackalloc byte[UDPServerConfig.PacketSize];
            uint       seq     = 0;

            for (int fi = 0; fi < frames.Length; fi++)
            {
                // Abort early if a new HELLO arrived and the session was superseded.
                if (_abortRequested)
                {
                    Console.WriteLine($"[BatchSession {_sessionId}] Aborted before frame {fi} — new session started");
                    OnSessionEnd?.Invoke(this);
                    return;
                }

                byte[] frameData   = frames[fi];
                int    totalChunks = (frameData.Length + PacketHelper.PacketPayloadSize - 1)
                                     / PacketHelper.PacketPayloadSize;

                for (int ci = 0; ci < totalChunks; ci++)
                {
                    // Check abort inside the chunk loop so a superseded session stops
                    // sending stale DATA immediately and does not flood the device's recv
                    // buffer, which would otherwise reset the new session's 3 s SO_RCVTIMEO.
                    if (_abortRequested)
                    {
                        Console.WriteLine($"[BatchSession {_sessionId}] Aborted at frame {fi} chunk {ci} — new session started");
                        OnSessionEnd?.Invoke(this);
                        return;
                    }

                    int offset = ci * PacketHelper.PacketPayloadSize;
                    int size   = Math.Min(PacketHelper.PacketPayloadSize, frameData.Length - offset);
                    bool isLastOfAll = fi == frames.Length - 1 && ci == totalChunks - 1;

                    PacketHeader dataHdr = new()
                    {
                        Magic         = UDPServerConfig.MagicToken,
                        PacketType    = (int)PacketTypes.DATA,
                        SessionId     = _sessionId,
                        Seq           = seq,
                        PayloadLength = (UInt16)size,
                        // FLAG_LAST only on the very last chunk of the entire batch.
                        Flags         = (UInt16)(isLastOfAll ? (int)PacketStatuses.LAST : 0),
                    };

                    ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(frameData, offset, size);
                    PacketHelper.WritePacket(sendBuf, ref dataHdr, payload);

                    bool acked = false;
                    for (int attempt = 0; attempt < BatchMaxRetries && !acked && !_abortRequested; attempt++)
                    {
                        _socket?.SendTo(sendBuf.ToArray(), RemoteIPEndPoint);
                        acked = WaitAck(seq, BatchAckTimeoutMs);
                        if (!acked && attempt > 0) OnLostPacket?.Invoke(RemoteIPEndPoint);
                    }

                    if (_abortRequested)
                    {
                        Console.WriteLine($"[BatchSession {_sessionId}] Aborted while waiting ACK seq={seq} — new session started");
                        OnSessionEnd?.Invoke(this);
                        return;
                    }

                    if (!acked)
                    {
                        Console.WriteLine(
                            $"[BatchSession {_sessionId}] Chunk seq={seq} not ACKed after {BatchMaxRetries} attempts — aborting");
                        OnSessionEnd?.Invoke(this);
                        return;
                    }

                    seq++;
                }

                Console.WriteLine(
                    $"[BatchSession {_sessionId}] Frame {fi} sent ({frameData.Length} bytes)");
            }

            // Send BATCH_COMMIT.
            _socket?.SendTo(PacketHelper.CreateBatchCommitPacket(_sessionId), RemoteIPEndPoint);

            // Wait for commit ACK (seq=0).
            bool commitAcked = WaitAck(0, 8000);
            if (commitAcked)
            {
                Console.WriteLine($"[BatchSession {_sessionId}] BATCH_COMMIT ACKed — playback started on device");
                BatchCrc         = computedBatchCrc;
                SessionSucceeded = true;
                OnSendFile?.Invoke((ulong)(frames.Sum(f => (long)f.Length) / 1024));
            }
            else
            {
                Console.WriteLine($"[BatchSession {_sessionId}] BATCH_COMMIT not ACKed");
            }

            OnSessionEnd?.Invoke(this);
        }

        // ── Batch (Class-C) per-chunk ACK timeout ────────────────────────────
        // Must be LESS than the ESP32 firmware batch recv timeout (SO_RCVTIMEO = 3 s).
        // If the server waited longer than 3 s the device would time out the batch loop
        // before the retransmit arrived — the device then sends a new HELLO, the old
        // session is aborted, yet it keeps flooding the channel until WaitAck returns.
        // At 2 s: server retransmits within the device's 3 s window so the device can
        // ACK the retransmit without re-starting the whole session.
        private const int BatchAckTimeoutMs = 2000;  // ms per chunk — must stay < 3000
        private const int BatchMaxRetries   = 3;     // retransmit attempts per chunk

        // ── Sliding-window Go-Back-N constants ───────────────────────────────
        // W=8 means up to 8 chunks are in-flight before the server waits for ACKs.
        // On WiFi RTT=20ms: throughput rises from ~49 KB/s (stop-and-wait) to ~390 KB/s.
        // AckTimeoutMs is per-chunk; on packet loss the server retransmits from the lost
        // seq so the ESP32 stays in sync (Go-Back-N: it discards ahead-of-order chunks).
        private const int WindowSize   = 8;    // chunks in flight simultaneously
        private const int AckTimeoutMs = 500;  // ms per chunk before retransmit
        private const int MaxRetries   = 3;    // retransmit attempts per chunk

        /// <summary>
        /// Streams <c>_fileData</c> to the device using a sliding Go-Back-N window.
        /// Sends up to <see cref="WindowSize"/> chunks without waiting for ACKs, then
        /// drains incoming ACKs and advances the window. On timeout, retransmits from
        /// the oldest unACKed chunk (Go-Back-N), compatible with ESP32 firmware that
        /// discards out-of-order chunks and NAKs by re-ACKing the last in-order seq.
        /// </summary>
        private void SessionLoop()
        {
            if (_fileData == null || _fileData.Length == 0)
                return;

            Stopwatch transferSw = Stopwatch.StartNew();

            uint totalChunks =
                (uint)(_fileData.Length + PacketHelper.PacketPayloadSize - 1)
                / PacketHelper.PacketPayloadSize;

            bool[] acked          = new bool[totalChunks];
            long[] sentAt         = new long[totalChunks];
            int[]  chunkRetries   = new int[totalChunks];
            int    totalRetryCount = 0;
            uint   lastProgressAt  = 0;   // windowBase value at last OnProgress call

            // Heap-allocated send buffer — avoids .ToArray() allocation per packet.
            byte[] sendBuf = new byte[UDPServerConfig.PacketSize];
            ReadOnlySpan<byte> fileSpan = new ReadOnlySpan<byte>(_fileData, 0, _fileData.Length);

            uint windowBase = 0;  // oldest unACKed seq
            uint nextSend   = 0;  // next seq to transmit
            bool aborted    = false;

            while (windowBase < totalChunks && !_abortRequested && !aborted)
            {
                // ── Fill send window ──────────────────────────────────────────────
                while (nextSend < totalChunks && nextSend < windowBase + WindowSize)
                {
                    SendDataChunk(nextSend, totalChunks, fileSpan, sendBuf, retransmit: false);
                    sentAt[nextSend] = transferSw.ElapsedMilliseconds;
                    nextSend++;
                }

                // ── Drain incoming ACKs (short wait so fill loop stays responsive) ─
                DrainAcks(acked, waitMs: 50);

                // ── Advance window base over consecutive ACKed seqs ───────────────
                while (windowBase < totalChunks && acked[windowBase])
                    windowBase++;

                // Fire OnProgress every ProgressIntervalChunks ACKed chunks.
                if (windowBase - lastProgressAt >= ProgressIntervalChunks && OnProgress is not null)
                {
                    OnProgress(
                        (int)windowBase,
                        (int)totalChunks,
                        (int)transferSw.ElapsedMilliseconds,
                        totalRetryCount);
                    lastProgressAt = windowBase;
                }

                // ── Check for timed-out chunks → Go-Back-N retransmit ─────────────
                long now = transferSw.ElapsedMilliseconds;
                for (uint seq = windowBase; seq < nextSend; seq++)
                {
                    if (acked[seq] || now - sentAt[seq] <= AckTimeoutMs)
                        continue;

                    if (++chunkRetries[seq] > MaxRetries)
                    {
                        Console.WriteLine(
                            $"[Session {_sessionId}] seq={seq} lost after {MaxRetries} retries — aborting");
                        SendErrorPacket((UInt16)PacketStatuses.TIMEOUT, sendBuf);
                        aborted = true;
                        break;
                    }

                    // Go-Back-N: reset nextSend so fill loop retransmits from here.
                    Console.WriteLine(
                        $"[Session {_sessionId}] timeout seq={seq} retry {chunkRetries[seq]}/{MaxRetries} — Go-Back-N");
                    totalRetryCount++;
                    OnLostPacket?.Invoke(RemoteIPEndPoint);
                    nextSend = seq;
                    break;
                }
            }

            if (!_abortRequested && !aborted && windowBase == totalChunks)
            {
                SendDonePacket(sendBuf);
                SessionSucceeded = true;
                OnSendFile?.Invoke((ulong)(_fileData.Length / 1024));
            }
            else if (_abortRequested)
            {
                Console.WriteLine($"[Session {_sessionId}] aborted by new HELLO — stopping transfer");
            }

            transferSw.Stop();
            TransferDurationMs = transferSw.ElapsedMilliseconds;
            TotalRetries       = totalRetryCount;
            OnSessionEnd?.Invoke(this);
        }

        /// <summary>Sends a single DATA chunk (or retransmit) to the device.</summary>
        private void SendDataChunk(
            uint seq, uint totalChunks,
            ReadOnlySpan<byte> fileSpan, byte[] sendBuf,
            bool retransmit)
        {
            int offset  = (int)(seq * PacketHelper.PacketPayloadSize);
            int size    = Math.Min(PacketHelper.PacketPayloadSize, _fileData!.Length - offset);
            bool isLast = seq == totalChunks - 1;

            PacketHeader header = new PacketHeader()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (int)PacketTypes.DATA,
                SessionId     = _sessionId,
                Seq           = seq,
                PayloadLength = (UInt16)size,
                Flags         = (UInt16)((isLast   ? (int)PacketStatuses.LAST      : 0)
                                       | (retransmit ? (int)PacketStatuses.RETRANSMIT : 0))
            };

            ReadOnlySpan<byte> payload = fileSpan.Slice(offset, size);
            PacketHelper.WritePacket(sendBuf, ref header, payload);
            _socket?.SendTo(sendBuf, 0, PacketHelper.PacketHeaderSize + size,
                            SocketFlags.None, RemoteIPEndPoint);
        }

        /// <summary>Sends a DONE packet signalling successful end of transfer.</summary>
        private void SendDonePacket(byte[] sendBuf)
        {
            PacketHeader done = new PacketHeader()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (int)PacketTypes.DONE,
                SessionId     = _sessionId,
                Seq           = 0,
                PayloadLength = 0,
                Flags         = 0
            };
            PacketHelper.WritePacket(sendBuf, ref done, default);
            _socket?.SendTo(sendBuf, 0, PacketHelper.PacketHeaderSize,
                            SocketFlags.None, RemoteIPEndPoint);
        }

        /// <summary>Sends an ERROR packet with the given status flags.</summary>
        private void SendErrorPacket(UInt16 flags, byte[] sendBuf)
        {
            PacketHeader err = new PacketHeader()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (int)PacketTypes.ERROR,
                SessionId     = _sessionId,
                Seq           = 0,
                PayloadLength = 0,
                Flags         = flags
            };
            PacketHelper.WritePacket(sendBuf, ref err, default);
            _socket?.SendTo(sendBuf, 0, PacketHelper.PacketHeaderSize,
                            SocketFlags.None, RemoteIPEndPoint);
        }

        /// <summary>
        /// Waits up to <paramref name="timeoutMs"/> milliseconds for an ACK packet whose
        /// <c>Seq</c> field equals <paramref name="expectedSeq"/>. Used by the stop-and-wait
        /// batch transfer path (<see cref="BatchSessionLoop"/>).
        /// </summary>
        /// <returns><c>true</c> when the matching ACK arrived within the timeout.</returns>
        private bool WaitAck(uint expectedSeq, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && !_abortRequested)
            {
                int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (remaining <= 0) break;
                _recvEvent.WaitOne(remaining);

                lock (_recvPackets)
                {
                    while (_recvPackets.Count > 0)
                    {
                        byte[] pkt = _recvPackets.Dequeue();
                        if (pkt.Length < PacketHelper.PacketHeaderSize) continue;

                        PacketHeader h = PacketHelper.ReadHeader(pkt);
                        if (h.Magic      == UDPServerConfig.MagicToken
                            && h.SessionId == _sessionId
                            && h.PacketType == (int)PacketTypes.ACK
                            && h.Seq        == expectedSeq)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Drains all ACK packets currently queued in <c>_recvPackets</c>, marking
        /// each confirmed seq in <paramref name="acked"/>. Waits at most
        /// <paramref name="waitMs"/> milliseconds for the first packet to arrive.
        /// </summary>
        private void DrainAcks(bool[] acked, int waitMs)
        {
            _recvEvent.WaitOne(waitMs);
            lock (_recvPackets)
            {
                while (_recvPackets.Count > 0)
                {
                    byte[] pkt = _recvPackets.Dequeue();
                    if (pkt.Length < PacketHelper.PacketHeaderSize)
                        continue;

                    PacketHeader h = PacketHelper.ReadHeader(pkt);
                    if (h.Magic       == UDPServerConfig.MagicToken
                        && h.SessionId == _sessionId
                        && h.PacketType == (int)PacketTypes.ACK
                        && h.Seq        < (uint)acked.Length)
                    {
                        acked[h.Seq] = true;
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _recvPackets.Clear();
            _recvEvent.Dispose();

            if (_fileData != null)
            {
                Array.Clear(_fileData, 0, _fileData.Length);
                _fileData = null;
            }

            // Sealed class — no derived types, so GC.SuppressFinalize is the full pattern.
            GC.SuppressFinalize(this);
        }
    }
}
