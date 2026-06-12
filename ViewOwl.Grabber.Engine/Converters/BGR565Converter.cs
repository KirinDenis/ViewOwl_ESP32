using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ViewOwl.Grabber.Engine.Converters
{
    /// <summary>
    /// Converts an RGB image to RGB565 packed binary format (2 bytes per pixel, big-endian).
    /// </summary>
    /// <remarks>
    /// Bit layout per 16-bit pixel value as the panel receives it over SPI:
    /// <code>
    ///   bits [15:11] = Red   (R &gt;&gt; 3,  5 bits)
    ///   bits [10:5]  = Green (G &gt;&gt; 2,  6 bits)
    ///   bits  [4:0]  = Blue  (B &gt;&gt; 3,  5 bits)
    /// </code>
    ///
    /// Why this works with <c>LCD_RGB_ELEMENT_ORDER_BGR</c>:
    /// The ST7796 panel is initialised with <c>LCD_RGB_ELEMENT_ORDER_BGR</c>, which sets the
    /// MADCTL BGR bit so the panel's internal element order matches the physical RGB filter layout.
    /// From the software side the effective pixel format is still standard RGB565 —
    /// the driver compensates transparently — so bits[15:11] drive the Red sub-pixel.
    ///
    /// Byte order:
    /// The SPI controller sends bytes low-address-first (i.e. the byte stored at
    /// <c>dest[offset]</c> is transmitted first and becomes the MSB of the 16-bit pixel value
    /// as received by the panel). Therefore pixels are written big-endian (high byte first).
    ///
    /// Example — pure Red (R=255, G=0, B=0):
    /// <code>
    ///   rgb565 = 0xF800
    ///   dest[offset]   = 0xF8   ← first byte sent = MSB
    ///   dest[offset+1] = 0x00   ← second byte sent = LSB
    ///   Panel sees 0xF800 → R=31, G=0, B=0 → RED ✓
    /// </code>
    /// </remarks>
    public sealed class BGR565Converter : IImageConverter
    {
        /// <inheritdoc/>
        public string FormatId => "bgr565";

        /// <inheritdoc/>
        public byte[] Convert(Image<Rgb24> image)
        {
            ArgumentNullException.ThrowIfNull(image);
            int width  = image.Width;
            int height = image.Height;
            int stride = width * 2;
            byte[] dest = new byte[height * stride];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Rgb24 pixel = image[x, y];

                    // Pack RGB888 → RGB565 (R in bits[15:11], G in bits[10:5], B in bits[4:0]).
                    // Written big-endian so the panel receives the MSB (Red channel) first over SPI.
                    ushort rgb565 = (ushort)(
                        (((pixel.R >> 3) & 0x1F) << 11) |
                        (((pixel.G >> 2) & 0x3F) << 5)  |
                        ((pixel.B >> 3) & 0x1F));

                    int offset = y * stride + x * 2;
                    dest[offset]     = (byte)((rgb565 >> 8) & 0xFF); // high byte first (MSB for panel)
                    dest[offset + 1] = (byte)(rgb565 & 0xFF);         // low byte
                }
            }

            return dest;
        }
    }
}
