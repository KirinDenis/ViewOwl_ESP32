using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ViewOwl.Grabber.Engine.Converters
{
    /// <summary>
    /// Converts an RGB image to a 2-bit-per-pixel ("4-gray" / "mono-CGA") packed buffer
    /// for SSD1683-class e-paper displays.
    /// </summary>
    /// <remarks>
    /// Exact target format (must match the ESP32 firmware's <c>epd_display_4gray</c>
    /// byte-for-byte):
    /// <list type="bullet">
    /// <item>2 bits per pixel, 4 pixels per byte, MSB-first:
    /// pixel0 = bits 7..6, pixel1 = bits 5..4, pixel2 = bits 3..2, pixel3 = bits 1..0.</item>
    /// <item>Per-pixel 2-bit code: <c>0b11</c> = white (brightest), <c>0b10</c> = light grey,
    /// <c>0b01</c> = dark grey, <c>0b00</c> = black (darkest). So
    /// <c>code == round(L / 85)</c> where L is 0..255 luminance
    /// (code 3 ↔ 255 white, 2 ↔ 170, 1 ↔ 85, 0 ↔ 0).</item>
    /// <item>Row stride = <c>ceil(width × 2 / 8) = ceil(width / 4)</c> bytes.
    /// For 400×300 → 100 bytes/row × 300 = 30000 bytes total.</item>
    /// </list>
    /// Produces the NATURAL luminance mapping (bright pixel → white code 3); the firmware
    /// applies inversion itself. This matches the <see cref="MonochromeConverter"/> convention
    /// (bit = 1 / white when bright).
    /// Luminance uses ITU-R BT.601 coefficients <c>L = 0.299·R + 0.587·G + 0.114·B</c>,
    /// quantized to the four levels {0, 85, 170, 255} via Floyd-Steinberg error diffusion.
    /// </remarks>
    public sealed class FourGrayConverter : IImageConverter
    {
        /// <inheritdoc/>
        public string FormatId => "mono4gray";

        /// <inheritdoc/>
        public byte[] Convert(Image<Rgb24> image)
        {
            ArgumentNullException.ThrowIfNull(image);

            int width  = image.Width;
            int height = image.Height;

            // Float working buffer of luminances so Floyd-Steinberg error can accumulate
            // across neighbours before quantization.
            float[] luminance = new float[width * height];
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    Rgb24 px = image[x, y];
                    luminance[rowOffset + x] = 0.299f * px.R + 0.587f * px.G + 0.114f * px.B;
                }
            }

            int stride   = (width + 3) / 4; // ceil(width / 4) bytes per row
            byte[] dest  = new byte[stride * height];

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                int destRow   = y * stride;

                for (int x = 0; x < width; x++)
                {
                    float workingValue = luminance[rowOffset + x];

                    // Quantize to the nearest of the four levels {0, 85, 170, 255}.
                    int nearestCode    = Math.Clamp((int)MathF.Round(workingValue / 85f), 0, 3);
                    int nearestValue   = nearestCode * 85;
                    float error        = workingValue - nearestValue;

                    // Pack the 2-bit code MSB-first: pixel index 0 occupies bits 7..6.
                    int shift = 6 - ((x & 3) * 2);
                    dest[destRow + (x >> 2)] |= (byte)(nearestCode << shift);

                    // Diffuse the quantization error to the not-yet-processed neighbours.
                    if (x + 1 < width)
                        luminance[rowOffset + x + 1] += error * (7f / 16f);

                    if (y + 1 < height)
                    {
                        int nextRow = rowOffset + width;
                        if (x - 1 >= 0)
                            luminance[nextRow + x - 1] += error * (3f / 16f);
                        luminance[nextRow + x]         += error * (5f / 16f);
                        if (x + 1 < width)
                            luminance[nextRow + x + 1] += error * (1f / 16f);
                    }
                }
            }

            return dest;
        }
    }
}
