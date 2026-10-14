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
        return amount == 1f ? this : ForEachPixelSpan(amount, static (pixels, value) => Enhance.Brightness(pixels, value));
    }

    /// <summary>
    /// Scales the contrast around the mean luma: 0 gives solid gray, 1 keeps the image, larger values increase
    /// contrast (as Pillow's <c>ImageEnhance.Contrast</c>). Alpha is kept.
    /// </summary>
    public ImageProcessingContext Contrast(float amount)
    {
        CheckFinite(amount, nameof(amount));
        return amount == 1f ? this : ForEachPixelSpan(amount, static (pixels, value) => Enhance.Contrast(pixels, value));
    }

    /// <summary>
    /// Scales the color saturation: 0 gives grayscale, 1 keeps the image, larger values intensify colors
    /// (as Pillow's <c>ImageEnhance.Color</c>). Alpha is kept.
    /// </summary>
    public ImageProcessingContext Saturate(float amount)
    {
        CheckFinite(amount, nameof(amount));
        return amount == 1f ? this : ForEachPixelSpan(amount, static (pixels, value) => Enhance.Saturate(pixels, value));
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
        return radius == 0f ? this : Spatial(radius, static (src, w, h, value) => Blur.BoxBlur(src, w, h, value, value, 1));
    }

    /// <summary>
    /// Blurs with an approximated Gaussian kernel of standard deviation <paramref name="sigma"/> (three box passes),
    /// as Pillow's <c>ImageFilter.GaussianBlur(radius=sigma)</c> on premultiplied alpha.
    /// </summary>
    public ImageProcessingContext GaussianBlur(float sigma)
    {
        CheckRadius(sigma, nameof(sigma));
        return sigma == 0f ? this : Spatial(sigma, static (src, w, h, value) => Blur.GaussianBlur(src, w, h, value));
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
        return Spatial((radius, percent, threshold), static (src, w, h, state) => Blur.UnsharpMask(src, w, h, state.radius, state.percent, state.threshold));
    }

    /// <summary>Sharpens with Pillow's 3×3 <c>ImageFilter.SHARPEN</c> kernel; the outermost pixels are kept.</summary>
    public ImageProcessingContext Sharpen() => Spatial(0f, static (src, w, h, offset) => Blur.Filter3x3(src, w, h, SharpenKernel, offset));

    // port: Pillow src/PIL/ImageFilter.py SHARPEN (divided by its scale, 16)
    private static ReadOnlySpan<float> SharpenKernel => [-2f / 16, -2f / 16, -2f / 16, -2f / 16, 32f / 16, -2f / 16, -2f / 16, -2f / 16, -2f / 16];

    /// <summary>Runs <paramref name="filter"/> on premultiplied copies of the frames, like Pillow filtering an "RGBa" image.</summary>
    private ImageProcessingContext Spatial<TState>(TState state, SpatialFilter<TState> filter)
    {
        Image image = Target;
        (int w, int h) = (image.Width, image.Height);
        Rgba32[] premultiplied = GC.AllocateUninitializedArray<Rgba32>(w * h);
        foreach (ImageFrame frame in _state.Frames)
        {
            Premultiplied.FromStraight(frame.Pixels, premultiplied);
            Rgba32[] result = filter(premultiplied, w, h, state);
            Premultiplied.ToStraight(result);
            frame.Replace(w, h, result);
        }

        _state.FramesReplaced();
        return this;
    }

    private delegate Rgba32[] SpatialFilter<TState>(ReadOnlySpan<Rgba32> src, int width, int height, TState state);

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
