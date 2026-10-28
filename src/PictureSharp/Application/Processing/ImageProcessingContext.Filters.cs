using System.Buffers;
using System.Numerics;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Pixels;
using PictureSharp.Application.Processing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Scales the brightness: 0 gives black, 1 keeps the image, larger values brighten (as Pillow's
    /// <c>ImageEnhance.Brightness</c>). Alpha is kept.
    /// </summary>
    public ImageProcessingContext Brightness(float amount)
    {
        CheckFinite(amount, nameof(amount));
        return amount == 1f ? this : ForEachFrame(new Enhance.Kernel(EnhanceMode.Brightness, amount));
    }

    /// <summary>
    /// Scales the contrast around the mean luma: 0 gives solid gray, 1 keeps the image, larger values increase
    /// contrast (as Pillow's <c>ImageEnhance.Contrast</c>). Alpha is kept.
    /// </summary>
    public ImageProcessingContext Contrast(float amount)
    {
        CheckFinite(amount, nameof(amount));
        return amount == 1f ? this : ForEachFrame(new Enhance.Kernel(EnhanceMode.Contrast, amount));
    }

    /// <summary>
    /// Scales the color saturation: 0 gives grayscale, 1 keeps the image, larger values intensify colors
    /// (as Pillow's <c>ImageEnhance.Color</c>). Alpha is kept.
    /// </summary>
    public ImageProcessingContext Saturate(float amount)
    {
        CheckFinite(amount, nameof(amount));
        return amount == 1f ? this : ForEachFrame(new Enhance.Kernel(EnhanceMode.Saturate, amount));
    }

    /// <summary>Applies a sepia tone (the classic 0.393/0.769/0.189 color matrix); alpha is kept.</summary>
    public ImageProcessingContext Sepia() => ForEachFrame(new ColorOps.SepiaKernel());

    /// <summary>
    /// Averages each pixel with its neighbours up to <paramref name="radius"/> pixels away in each direction (a fractional
    /// radius weights the outermost pixels partially), as Pillow's <c>ImageFilter.BoxBlur</c> on premultiplied alpha.
    /// </summary>
    public ImageProcessingContext BoxBlur(float radius)
    {
        CheckRadius(radius, nameof(radius));
        return radius == 0f ? this : Spatial(new BoxBlurFilter(radius));
    }

    /// <summary>
    /// Blurs with an approximated Gaussian kernel of standard deviation <paramref name="sigma"/> (three box passes),
    /// as Pillow's <c>ImageFilter.GaussianBlur(radius=sigma)</c> on premultiplied alpha.
    /// </summary>
    public ImageProcessingContext GaussianBlur(float sigma)
    {
        CheckRadius(sigma, nameof(sigma));
        return sigma == 0f ? this : Spatial(new GaussianBlurFilter(sigma));
    }

    /// <summary>
    /// Sharpens by adding <paramref name="percent"/>% of the difference to a Gaussian blur of the given radius wherever
    /// that difference exceeds <paramref name="thresholdNormalized"/>, as Pillow's <c>ImageFilter.UnsharpMask</c>.
    /// </summary>
    /// <param name="radius">Standard deviation of the blur in pixels; finite and at least 0.</param>
    /// <param name="percent">The strength in percent; at least 0.</param>
    /// <param name="thresholdNormalized">
    /// The smallest difference that is sharpened, normalized to 0–1 of the channel range at any depth; the default
    /// 3 / 255 is Pillow's threshold of 3 levels (an 8-bit threshold t is <c>t / 255f</c>).
    /// </param>
    public ImageProcessingContext UnsharpMask(float radius = 2f, int percent = 150, float thresholdNormalized = 3f / 255f)
    {
        CheckRadius(radius, nameof(radius));
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        NormalizedLevel.Check(thresholdNormalized, nameof(thresholdNormalized));
        return Spatial(new UnsharpMaskFilter(radius, percent, thresholdNormalized));
    }

    /// <summary>Sharpens with Pillow's 3×3 <c>ImageFilter.SHARPEN</c> kernel; the outermost pixels are kept.</summary>
    public ImageProcessingContext Sharpen() => Spatial(new SharpenFilter());

    // port: Pillow src/PIL/ImageFilter.py SHARPEN (divided by its scale, 16)
    private static ReadOnlySpan<float> SharpenKernel => [-2f / 16, -2f / 16, -2f / 16, -2f / 16, 32f / 16, -2f / 16, -2f / 16, -2f / 16, -2f / 16];

    /// <summary>
    /// Runs <paramref name="filter"/> on premultiplied copies of the frames, like Pillow filtering an "RGBa" image:
    /// 8-bit frames through the 8-bit port, others on unrounded working values.
    /// </summary>
    private ImageProcessingContext Spatial<TFilter>(TFilter filter) where TFilter : struct, ISpatialFilter
    {
        Image image = Target;
        Rgba32[]? premultiplied = image.PixelType == PixelType.Rgba32 ? GC.AllocateUninitializedArray<Rgba32>(image.Width * image.Height) : null;
        _state.ReplaceFrames((filter, premultiplied), static (frame, data, token) =>
        {
            var operation = new SpatialFrame<TFilter>(frame, data.filter, data.premultiplied, token);
            return new FrameReplacement(frame.Width, frame.Height, PixelDispatch.Run<SpatialFrame<TFilter>, PixelBuffer>(frame.PixelType, ref operation));
        });
        return this;
    }

    private readonly struct SpatialFrame<TFilter>(ImageFrame frame, TFilter filter, Rgba32[]? premultiplied, CancellationToken token)
        : IPixelOperation<PixelBuffer>
        where TFilter : struct, ISpatialFilter
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            (int w, int h) = (frame.Width, frame.Height);
            if (typeof(TPixel) == typeof(Rgba32))
            {
                Premultiplied.FromStraight(frame.Buffer.As<Rgba32>(), premultiplied!);
                Rgba32[] result = filter.Run(premultiplied!, w, h, token);
                Premultiplied.ToStraight(result);
                return result;
            }

            TPixel[] source = frame.Buffer.As<TPixel>();
            Vector4[] scratch = ArrayPool<Vector4>.Shared.Rent(source.Length);
            try
            {
                Span<Vector4> plane = scratch.AsSpan(0, source.Length);
                UnitRows.Premultiply<TPixel, EncodedUnit>(source, plane);
                Vector4[] filtered = filter.RunWide(plane, w, h, token);
                var pixels = GC.AllocateUninitializedArray<TPixel>(source.Length);
                UnitRows.Unpremultiply<TPixel, EncodedUnit>(filtered, pixels);
                return PixelBuffer.From(pixels);
            }
            finally
            {
                ArrayPool<Vector4>.Shared.Return(scratch);
            }
        }
    }

    /// <summary>A filter of premultiplied pixels with an 8-bit port and a high-precision form.</summary>
    private interface ISpatialFilter
    {
        Rgba32[] Run(ReadOnlySpan<Rgba32> src, int width, int height, CancellationToken cancellationToken);

        Vector4[] RunWide(ReadOnlySpan<Vector4> src, int width, int height, CancellationToken cancellationToken);
    }

    private readonly struct BoxBlurFilter(float radius) : ISpatialFilter
    {
        public Rgba32[] Run(ReadOnlySpan<Rgba32> src, int width, int height, CancellationToken cancellationToken) =>
            Blur.BoxBlur(src, width, height, radius, radius, 1, cancellationToken);

        public Vector4[] RunWide(ReadOnlySpan<Vector4> src, int width, int height, CancellationToken cancellationToken) =>
            BlurWide.BoxBlur(src, width, height, radius, radius, 1, cancellationToken);
    }

    private readonly struct GaussianBlurFilter(float sigma) : ISpatialFilter
    {
        public Rgba32[] Run(ReadOnlySpan<Rgba32> src, int width, int height, CancellationToken cancellationToken) =>
            Blur.GaussianBlur(src, width, height, sigma, cancellationToken);

        public Vector4[] RunWide(ReadOnlySpan<Vector4> src, int width, int height, CancellationToken cancellationToken) =>
            BlurWide.GaussianBlur(src, width, height, sigma, cancellationToken);
    }

    private readonly struct UnsharpMaskFilter(float radius, int percent, float threshold) : ISpatialFilter
    {
        public Rgba32[] Run(ReadOnlySpan<Rgba32> src, int width, int height, CancellationToken cancellationToken) =>
            Blur.UnsharpMask(src, width, height, radius, percent, NormalizedLevel.Threshold(threshold, byte.MaxValue), cancellationToken);

        public Vector4[] RunWide(ReadOnlySpan<Vector4> src, int width, int height, CancellationToken cancellationToken) =>
            BlurWide.UnsharpMask(src, width, height, radius, percent, threshold, cancellationToken);
    }

    private readonly struct SharpenFilter : ISpatialFilter
    {
        public Rgba32[] Run(ReadOnlySpan<Rgba32> src, int width, int height, CancellationToken cancellationToken) =>
            Blur.Filter3x3(src, width, height, SharpenKernel, 0f, cancellationToken);

        public Vector4[] RunWide(ReadOnlySpan<Vector4> src, int width, int height, CancellationToken cancellationToken) =>
            BlurWide.Filter3x3(src, width, height, SharpenKernel, cancellationToken);
    }

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
