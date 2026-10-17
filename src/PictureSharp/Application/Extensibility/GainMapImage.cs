using System.Drawing;

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
    /// <summary>Initializes a map of the given stored size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive.</exception>
    protected GainMapImage(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The gain map size must be positive.");
        }

        Size = size;
    }

    /// <summary>The stored size of the map, before any orientation of the base image.</summary>
    public Size Size { get; }

    /// <summary>
    /// Decodes the map at its stored <see cref="Size"/>. A gray map repeats its sample in R, G and B; alpha is ignored.
    /// The caller owns and disposes the result.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Maximum number of threads to use (1 = single-threaded, -1 = automatic).</param>
    /// <param name="cancellationToken">Stops the decoding.</param>
    /// <exception cref="ImageFormatException">The map data is corrupt.</exception>
    public abstract Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken);
}
