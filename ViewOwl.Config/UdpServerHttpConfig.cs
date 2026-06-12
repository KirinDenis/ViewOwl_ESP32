namespace ViewOwl.Config
{
    /// <summary>
    /// Configuration for the UDP Server's internal HTTP API.
    /// </summary>
    public sealed class UdpServerHttpConfig
    {
        /// <summary>
        /// Port for the HTTP API (localhost only).
        /// Default: 5010.
        /// </summary>
        public int HttpPort { get; set; } = 5010;

        /// <summary>
        /// Whether the HTTP API is enabled.
        /// Default: true.
        /// </summary>
        public bool Enabled { get; set; } = true;
    }
}
