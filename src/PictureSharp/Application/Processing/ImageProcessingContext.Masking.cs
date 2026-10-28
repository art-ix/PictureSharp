using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Multiplies the alpha of every frame by the first frame of <paramref name="mask"/>, placed with its top-left corner
    /// at the image's top-left corner: <c>A' = round(A · v / 255)</c>, where v is the mask's alpha or luminance
    /// (see <see cref="MaskOptions"/>); with a 16-bit mask v is read at 16 bits and divided by 65535, also on an 8-bit
    /// image. Color channels are never changed. Parts of the mask outside the image are ignored.
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
        PixelBuffer pixels = ReferenceEquals(mask, Target) ? source.Buffer.Clone() : source.Buffer;
        foreach (ImageFrame frame in _state.WritableFrames)
        {
            var operation = new AlphaMask.ApplyFrame(frame, pixels, source.Width, source.Height, location, options, _state.CancellationToken);
            PixelDispatch.Run<AlphaMask.ApplyFrame, bool>(frame.PixelType, ref operation);
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
