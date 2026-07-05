using System.Runtime.InteropServices;

namespace ViewOwl.UDP.Utils
{
    /// <summary>
    /// Extended HELLO packet payload sent by an ESP32 device on first connection.
    /// Carries device capabilities so the server can update the database without
    /// a separate configuration call.
    /// Total size: 25 bytes (blittable — compatible with <see cref="PacketHelper.ReadStruct{T}"/>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HelloPacketPayload
    {
        /// <summary>Device auth token as a raw 16-byte UUID (matches <c>Device.Token</c> in the DB).</summary>
        public Guid Token;

        /// <summary>Display controller type (see <see cref="DisplayType"/>).</summary>
        public byte DisplayTypeId;

        /// <summary>Display width in pixels.</summary>
        public ushort DisplayWidth;

        /// <summary>Display height in pixels.</summary>
        public ushort DisplayHeight;

        /// <summary>Firmware version — major component.</summary>
        public byte FirmwareVersionMajor;

        /// <summary>Firmware version — minor component.</summary>
        public byte FirmwareVersionMinor;

        /// <summary>Firmware version — patch component.</summary>
        public byte FirmwareVersionPatch;

        /// <summary>Reserved for future use; must be zero.</summary>
        public byte Reserved;
    }

    /// <summary>
    /// Display controller type enumeration — must stay in sync with ESP32 firmware.
    /// </summary>
    public enum DisplayType : byte
    {
        /// <summary>Unknown or not reported.</summary>
        Unknown = 0,

        /// <summary>Sitronix ST7789.</summary>
        ST7789 = 1,

        /// <summary>ILI Technology ILI9341.</summary>
        ILI9341 = 2,

        /// <summary>Sitronix ST7796.</summary>
        ST7796 = 3,

        /// <summary>ILI Technology ILI9486 (Waveshare RPi LCD via 74HC245).</summary>
        ILI9486 = 4,

        /// <summary>Galaxycore GC9A01 (240x240 round, ESP32-C3 client).</summary>
        GC9A01 = 5,

        /// <summary>Solomon Systech SSD1683 (400x300 4.2" e-paper, ESP32-S3 client).</summary>
        EPD = 6,

        /// <summary>Two Solomon Systech SSD1683 in master/slave cascade (792x272 5.79" wide e-paper, ESP32-S3 client).</summary>
        EPDWIDE = 7,
    }
}
