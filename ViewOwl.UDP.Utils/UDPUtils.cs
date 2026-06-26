using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ViewOwl.Config;

namespace ViewOwl.UDP.Utils
{
    /// <summary>Error reason codes carried in the single-byte PACKET_ERROR payload.</summary>
    public enum ErrorCodes : byte
    {
        BadToken      = 0x01,  // HELLO payload could not be parsed as a device token
        UnknownDevice = 0x02,  // token not registered in the database
        NoTemplate    = 0x03,  // device exists but has no active template assigned
        NoFrame       = 0x04,  // template assigned but no successful grab has completed yet
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Windows API constant", Scope = "module")]
    [Flags]
    public enum PacketTypes : int
    {
        HELLO        = 0b00000_00001,  // 1   — device identifies itself; payload = token
        AUTH         = 0b00000_00010,  // 2   — server acknowledges and assigns session id
        NOT_MODIFIED = 0b00000_00011,  // 3   — server: frame CRC matches, skip download
        DATA         = 0b00000_00100,  // 4   — server sends a frame chunk
        BATCH_START  = 5,              // 5   — server: Class-C multi-frame transfer start; payload = BatchStartPayload
        BATCH_COMMIT = 6,              // 6   — server: all frames written to flash, start looped playback
        ACK          = 0b00000_01000,  // 8   — device acknowledges a DATA or PING packet
        DONE         = 0b00000_10000,  // 16  — server signals end of frame transfer
        ERROR        = 0b00001_00000,  // 32  — error / rejection
        PING         = 0b00010_00000,  // 64  — device heartbeat (uptime, heap, RSSI)
        CONFIG       = 0b00100_00000,  // 128 — server pushes config to device (ping interval, etc.)
    }

    [Flags]
    public enum PacketStatuses : int
    {
        LAST       = 0b0000_0000__0000_0001,
        RETRANSMIT = 0b0000_0000__0000_0010,
        TIMEOUT    = 0b0000_0000__0000_0100,
        BADPACKET  = 0b0000_0000__0000_1000,
        BYE        = 0b0000_0000__0001_0000,
        RESTART    = 0b0000_0000__0010_0000,  // instructs device to reboot after applying CONFIG
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)] // map to memory as-is, no byte padding
    [DebuggerDisplay("Magic = {Magic.ToString(\"X8\")}, PacketType = {PacketType.ToString(\"X8\")}, Flags = {Flags.ToString(\"X8\")}")]
    public struct PacketHeader : IEquatable<PacketHeader>
    {
        public UInt32 Magic;       // Magic number hardcoded for this protocol client and server (for blocking alien packets)
        public byte PacketType;    // SEE: PacketType flags enum
        public UInt16 SessionId;   // Session id after AUTH (65536 client sessions per one server, not ZERO)
        public UInt32 Seq;         // Client DATA packet index for DATA and ACK control; one packet PacketSize (1024); UInt32 * PacketSize data send limit per one session
        public UInt16 PayloadLength; // Current packet DATA section length, not more than PacketSize - PacketHeader length
        public UInt16 Flags;       // SEE: PacketFlags enum

        public PacketHeader(UInt32 Magic, byte PacketType, UInt16 SessionId, UInt32 Seq, UInt16 PayloadLength, UInt16 Flags)
        {
            this.Magic = Magic;
        }

        public override bool Equals(object? obj) => obj is PacketHeader otherPacketHeader && Equals(otherPacketHeader);

        public bool Equals(PacketHeader other) =>
            Magic == other.Magic
            && PacketType == other.PacketType
            && SessionId == other.SessionId
            && Seq == other.Seq
            && PayloadLength == other.PayloadLength
            && Flags == other.Flags;

        public override int GetHashCode() => HashCode.Combine(Magic, PacketType, SessionId, Seq, PayloadLength, Flags);

        public static bool operator ==(PacketHeader left, PacketHeader right) => left.Equals(right);

        public static bool operator !=(PacketHeader left, PacketHeader right) => !left.Equals(right);
    }


