using System.Drawing;
using PictureSharp.Algorithms.Processing;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Multiplies the alpha of every frame by the first frame of <paramref name="mask"/>, placed with its top-left corner
    /// at the image's top-left corner: <c>A' = round(A · v / 255)</c>, where v is the mask's alpha or luminance
    /// (see <see cref="MaskOptions"/>). Color channels are never changed. Parts of the mask outside the image are ignored.
    /// </summary>
    /// <param name="mask">The mask; may be the image being processed.</param>
    /// <param name="options">Mask channel, inversion and what happens outside the mask.</param>
    public ImageProcessingContext ApplyMask(Image mask, MaskOptions? options = null) => ApplyMask(mask, Point.Empty, options);

    /// <summary>
    /// Multiplies the alpha of every frame by the first frame of <paramref name="mask"/> placed with its top-left corner
    /// at <paramref name="location"/>, as <see cref="ApplyMask(Image, MaskOptions?)"/>.
    /// </summary>
    /// <param name="mask">The mask; may be the image being processed.</param>
    /// <param name="location">Position of the mask's top-left corner; may be negative.</param>
    /// <param name="options">Mask channel, inversion and what happens outside the mask.</param>
    /// <example>
    /// <code>photo.Mutate(x => x.ApplyMask(vignette, new MaskOptions { Channel = MaskChannel.Luminance }));</code>
    /// </example>
    public ImageProcessingContext ApplyMask(Image mask, Point location, MaskOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mask);
        options ??= MaskOptions.Default;
        ImageFrame source = mask.Frames.RootFrame;
        Rgba32[] pixels = ReferenceEquals(mask, Target) ? (Rgba32[])source.Pixels.Clone() : source.Pixels;
        int width = source.Width;
        int height = source.Height;
        foreach (ImageFrame frame in _state.WritableFrames)
        {
            AlphaMask.Apply(frame, pixels, width, height, location, options, cancellationToken: _state.CancellationToken);
        }

        return this;
    }

    /// <summary>Multiplies the alpha of every frame by the first frame of <paramref name="mask"/> placed at <paramref name="position"/>, as <see cref="ApplyMask(Image, MaskOptions?)"/>.</summary>
    public ImageProcessingContext ApplyMask(Image mask, AnchorPosition position, MaskOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mask);
        return ApplyMask(mask, Anchor.Place(Target.Size, mask.Size, position), options);
    }
}
