using System.Drawing;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Formats;

/// <summary>
/// The pixels of a gain map stored by a format plugin, decoded only when needed: by <c>ToHdr</c> or by the first
/// geometric edit of the base image. Pass it to <see cref="ImageDecodeContext.AttachGainMap(GainMapMetadata, GainMapImage)"/>.
/// </summary>
/// <remarks>
/// The object stays attached to the loaded image and its clones, so it must own the data it needs (not the load's input
/// or context) and be safe to call from any thread, after the load has returned and more than once.
/// </remarks>
public abstract class GainMapImage
{
    /// <summary>Initializes a map of the given stored size whose <see cref="Decode"/> returns <paramref name="pixelType"/> pixels.</summary>
    /// <param name="size">The stored size of the map.</param>
    /// <param name="pixelType">
    /// <see cref="PixelType.Rgba64"/> keeps a map stored with more than 8 bits per sample at its precision (resolve it
    /// from the map's headers and <see cref="DecoderOptions.PreserveBitDepth"/>, as for the image); the default
    /// <see cref="PixelType.Rgba32"/> suits 8-bit maps.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive, or <paramref name="pixelType"/> is not defined.</exception>
    protected GainMapImage(Size size, PixelType pixelType = PixelType.Rgba32)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The gain map size must be positive.");
        }

        Size = size;
        PixelType = PixelTypes.Check(pixelType, nameof(pixelType));
    }

    /// <summary>The stored size of the map, before any orientation of the base image.</summary>
    public Size Size { get; }

    /// <summary>
    /// The pixel type <see cref="Decode"/> returns. The map is charged with it against
    /// <see cref="DecoderOptions.MaxPixels"/> when attached (a 16-bit pixel counts twice), and a decoded map of another
    /// type is rejected as corrupt.
    /// </summary>
    public PixelType PixelType { get; }

    /// <summary>
    /// Decodes the map at its stored <see cref="Size"/> into <see cref="PixelType"/> pixels. A gray map repeats its
    /// sample in R, G and B; alpha is ignored. The caller owns and disposes the result.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Maximum number of threads to use (1 = single-threaded, -1 = automatic).</param>
    /// <param name="cancellationToken">Stops the decoding.</param>
    /// <exception cref="ImageFormatException">The map data is corrupt.</exception>
    public abstract Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken);
}
