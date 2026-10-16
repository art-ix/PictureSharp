using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;

namespace PictureSharp.Processing;

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
        CheckOpacity(opacity);
        return DrawImage(source, location, opacity == 1f ? DrawImageOptions.Default : new DrawImageOptions { Opacity = opacity });
    }

    /// <summary>
    /// Draws the first frame of <paramref name="source"/> over every frame with its top-left corner at
    /// <paramref name="location"/>, blended as <paramref name="options"/> describe. Parts outside are clipped.
    /// </summary>
    /// <param name="source">Image to draw; may be the image being processed.</param>
    /// <param name="location">Position of the source's top-left corner; may be negative.</param>
    /// <param name="options">Opacity and blending space.</param>
    public ImageProcessingContext DrawImage(Image source, Point location, DrawImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ImageFrame src = source.Frames.RootFrame;
        if (options.Opacity == 0f)
        {
            return this;
        }

        Rgba32[] pixels = ReferenceEquals(source, Target) ? (Rgba32[])src.Pixels.Clone() : src.Pixels;

        foreach (ImageFrame frame in _state.WritableFrames)
        {
            if (options.BlendMode != BlendMode.Normal)
            {
                Compositor.DrawBlended(frame, pixels, src.Width, src.Height, location, options.Opacity, options.BlendMode, options.LinearLight, cancellationToken: _state.CancellationToken);
            }
            else if (options.LinearLight)
            {
                Compositor.DrawOverLinear(frame, pixels, src.Width, src.Height, location, options.Opacity, cancellationToken: _state.CancellationToken);
            }
            else
            {
                Compositor.DrawOver(frame, pixels, src.Width, src.Height, location, options.Opacity, cancellationToken: _state.CancellationToken);
            }
        }

        return this;
    }

    /// <summary>Draws the first frame of <paramref name="source"/> over every frame at <paramref name="position"/>, e.g. as a watermark.</summary>
    public ImageProcessingContext DrawImage(Image source, AnchorPosition position, float opacity = 1f)
    {
        ArgumentNullException.ThrowIfNull(source);
        return DrawImage(source, Anchor.Place(Target.Size, source.Size, position), opacity);
    }

    /// <summary>Draws the first frame of <paramref name="source"/> over every frame at <paramref name="position"/>, blended as <paramref name="options"/> describe.</summary>
    public ImageProcessingContext DrawImage(Image source, AnchorPosition position, DrawImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        return DrawImage(source, Anchor.Place(Target.Size, source.Size, position), options);
    }
}
