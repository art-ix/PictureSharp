using System.Drawing;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Application.ColorManagement;
using PictureSharp.Domain.Frames;
using PictureSharp.Processing;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Converts every frame to sRGB from the image's ICC profile or PNG color description (<see cref="ImageMetadata.Gamma"/>,
    /// <see cref="ImageMetadata.Chromaticities"/>), then removes them, so the result is not converted again. An ICC
    /// profile takes precedence, then <see cref="ImageMetadata.SrgbRenderingIntent"/> (already sRGB), then gamma and
    /// chromaticities; an image without any is already sRGB and is left unchanged.
    /// </summary>
    /// <remarks>
    /// RGB and Gray matrix/TRC profiles of ICC v2 and v4 are supported, with the relative colorimetric intent and
    /// without black-point compensation; colors outside sRGB are clipped and alpha is kept.
    /// </remarks>
    /// <exception cref="NotSupportedException">The profile is LUT-based, CMYK, Lab or otherwise not matrix/TRC.</exception>
    /// <exception cref="ImageFormatException">The ICC profile is malformed.</exception>
    public ImageProcessingContext ConvertToSrgb()
    {
        ImageMetadata metadata = Target.Metadata;
        if (ColorDescriptionResolver.ToSrgb(metadata, Target.SourceFormat) is { } conversion)
        {
            // New buffers are committed together, so a canceled conversion leaves pixels and profile consistent.
            _state.ReplaceFrames(conversion, static (frame, conversion, token) =>
            {
                ReadOnlySpan<Rgba32> source = frame.GetPixelSpan();
                Rgba32[] pixels = GC.AllocateUninitializedArray<Rgba32>(source.Length);
                const int band = 1 << 16;
                for (int start = 0; start < source.Length; start += band)
                {
                    token.ThrowIfCancellationRequested();
                    int length = Math.Min(band, source.Length - start);
                    conversion.Convert(source.Slice(start, length), pixels.AsSpan(start, length));
                }

                return new FrameReplacement(frame.Width, frame.Height, pixels);
            });
        }

        metadata.IccProfile = null;
        metadata.ClearColorDescription();
        return this;
    }

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
