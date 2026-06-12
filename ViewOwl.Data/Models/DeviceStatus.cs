namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Real-time connection status of an ESP32 device.
    /// </summary>
    public enum DeviceStatus
    {
        /// <summary>No recent PING/HELLO — device is considered disconnected.</summary>
        Offline = 0,

        /// <summary>Device sent a PING/HELLO within the expected heartbeat window.</summary>
        Online = 1
    }
}
