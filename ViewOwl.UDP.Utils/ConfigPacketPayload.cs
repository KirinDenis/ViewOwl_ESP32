using System.Runtime.InteropServices;

namespace ViewOwl.UDP.Utils
{
    /// <summary>
    /// Payload carried by a <see cref="PacketTypes.CONFIG"/> packet from the UDP Server to the device.
    /// Allows the server to push runtime configuration without a firmware update.
    /// Must remain in sync with the <c>config_payload_t</c> struct in ESP32 firmware.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ConfigPacketPayload
    {
        /// <summary>
        /// How often the device should send PING packets, in seconds.
        /// Valid range: 30–600. Value of 0 means "use firmware default".
        /// </summary>
        public UInt16 PingInterval;

        /// <summary>Reserved — must be zero.</summary>
        public UInt16 Reserved1;

        /// <summary>Reserved — must be zero.</summary>
        public UInt32 Reserved2;

        /// <summary>Reserved — must be zero.</summary>
        public UInt32 Reserved3;
    }
}
