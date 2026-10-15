using System.Drawing;
using PictureSharp.Processing;

namespace PictureSharp;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Draws the first frame of <paramref name="source"/> over every frame (source-over, as Pillow's
    /// <c>Image.alpha_composite</c>) with its top-left corner at <paramref name="location"/>. Parts outside are clipped.
    /// </summary>
    /// <param name="source">Image to draw; may be the image being processed.</param>
    /// <param name="location">Position of the source's top-left corner; may be negative.</param>
    /// <param name="opacity">0–1, multiplies the source alpha.</param>
    public ImageProcessingContext DrawImage(Image source, Point location, float opacity = 1f)
    {
        ArgumentNullException.ThrowIfNull(source);
        CheckOpacity(opacity);
        ImageFrame src = source.Frames.RootFrame;
        Rgba32[] pixels = ReferenceEquals(source, Target) ? (Rgba32[])src.Pixels.Clone() : src.Pixels;
        if (opacity == 0f)
        {
            return this;
        }

        foreach (ImageFrame frame in _state.WritableFrames)
        {
            Compositor.DrawOver(frame, pixels, src.Width, src.Height, location, opacity);
        }

        return this;
    }

    /// <summary>Draws the first frame of <paramref name="source"/> over every frame at <paramref name="position"/>, e.g. as a watermark.</summary>
    public ImageProcessingContext DrawImage(Image source, AnchorPosition position, float opacity = 1f)
    {
        ArgumentNullException.ThrowIfNull(source);
        return DrawImage(source, Anchor.Place(Target.Size, source.Size, position), opacity);
    }
}
