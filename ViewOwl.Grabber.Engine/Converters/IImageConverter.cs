using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ViewOwl.Grabber.Engine.Converters
{
    /// <summary>
    /// Converts an in-memory RGB image to a raw byte array for a specific display pixel format.
    /// Implementations must be stateless and thread-safe.
    /// </summary>
    public interface IImageConverter
    {
        /// <summary>
        /// Gets the unique identifier for this converter.
        /// Used to select the converter from <see cref="ScreenShotParamDTO.ConverterId"/>.
        /// </summary>
        string FormatId { get; }

        /// <summary>
        /// Converts the given image to a raw byte array in the implementation's pixel format.
        /// The image is already resized to the target display resolution before this call.
        /// </summary>
        /// <param name="image">Source image at target display resolution.</param>
        /// <returns>Raw byte array of pixel data, ready to write to ExchangeFolder.</returns>
        byte[] Convert(Image<Rgb24> image);
    }
}
