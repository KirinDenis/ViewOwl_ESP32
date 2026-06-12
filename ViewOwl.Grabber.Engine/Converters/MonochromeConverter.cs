using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ViewOwl.Grabber.Engine.Converters
{
    /// <summary>
    /// Converts an RGB image to 1-bit packed monochrome binary.
    /// Each byte represents 8 pixels (MSB = leftmost pixel in the byte).
    /// Output length: <c>ceil(width × height / 8)</c> bytes.
    /// </summary>
    /// <remarks>
    /// Luminance threshold (ITU-R BT.601 coefficients):
    /// <code>L = 0.299·R + 0.587·G + 0.114·B</code>
    /// Pixel is white (bit = 1) when L ≥ 128, black (bit = 0) otherwise.
    /// </remarks>
    public sealed class MonochromeConverter : IImageConverter
    {
        /// <inheritdoc/>
        public string FormatId => "mono1bit";

        /// <inheritdoc/>
        public byte[] Convert(Image<Rgb24> image)
        {
            ArgumentNullException.ThrowIfNull(image);
            int width       = image.Width;
            int height      = image.Height;
            int totalPixels = width * height;
            byte[] dest     = new byte[(totalPixels + 7) / 8];

            int bitIndex = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Rgb24 px = image[x, y];

                    // Integer-scaled BT.601 luminance (× 1000 to avoid floating-point)
                    int luminance = (299 * px.R + 587 * px.G + 114 * px.B) / 1000;

                    if (luminance >= 128)
                    {
                        // MSB = leftmost pixel; set the corresponding bit
                        dest[bitIndex / 8] |= (byte)(0x80 >> (bitIndex % 8));
                    }

                    bitIndex++;
                }
            }

            return dest;
        }
    }
}
