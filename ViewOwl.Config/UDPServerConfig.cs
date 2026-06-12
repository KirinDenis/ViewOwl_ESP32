namespace ViewOwl.Config
{
    /// <summary>
    /// Shared constants for the UDP frame-delivery protocol.
    /// See <see href="../docs/hardware.md">hardware.md — "UDP packet sizing"</see> for the
    /// rationale behind packet size and the ACK protocol design.
    /// </summary>
    public static class UDPServerConfig
    {
        public const UInt16 PacketSize = 1400;
        public const UInt32 MagicToken = 0xABADBABE;

#pragma warning disable CA1707
        public const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
#pragma warning restore CA1707

        public const int WSAETIMEDOUT = 10060;

        /// <summary>
        /// Interrupted function call
        /// </summary>
        public const int WSAEINTR = 10004;
        
    }
}