    public static class PacketHelper
    {
        public static UInt16 PacketHeaderSize { get; } = (UInt16)Marshal.SizeOf<PacketHeader>();
        public static UInt16 PacketPayloadSize { get; } = (UInt16)(UDPServerConfig.PacketSize - PacketHeaderSize);

        public static ReadOnlySpan<byte> ReadPayload(ReadOnlySpan<byte> packet)
        {
            if (packet.Length <= Marshal.SizeOf<PacketHeader>())
            {
                return default;
            }

            PacketHeader header = ReadHeader(packet);

            if (header.PacketType == (int)PacketTypes.ERROR || header.PayloadLength + Marshal.SizeOf<PacketHeader>() != packet.Length)
            {
                return default;
            }

            return packet.Slice(Marshal.SizeOf<PacketHeader>(), packet.Length - Marshal.SizeOf<PacketHeader>());
        }

        public static int WritePacket(Span<byte> buf, ref PacketHeader header, ReadOnlySpan<byte> payload)
        {
            if (payload.IsEmpty)
            {
                return WriteHeader(buf, ref header);
            }

            if (payload.Length + Marshal.SizeOf<PacketHeader>() > UDPServerConfig.PacketSize || buf.Length < payload.Length + Marshal.SizeOf<PacketHeader>())
            {
                return -1;
            }

            header.PayloadLength = (UInt16)payload.Length;


            if (WriteHeader(buf, ref header) == -1)
            {
                return -2;
            }

            payload.CopyTo(buf.Slice(Marshal.SizeOf<PacketHeader>(), payload.Length));

            return buf.Length;
        }

        public static int WriteHeader(Span<byte> buf, ref PacketHeader header)
        {
            if (buf.Length < Marshal.SizeOf<PacketHeader>())
            {
                return -1;
            }

            int offset = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(offset, sizeof(UInt32)), header.Magic);
            offset += sizeof(UInt32);

            buf[offset] = header.PacketType;
            offset += sizeof(byte);

            BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(offset, sizeof(UInt16)), header.SessionId);
            offset += sizeof(UInt16);

            BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(offset, sizeof(UInt32)), header.Seq);
            offset += sizeof(UInt32);


            BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(offset, sizeof(UInt16)), header.PayloadLength);
            offset += sizeof(UInt16);

            BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(offset, sizeof(UInt16)), header.Flags);
            offset += sizeof(UInt16);

            return offset;
        }

        public static PacketHeader ReadHeader(ReadOnlySpan<byte> buf)
        {
            PacketHeader header = new PacketHeader();

            if (buf.Length < Marshal.SizeOf<PacketHeader>() || BinaryPrimitives.ReadUInt32LittleEndian(buf) != UDPServerConfig.MagicToken)
            {
                header.PacketType = (byte)PacketTypes.ERROR;
                header.Flags = (UInt16)PacketStatuses.BADPACKET;
                return header;
            }

            int offset = 0;

            header.Magic = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(offset, sizeof(UInt32)));
            offset += sizeof(UInt32);

            header.PacketType = buf[offset];
            offset += sizeof(byte);

            header.SessionId = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset, sizeof(UInt16)));
            offset += sizeof(UInt16);

            header.Seq = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(offset, sizeof(UInt32)));
            offset += sizeof(UInt32);

            header.PayloadLength = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset, sizeof(UInt16)));
            offset += sizeof(UInt16);

            header.Flags = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(offset, sizeof(UInt16)));
            offset += sizeof(UInt16);

            return header;
        }

        /// <summary>
        /// Reads a blittable struct from the beginning of <paramref name="data"/>.
        /// Throws <see cref="ArgumentException"/> when the span is too short.
        /// </summary>
        public static T ReadStruct<T>(ReadOnlySpan<byte> data) where T : struct
        {
            if (data.Length < Marshal.SizeOf<T>())
                throw new ArgumentException($"Data too small to read {typeof(T).Name} ({Marshal.SizeOf<T>()} bytes required, {data.Length} available).");

            return MemoryMarshal.Read<T>(data);
        }

        /// <summary>
        /// Writes a blittable struct into the beginning of <paramref name="buffer"/>.
        /// Throws <see cref="ArgumentException"/> when the buffer is too short.
        /// </summary>
        public static void WriteStruct<T>(Span<byte> buffer, T value) where T : struct
        {
            if (buffer.Length < Marshal.SizeOf<T>())
                throw new ArgumentException($"Buffer too small to write {typeof(T).Name} ({Marshal.SizeOf<T>()} bytes required, {buffer.Length} available).");

            MemoryMarshal.Write(buffer, ref value);
        }

        /// <summary>
        /// Builds a PACKET_ERROR response to send back to a device when HELLO authentication fails.
        /// </summary>
        /// <param name="errorCode">One of the <see cref="ErrorCodes"/> values; 0 sends no payload.</param>
        public static byte[] CreateErrorPacket(byte errorCode = 0)
        {
            bool hasPayload = errorCode > 0;
            int payloadSize = hasPayload ? 1 : 0;

            PacketHeader hdr = new()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (byte)PacketTypes.ERROR,
                SessionId     = 0,
                Seq           = 0,
                PayloadLength = (UInt16)payloadSize,
                Flags         = 0,
            };

            byte[] packet = new byte[Marshal.SizeOf<PacketHeader>() + payloadSize];
            WriteHeader(packet, ref hdr);
            if (hasPayload) packet[Marshal.SizeOf<PacketHeader>()] = errorCode;
            return packet;
        }

        /// <summary>
        /// Builds an ACK packet to acknowledge a PING from the device.
        /// </summary>
        /// <param name="sessionId">Session id copied from the incoming PING header.</param>
        public static byte[] CreatePingAck(UInt16 sessionId)
        {
            PacketHeader hdr = new()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (byte)PacketTypes.ACK,
                SessionId     = sessionId,
                Seq           = 0,
                PayloadLength = 0,
                Flags         = 0,
            };

            byte[] packet = new byte[Marshal.SizeOf<PacketHeader>()];
            WriteHeader(packet, ref hdr);
            return packet;
        }

        /// <summary>
        /// Builds a CONFIG packet carrying the supplied <paramref name="config"/> payload.
        /// </summary>
        /// <param name="sessionId">Active session id for this device.</param>
        /// <param name="config">Configuration values to push to the device.</param>
        /// <param name="flags">Optional header flags (e.g. <see cref="PacketStatuses.RESTART"/>).</param>
        public static byte[] CreateConfigPacket(UInt16 sessionId, ConfigPacketPayload config, UInt16 flags = 0)
        {
            int payloadSize = Marshal.SizeOf<ConfigPacketPayload>();

            PacketHeader hdr = new()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (byte)PacketTypes.CONFIG,
                SessionId     = sessionId,
                Seq           = 0,
                PayloadLength = (UInt16)payloadSize,
                Flags         = flags,
            };

            byte[] payload = new byte[payloadSize];
            WriteStruct<ConfigPacketPayload>(payload, config);

            byte[] packet = new byte[Marshal.SizeOf<PacketHeader>() + payloadSize];
            WritePacket(packet, ref hdr, payload);
            return packet;
        }

        /// <summary>
        /// Builds a NOT_MODIFIED packet to inform the device that the frame CRC matches
        /// what it already has — no data transfer needed.
        /// </summary>
        public static byte[] CreateNotModifiedPacket()
        {
            PacketHeader hdr = new()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (byte)PacketTypes.NOT_MODIFIED,
                SessionId     = 0,
                Seq           = 0,
                PayloadLength = 0,
                Flags         = 0,
            };

            byte[] packet = new byte[Marshal.SizeOf<PacketHeader>()];
            WriteHeader(packet, ref hdr);
            return packet;
        }

        /// <summary>
        /// Builds a BATCH_START packet announcing a Class-C multi-frame transfer.
        /// </summary>
        /// <param name="sessionId">Active session id for this device.</param>
        /// <param name="payload">Batch metadata (frame count, FPS, per-frame sizes).</param>
        public static byte[] CreateBatchStartPacket(UInt16 sessionId, BatchStartPayload payload)
        {
            int payloadSize = Marshal.SizeOf<BatchStartPayload>();

            PacketHeader hdr = new()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (byte)PacketTypes.BATCH_START,
                SessionId     = sessionId,
                Seq           = 0,
                PayloadLength = (UInt16)payloadSize,
                Flags         = 0,
            };

            // BatchStartPayload contains a managed uint[] field marshalled via [MarshalAs],
            // so MemoryMarshal.Write (which requires blittable types) cannot be used.
            // Marshal.StructureToPtr correctly handles [MarshalAs(ByValArray)] attributes.
            byte[] payloadBytes = new byte[payloadSize];
            IntPtr ptr = Marshal.AllocHGlobal(payloadSize);
            try
            {
                Marshal.StructureToPtr(payload, ptr, false);
                Marshal.Copy(ptr, payloadBytes, 0, payloadSize);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            byte[] packet = new byte[Marshal.SizeOf<PacketHeader>() + payloadSize];
            WritePacket(packet, ref hdr, payloadBytes);
            return packet;
        }

        /// <summary>
        /// Builds a BATCH_COMMIT packet — tells the device all frames are stored, begin playback.
        /// </summary>
        /// <param name="sessionId">Active session id for this device.</param>
        public static byte[] CreateBatchCommitPacket(UInt16 sessionId)
        {
            PacketHeader hdr = new()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (byte)PacketTypes.BATCH_COMMIT,
                SessionId     = sessionId,
                Seq           = 0,
                PayloadLength = 0,
                Flags         = 0,
            };

            byte[] packet = new byte[Marshal.SizeOf<PacketHeader>()];
            WriteHeader(packet, ref hdr);
            return packet;
        }

        /// <summary>
        /// Builds a minimal AUTH packet with no payload and <c>SessionId = 0</c>.
        /// Sent by the server to an idle device to trigger an immediate frame refresh:
        /// the device recognises the AUTH packet type during its idle PING loop and
        /// breaks out to send a new HELLO, starting a fresh transfer session.
        /// </summary>
        public static byte[] CreateAuthTriggerPacket()
        {
            PacketHeader hdr = new()
            {
                Magic         = UDPServerConfig.MagicToken,
                PacketType    = (byte)PacketTypes.AUTH,
                SessionId     = 0,
                Seq           = 0,
                PayloadLength = 0,
                Flags         = 0,
            };

            byte[] packet = new byte[Marshal.SizeOf<PacketHeader>()];
            WriteHeader(packet, ref hdr);
            return packet;
        }

    }

    /// <summary>
    /// Payload carried in a BATCH_START packet.
    /// Binary-identical to <c>batch_start_payload_t</c> in <c>packet.h</c>.
    /// Total size: 4 + MaxFrames * 4 = 132 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BatchStartPayload
    {
        /// <summary>
        /// Maximum frames per batch.  Must match BATCH_MAX_FRAMES in packet.h.
        /// Raised 16→32 to allow longer Class-C loops (e.g. the 30-frame trench).
        /// Wire-compatible with ≤16-frame batches on older firmware (which reads
        /// only the first 68 bytes); >16-frame batches require firmware ≥ the
        /// matching BATCH_MAX_FRAMES=32 build.
        /// </summary>
        public const int MaxFrames = 32;

        /// <summary>Number of frames to transfer (1..MaxFrames).</summary>
        public byte FrameCount;

        /// <summary>Playback frame-rate in frames per second.</summary>
        public byte Fps;

        /// <summary>Reserved — must be zero.</summary>
        public ushort Reserved;

        /// <summary>
        /// Compressed byte length of each frame.  Only the first FrameCount entries are valid;
        /// the remaining slots must be zero.
        /// </summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxFrames)]
        public uint[] FrameSizes;
    }
}
