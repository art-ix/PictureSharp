using System.Drawing;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Application.ColorManagement;
using PictureSharp.Domain.Frames;
using PictureSharp.Primitives.Parallelism;
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
                Rgba32[] pixels = GC.AllocateUninitializedArray<Rgba32>(frame.Width * frame.Height);
                ConvertBands(conversion, pixels.Length, frame.Pixels, pixels, token);
                return new FrameReplacement(frame.Width, frame.Height, pixels);
            });
        }

        metadata.IccProfile = null;
        metadata.ClearColorDescription();
        return this;
    }

    /// <summary>
    /// <see cref="ConvertToSrgb"/> converting the pixels in place. Only for an image that nobody else sees until it
    /// completes, such as one being decoded: a canceled conversion leaves it half converted.
    /// </summary>
    internal ImageProcessingContext ConvertToSrgbInPlace()
    {
        ImageMetadata metadata = Target.Metadata;
        if (ColorDescriptionResolver.ToSrgb(metadata, Target.SourceFormat) is { } conversion)
        {
            foreach (ImageFrame frame in _state.WritableFrames)
            {
                ConvertBands(conversion, frame.Width * frame.Height, frame.Pixels, frame.Pixels, _state.CancellationToken);
            }
        }

        metadata.IccProfile = null;
        metadata.ClearColorDescription();
        return this;
    }

    // Pixels convert independently, so bands of them may run on several threads (like Resize).
    private static void ConvertBands(SrgbConversion conversion, int count, Rgba32[] source, Rgba32[] target, CancellationToken token)
    {
        const int band = 1 << 16;
        int bands = (count + band - 1) / band;
        if (Parallelism.IsWorthwhile(-1, count, 1 << 18))
        {
            Parallel.For(0, bands, Parallelism.Options(-1, token), ConvertBand);
        }
        else
        {
            for (int i = 0; i < bands; i++)
            {
                ConvertBand(i);
            }
        }

        void ConvertBand(int index)
        {
            token.ThrowIfCancellationRequested();
            int start = index * band, length = Math.Min(band, count - start);
            conversion.Convert(source.AsSpan(start, length), target.AsSpan(start, length));
        }
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
