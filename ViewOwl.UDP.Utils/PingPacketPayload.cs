using System.Runtime.InteropServices;

namespace ViewOwl.UDP.Utils
{
    /// <summary>
    /// Payload carried by a <see cref="PacketTypes.PING"/> packet from the ESP32 device.
    /// Contains device health metrics for server-side monitoring.
    /// Must remain in sync with the <c>ping_payload_t</c> struct in ESP32 firmware.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PingPacketPayload
    {
        /// <summary>Device uptime in seconds since the last boot.</summary>
        public UInt32 Uptime;

        /// <summary>Free heap memory in bytes (internal DRAM).</summary>
        public UInt32 FreeHeap;

        /// <summary>WiFi signal strength in dBm (negative value; higher is better).</summary>
        public Int16 WifiRssi;

        /// <summary>Reserved — must be zero; reserved for future health fields.</summary>
        public UInt16 Reserved;
    }
}
