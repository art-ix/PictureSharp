using System.Drawing;
using PictureSharp.Processing;

namespace PictureSharp;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>Converts to grayscale (BT.601 luma, as Pillow's "L" mode); alpha is kept.</summary>
    public ImageProcessingContext Grayscale() => ForEachPixelSpan(ColorOps.Grayscale);

    /// <summary>Inverts the color channels; alpha is kept.</summary>
    public ImageProcessingContext Invert() => ForEachPixelSpan(ColorOps.Invert);

    /// <summary>Overwrites every pixel with <paramref name="color"/> (no blending).</summary>
    public ImageProcessingContext Fill(Rgba32 color)
    {
        ForEachPixelSpan(color, static (pixels, value) => pixels.Fill(value));

        Target.HasAlpha = color.A != 255;
        return this;
    }

    /// <summary>Overwrites the pixels inside <paramref name="region"/> with <paramref name="color"/> (no blending), clipped to the image.</summary>
    public ImageProcessingContext Fill(Rgba32 color, Rectangle region)
    {
        region.Intersect(Target.Bounds);
        if (region.IsEmpty)
        {
            return this;
        }

        foreach (ImageFrame frame in _state.WritableFrames)
        {
            ColorOps.Fill(frame, region, color);
        }

        Target.HasAlpha |= color.A != 255;
        return this;
    }

    /// <summary>Sets alpha to 0 on every pixel equal to <paramref name="color"/> (all four channels); their color channels are kept.</summary>
    public ImageProcessingContext MakeTransparent(Rgba32 color)
    {
        ForEachPixelSpan(color, static (pixels, value) => ColorOps.MakeTransparent(pixels, value));

        Target.HasAlpha = true;
        return this;
    }

    /// <summary>
    /// Composites the image over <paramref name="color"/> (as Pillow's <c>Image.alpha_composite</c>), removing
    /// transparency when the color is opaque.
    /// </summary>
    public ImageProcessingContext BackgroundColor(Rgba32 color)
    {
        ForEachPixelSpan(color, static (pixels, value) => ColorOps.BackgroundColor(pixels, value));

        if (color.A == 255)
        {
            Target.HasAlpha = false;
        }

        return this;
    }

    /// <summary>Multiplies the alpha of every pixel by <paramref name="opacity"/> (0–1).</summary>
    public ImageProcessingContext Opacity(float opacity)
    {
        CheckOpacity(opacity);
        if (opacity == 1f)
        {
            return this;
        }

        ForEachPixelSpan(opacity, static (pixels, value) => ColorOps.Opacity(pixels, value));

        Target.HasAlpha = true;
        return this;
    }

    private static void CheckOpacity(float opacity)
    {
        if (!(opacity >= 0f && opacity <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be between 0 and 1.");
        }
    }
}
