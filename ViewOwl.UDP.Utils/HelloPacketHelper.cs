using System.Runtime.InteropServices;
using System.Text;

namespace ViewOwl.UDP.Utils
{
    /// <summary>
    /// Parsing helpers for the HELLO packet sent by ESP32 devices on first connection.
    /// Supports both legacy (GUID string) and extended (struct) formats for
    /// backward compatibility while firmware is being upgraded.
    /// </summary>
    public static class HelloPacketHelper
    {
        private static readonly int ExtendedSize = Marshal.SizeOf<HelloPacketPayload>();

        /// <summary>
        /// Parses a legacy HELLO packet where the payload is the device token
        /// encoded as a UTF-8 string (32-char "N" format or 36-char standard format).
        /// </summary>
        /// <param name="payload">Raw packet payload bytes.</param>
        /// <returns>Success flag and parsed token.</returns>
        public static (bool Success, Guid Token) ParseLegacyHello(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 32)
                return (false, Guid.Empty);

            // Trim null bytes and whitespace that may pad the buffer to a fixed size.
            string tokenStr = Encoding.UTF8.GetString(payload).Trim('\0', ' ', '\r', '\n');

            return Guid.TryParse(tokenStr, out Guid token)
                ? (true, token)
                : (false, Guid.Empty);
        }

        /// <summary>
        /// Parses an extended HELLO packet where the payload is a
        /// <see cref="HelloPacketPayload"/> struct with device capabilities.
        /// </summary>
        /// <param name="data">Raw packet payload bytes.</param>
        /// <returns>Success flag, token, and full payload struct.</returns>
        public static (bool Success, Guid Token, HelloPacketPayload? Payload)
            ParseExtendedHello(ReadOnlySpan<byte> data)
        {
            if (data.Length < ExtendedSize)
                return (false, Guid.Empty, null);

            try
            {
                HelloPacketPayload p = PacketHelper.ReadStruct<HelloPacketPayload>(data);
                return (true, p.Token, p);
            }
            catch
            {
                return (false, Guid.Empty, null);
            }
        }

        /// <summary>
        /// Tries to parse a HELLO packet, attempting the extended format first
        /// and falling back to the legacy GUID-string format.
        /// </summary>
        /// <param name="payload">Raw packet payload bytes.</param>
        /// <returns>
        /// Success flag, device token, optional extended data, and optional MAC address.
        /// <c>MacAddress</c> is non-null only when the payload is ≥ 31 bytes (firmware v1.2.17+).
        /// Old firmware sends 25 bytes without the MAC field; <c>MacAddress</c> is <c>null</c> in that case.
        /// </returns>
        public static (bool Success, Guid Token, HelloPacketPayload? ExtendedData, string? MacAddress)
            ParseHello(ReadOnlySpan<byte> payload)
        {
            // A 32-byte ("N" format) or 36-byte ("D" format) payload is unambiguously a
            // legacy GUID string — the extended struct is 25 bytes, so treating 32/36 ASCII
            // bytes as a struct would silently produce a garbage token.
            if (payload.Length is 32 or 36)
            {
                var legacy = ParseLegacyHello(payload);
                return legacy.Success
                    ? (true, legacy.Token, null, null)
                    : (false, Guid.Empty, null, null);
            }

            // Extended struct format (25 bytes) or any other size — try extended first.
            var extended = ParseExtendedHello(payload);
            if (extended.Success)
            {
                // Firmware ≥ v1.2.17 appends 6 MAC bytes immediately after the 25-byte struct.
                string? mac = null;
                if (payload.Length >= 31)
                {
                    mac = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                        payload[25], payload[26], payload[27],
                        payload[28], payload[29], payload[30]);
                }

                return (extended.Success, extended.Token, extended.Payload, mac);
            }

            var legacyFallback = ParseLegacyHello(payload);
            return legacyFallback.Success
                ? (true, legacyFallback.Token, null, null)
                : (false, Guid.Empty, null, null);
        }

        /// <summary>
        /// Converts a <see cref="DisplayType"/> enum value to its display-controller name string.
        /// </summary>
        public static string DisplayTypeToString(DisplayType type) => type switch
        {
            DisplayType.ST7789  => "ST7789",
            DisplayType.ILI9341 => "ILI9341",
            DisplayType.ST7796  => "ST7796",
            _                   => "Unknown"
        };

        /// <summary>
        /// Formats a three-part firmware version as <c>"major.minor.patch"</c>.
        /// </summary>
        public static string FormatFirmwareVersion(byte major, byte minor, byte patch)
            => $"{major}.{minor}.{patch}";
    }
}
