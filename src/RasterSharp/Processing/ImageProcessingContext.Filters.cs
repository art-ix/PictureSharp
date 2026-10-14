using RasterSharp.Processing;

namespace RasterSharp;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Scales the brightness: 0 gives black, 1 keeps the image, larger values brighten (as Pillow's
    /// <c>ImageEnhance.Brightness</c>). Alpha is kept.
    /// </summary>
    public ImageProcessingContext Brightness(float amount)
    {
        CheckFinite(amount, nameof(amount));
        return amount == 1f ? this : ForEachPixelSpan(pixels => Enhance.Brightness(pixels, amount));
    }

    /// <summary>
    /// Scales the contrast around the mean luma: 0 gives solid gray, 1 keeps the image, larger values increase
    /// contrast (as Pillow's <c>ImageEnhance.Contrast</c>). Alpha is kept.
    /// </summary>
    public ImageProcessingContext Contrast(float amount)
    {
        CheckFinite(amount, nameof(amount));
        return amount == 1f ? this : ForEachPixelSpan(pixels => Enhance.Contrast(pixels, amount));
    }

    /// <summary>
    /// Scales the color saturation: 0 gives grayscale, 1 keeps the image, larger values intensify colors
    /// (as Pillow's <c>ImageEnhance.Color</c>). Alpha is kept.
    /// </summary>
    public ImageProcessingContext Saturate(float amount)
    {
        CheckFinite(amount, nameof(amount));
        return amount == 1f ? this : ForEachPixelSpan(pixels => Enhance.Saturate(pixels, amount));
    }

    /// <summary>Applies a sepia tone (the classic 0.393/0.769/0.189 color matrix); alpha is kept.</summary>
    public ImageProcessingContext Sepia() => ForEachPixelSpan(ColorOps.Sepia);

    /// <summary>
    /// Averages each pixel with its neighbours up to <paramref name="radius"/> pixels away in each direction (a fractional
    /// radius weights the outermost pixels partially), as Pillow's <c>ImageFilter.BoxBlur</c> on premultiplied alpha.
    /// </summary>
    public ImageProcessingContext BoxBlur(float radius)
    {
        CheckRadius(radius, nameof(radius));
        return radius == 0f ? this : Spatial((src, w, h) => Blur.BoxBlur(src, w, h, radius, radius, 1));
    }

    /// <summary>
    /// Blurs with an approximated Gaussian kernel of standard deviation <paramref name="sigma"/> (three box passes),
    /// as Pillow's <c>ImageFilter.GaussianBlur(radius=sigma)</c> on premultiplied alpha.
    /// </summary>
    public ImageProcessingContext GaussianBlur(float sigma)
    {
        CheckRadius(sigma, nameof(sigma));
        return sigma == 0f ? this : Spatial((src, w, h) => Blur.GaussianBlur(src, w, h, sigma));
    }

    /// <summary>
    /// Sharpens by adding <paramref name="percent"/>% of the difference to a Gaussian blur of the given radius wherever
    /// that difference exceeds <paramref name="threshold"/>, as Pillow's <c>ImageFilter.UnsharpMask</c>.
    /// </summary>
    public ImageProcessingContext UnsharpMask(float radius = 2f, int percent = 150, int threshold = 3)
    {
        CheckRadius(radius, nameof(radius));
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        return Spatial((src, w, h) => Blur.UnsharpMask(src, w, h, radius, percent, threshold));
    }

    /// <summary>Sharpens with Pillow's 3×3 <c>ImageFilter.SHARPEN</c> kernel; the outermost pixels are kept.</summary>
    public ImageProcessingContext Sharpen() => Spatial(static (src, w, h) => Blur.Filter3x3(src, w, h, SharpenKernel, 0f));

    /// <summary>
    /// Rotates clockwise by <paramref name="degrees"/>, as Pillow's <c>Image.rotate(-degrees)</c>. With
    /// <paramref name="expand"/> the canvas grows to hold the whole image, otherwise the corners are cut off;
    /// uncovered areas are filled with <paramref name="background"/>. Multiples of 90° are exact.
    /// </summary>
    /// <param name="degrees">Clockwise angle.</param>
    /// <param name="filter"><see cref="ResamplingFilter.Nearest"/>, <see cref="ResamplingFilter.Bilinear"/> or <see cref="ResamplingFilter.Bicubic"/>.</param>
    /// <param name="background">Fill for the uncovered corners. Default: transparent black.</param>
    /// <param name="expand">Whether the canvas grows to fit the rotated image. Default: true.</param>
    public ImageProcessingContext Rotate(float degrees, ResamplingFilter filter = ResamplingFilter.Bicubic, Rgba32 background = default, bool expand = true)
    {
        CheckFinite(degrees, nameof(degrees));
        if (filter is not (ResamplingFilter.Nearest or ResamplingFilter.Bilinear or ResamplingFilter.Bicubic))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter, "Rotation supports the Nearest, Bilinear and Bicubic filters.");
        }

        // Pillow rotates counter-clockwise; angle % 360.0 follows Python's sign rules.
        double angle = -(double)degrees % 360.0;
        if (angle < 0)
        {
            angle += 360.0;
        }

        Image image = Target;
        (int w, int h) = (image.Width, image.Height);
        if (angle == 0)
        {
            return this;
        }

        if (angle == 180)
        {
            return RotateFlip(2, false);
        }

        if (angle is 90 or 270 && (expand || w == h))
        {
            return RotateFlip(angle == 90 ? 3 : 1, false);
        }

        (double[] matrix, int width, int height) = Affine.RotationMatrix(w, h, angle, expand);
        ImageFrame.ValidateSize(width, height);
        bool premultiplied = filter != ResamplingFilter.Nearest;
        Rgba32 fill = background;
        if (premultiplied)
        {
            Span<Rgba32> one = [background];
            Premultiplied.FromStraight(one, one);
            fill = one[0];
        }

        Rgba32[]? scratch = premultiplied ? GC.AllocateUninitializedArray<Rgba32>(w * h) : null;
        foreach (ImageFrame frame in _state.Frames)
        {
            var output = GC.AllocateUninitializedArray<Rgba32>(width * height);
            output.AsSpan().Fill(fill);
            if (scratch is null)
            {
                Affine.TransformNearest(frame.Pixels, w, h, output, width, height, matrix);
            }
            else
            {
                Premultiplied.FromStraight(frame.Pixels, scratch);
                Affine.TransformFiltered(scratch, w, h, output, width, height, matrix, filter);
                Premultiplied.ToStraight(output);
            }

            frame.Replace(width, height, output);
        }

        _state.FramesReplaced();
        image.HasAlpha |= background.A != 255;
        return this;
    }

    // port: Pillow src/PIL/ImageFilter.py SHARPEN (divided by its scale, 16)
    private static ReadOnlySpan<float> SharpenKernel => [-2f / 16, -2f / 16, -2f / 16, -2f / 16, 32f / 16, -2f / 16, -2f / 16, -2f / 16, -2f / 16];

    /// <summary>Runs <paramref name="filter"/> on premultiplied copies of the frames, like Pillow filtering an "RGBa" image.</summary>
    private ImageProcessingContext Spatial(SpatialFilter filter)
    {
        Image image = Target;
        (int w, int h) = (image.Width, image.Height);
        Rgba32[] premultiplied = GC.AllocateUninitializedArray<Rgba32>(w * h);
        foreach (ImageFrame frame in _state.Frames)
        {
            Premultiplied.FromStraight(frame.Pixels, premultiplied);
            Rgba32[] result = filter(premultiplied, w, h);
            Premultiplied.ToStraight(result);
            frame.Replace(w, h, result);
        }

        _state.FramesReplaced();
        return this;
    }

    private delegate Rgba32[] SpatialFilter(ReadOnlySpan<Rgba32> src, int width, int height);

    private static void CheckFinite(float value, string paramName)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Expected a finite number.");
        }
    }

    private static void CheckRadius(float value, string paramName)
    {
        if (!(value >= 0f) || !float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Expected a finite number >= 0.");
        }
    }
}
