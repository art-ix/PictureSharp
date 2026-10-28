using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

/// <summary>
/// 0.9.0 step 2: kernels and composition on 16-bit images, against the double-precision <see cref="WideReference"/>
/// (errors in 16-bit codes), the 8-bit path after rounding, full ramps and thread-count independence.
/// </summary>
public class HighBitDepthProcessingTests
{
    public enum Alpha
    {
        Opaque,
        Varying,
        // Constant 7/65535: well conditioned, but 8-bit premultiplication would erase every color.
        Low,
    }

    /// <summary>A smooth gradient with noise in every channel, deterministic per seed.</summary>
    internal static Rgba64[] Pixels(int width, int height, Alpha alpha, int seed = 1)
    {
        var random = new Random(seed);
        var pixels = new Rgba64[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ushort Channel(double phase) =>
                    (ushort)Math.Clamp((32767 * (1 + Math.Sin((x * 0.31) + (y * 0.17) + phase))) + random.Next(-6000, 6000), 0, 65535);
                ushort a = alpha switch
                {
                    Alpha.Opaque => ushort.MaxValue,
                    Alpha.Low => 7,
                    _ => random.Next(8) == 0 ? (ushort)0 : (ushort)random.Next(1, 65536),
                };
                pixels[(y * width) + x] = new Rgba64(Channel(0), Channel(2), Channel(4), a);
            }
        }

        return pixels;
    }

    internal static Image Image16(int width, int height, Alpha alpha, int seed = 1) =>
        Image.LoadPixelData<Rgba64>(Pixels(width, height, alpha, seed), width, height);

    private readonly record struct Error(int MaxAlpha, double MaxColor, double RmsColor)
    {
        public override string ToString() => $"alpha max {MaxAlpha}, color max {MaxColor:F3}, RMS {RmsColor:F4} (16-bit codes, weighted by alpha)";
    }

    /// <summary>
    /// The alpha error and the color error weighted by the reference alpha: the color of a nearly transparent pixel
    /// may be ill-conditioned (alpha from cancelling taps) without changing what it contributes.
    /// </summary>
    private static Error Measure(ReadOnlySpan<Rgba64> actual, ReadOnlySpan<Rgba64> expected)
    {
        int maxAlpha = 0;
        double maxColor = 0, sum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Rgba64 a = actual[i], e = expected[i];
            maxAlpha = Math.Max(maxAlpha, Math.Abs(a.A - e.A));
            double weight = e.A / 65535.0;
            foreach (int d in new[] { a.R - e.R, a.G - e.G, a.B - e.B })
            {
                double weighted = Math.Abs(d) * weight;
                maxColor = Math.Max(maxColor, weighted);
                sum += weighted * weighted;
            }
        }

        return new(maxAlpha, maxColor, Math.Sqrt(sum / (expected.Length * 3)));
    }

    private static async Task AssertClose(Rgba64[] actual, Rgba64[] expected, double maxColor = 1, double rms = 0.25)
    {
        Error error = Measure(actual, expected);
        Console.WriteLine(error);
        await Assert.That(error.MaxAlpha).IsLessThanOrEqualTo(1);
        await Assert.That(error.MaxColor).IsLessThanOrEqualTo(maxColor);
        await Assert.That(error.RmsColor).IsLessThanOrEqualTo(rms);
    }

    /// <summary>For uniform low alpha the colors are well conditioned, so they must match without weighting.</summary>
    private static async Task AssertLowAlphaColors(Rgba64[] actual, Rgba64[] expected)
    {
        int max = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Rgba64 a = actual[i], e = expected[i];
            max = Math.Max(max, Math.Max(Math.Abs(a.R - e.R), Math.Max(Math.Abs(a.G - e.G), Math.Abs(a.B - e.B))));
        }

        Console.WriteLine($"low alpha: color max {max}");
        await Assert.That(max).IsLessThanOrEqualTo(1);
    }

    private static Rgba64[] Result(Image image, Action<ImageProcessingContext> operation)
    {
        using Image result = image.Clone(x => operation(x));
        return result.GetPixelSpan<Rgba64>().ToArray();
    }

    // ------------------------------------------------------------ kernels against the reference

    public static IEnumerable<(ResamplingFilter Filter, bool Linear, int SrcWidth, int SrcHeight, int Width, int Height, Alpha Alpha)> ResizeCases()
    {
        foreach (Alpha alpha in new[] { Alpha.Varying, Alpha.Low })
        {
            // A 44× reduction: 269 taps per output pixel.
            yield return (ResamplingFilter.Lanczos3, false, 400, 12, 9, 12, alpha);
            yield return (ResamplingFilter.Lanczos3, false, 48, 40, 31, 17, alpha);
            yield return (ResamplingFilter.Bicubic, false, 13, 11, 50, 37, alpha);
            yield return (ResamplingFilter.Bilinear, false, 64, 48, 23, 17, alpha);
            yield return (ResamplingFilter.Box, false, 40, 30, 17, 30, alpha);
            yield return (ResamplingFilter.Lanczos3, true, 60, 40, 25, 17, alpha);
            yield return (ResamplingFilter.Bicubic, true, 13, 11, 40, 29, alpha);
        }
    }

    [Test]
    [MethodDataSource(nameof(ResizeCases))]
    public async Task Resize_MatchesReference(ResamplingFilter filter, bool linear, int srcWidth, int srcHeight, int width, int height, Alpha alpha)
    {
        using Image image = Image16(srcWidth, srcHeight, alpha);
        Rgba64[] actual = Result(image, x => x.Resize(new ResizeOptions { Size = new(width, height), Filter = filter, LinearLight = linear }));
        Rgba64[] expected = WideReference.Resize(image.GetPixelSpan<Rgba64>().ToArray(), srcWidth, srcHeight, width, height, filter, linear);
        await AssertClose(actual, expected);
        if (alpha == Alpha.Low)
        {
            await AssertLowAlphaColors(actual, expected);
        }
    }

    [Test]
    public async Task Resize_Nearest_CopiesSamples()
    {
        using Image image = Image16(20, 10, Alpha.Varying);
        Rgba64[] source = image.GetPixelSpan<Rgba64>().ToArray();
        Rgba64[] actual = Result(image, x => x.Resize(10, 5, ResamplingFilter.Nearest));
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                await Assert.That(actual[(y * 10) + x]).IsEqualTo(source[(((y * 2) + 1) * 20) + (x * 2) + 1]);
            }
        }
    }

    [Test]
    public async Task Resize_PadsWithAHighPrecisionColor()
    {
        using Image image = Image16(8, 4, Alpha.Opaque);
        var pad = new Rgba64(1, 30000, 65534, 12345);
        using Image result = image.Clone(x => x.Resize(new ResizeOptions { Size = new(8, 8), Mode = ResizeMode.Pad, PadColor = pad }));
        await Assert.That(result.GetPixel<Rgba64>(0, 0)).IsEqualTo(pad);
        await Assert.That(result.HasAlpha).IsTrue();
    }

    public static IEnumerable<(string Name, Alpha Alpha)> BlurCases()
    {
        foreach (Alpha alpha in new[] { Alpha.Varying, Alpha.Low })
        {
            foreach (string name in new[] { "gauss0.7", "gauss3.3", "gauss25", "box2.5", "box0.4", "unsharp", "sharpen" })
            {
                yield return (name, alpha);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(BlurCases))]
    public async Task Blur_MatchesReference(string name, Alpha alpha)
    {
        // Sigma 25 covers the whole width several times over: a long kernel with edge clamping.
        (int w, int h) = name == "gauss25" ? (120, 24) : (48, 40);
        using Image image = Image16(w, h, alpha);
        double[] v = WideReference.Premultiply(image.GetPixelSpan<Rgba64>());
        (Action<ImageProcessingContext> operation, double[] expected) = name switch
        {
            "gauss0.7" => ((Action<ImageProcessingContext>)(x => x.GaussianBlur(0.7f)), WideReference.GaussianBlur(v, w, h, 0.7f)),
            "gauss3.3" => (x => x.GaussianBlur(3.3f), WideReference.GaussianBlur(v, w, h, 3.3f)),
            "gauss25" => (x => x.GaussianBlur(25f), WideReference.GaussianBlur(v, w, h, 25f)),
            "box2.5" => (x => x.BoxBlur(2.5f), WideReference.BoxBlur(v, w, h, 2.5f, 1)),
            "box0.4" => (x => x.BoxBlur(0.4f), WideReference.BoxBlur(v, w, h, 0.4f, 1)),
            "unsharp" => (x => x.UnsharpMask(2f, 150, 3 / 255f), WideReference.UnsharpMask(v, w, h, 2f, 150, 3 / 255f)),
            _ => (x => x.Sharpen(), WideReference.Sharpen(v, w, h)),
        };

        Rgba64[] actual = Result(image, operation);
        Rgba64[] reference = WideReference.Unpremultiply(expected);
        await AssertClose(actual, reference);
        if (alpha == Alpha.Low)
        {
            await AssertLowAlphaColors(actual, reference);
        }
    }

    [Test]
    [Arguments(ResamplingFilter.Bicubic, true)]
    [Arguments(ResamplingFilter.Bicubic, false)]
    [Arguments(ResamplingFilter.Bilinear, true)]
    public async Task Rotate_MatchesReference(ResamplingFilter filter, bool expand)
    {
        using Image image = Image16(37, 29, Alpha.Varying);
        var background = new Rgba64(1000, 2000, 3000, 40000);
        Rgba64[] actual = Result(image, x => x.Rotate(17f, filter, background, expand));
        (double[] m, int w, int h) = Affine.RotationMatrix(37, 29, 343, expand);
        Rgba64[] expected = WideReference.Transform(image.GetPixelSpan<Rgba64>().ToArray(), 37, 29, w, h, m, filter == ResamplingFilter.Bicubic, background);
        await AssertClose(actual, expected);
    }

    [Test]
    public async Task Rotate_Nearest_CopiesSamplesAndFillsTheBackground()
    {
        using Image image = Image16(10, 10, Alpha.Varying);
        Rgba64[] source = image.GetPixelSpan<Rgba64>().ToArray();
        var background = new Rgba64(1, 2, 3, 4);
        using Image result = image.Clone(x => x.Rotate(45f, ResamplingFilter.Nearest, background));
        Rgba64[] pixels = result.GetPixelSpan<Rgba64>().ToArray();
        await Assert.That(pixels[0]).IsEqualTo(background);
        await Assert.That(pixels.Where(p => p != background).All(p => source.Contains(p))).IsTrue();
    }

    [Test]
    [Arguments(Alpha.Opaque)]
    [Arguments(Alpha.Varying)]
    public async Task Vignette_MatchesReference(Alpha alpha)
    {
        const int w = 41, h = 23;
        using Image image = Image16(w, h, alpha);
        var options = new VignetteOptions { Amount = 0.8f, Midpoint = 0.3f, Feather = 0.7f };
        Rgba64[] actual = Result(image, x => x.Vignette(options));
        Rgba64[] source = image.GetPixelSpan<Rgba64>().ToArray();
        var expected = new Rgba64[source.Length];
        double inner = 0.3, outer = 0.3 + (0.7 * 0.7), radius = 0.5 * Math.Sqrt((w * w) + (h * h));
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double d = Math.Sqrt(Math.Pow(x - ((w - 1) / 2.0), 2) + Math.Pow(y - ((h - 1) / 2.0), 2)) / radius;
                double t = Math.Clamp((d - inner) / (outer - inner), 0, 1);
                double gain = Math.Pow(2, -2 * 0.8 * t * t * (3 - (2 * t)));
                Rgba64 p = source[(y * w) + x];
                ushort C(ushort v) => WideReference.Round16(WideReference.Encode(WideReference.Decode(v / 65535.0) * gain));
                expected[(y * w) + x] = new Rgba64(C(p.R), C(p.G), C(p.B), p.A);
            }
        }

        await AssertClose(actual, expected);
    }

    // ------------------------------------------------------------ composition

    public static IEnumerable<(BlendMode Mode, bool Linear, PixelType Destination, PixelType Source)> DrawCases()
    {
        foreach (BlendMode mode in new[] { BlendMode.Normal, BlendMode.Multiply, BlendMode.SoftLight, BlendMode.Difference })
        {
            foreach (bool linear in new[] { false, true })
            {
                yield return (mode, linear, PixelType.Rgba64, PixelType.Rgba64);
                yield return (mode, linear, PixelType.Rgba64, PixelType.Rgba32);
                yield return (mode, linear, PixelType.Rgba32, PixelType.Rgba64);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(DrawCases))]
    public async Task DrawImage_MatchesReference(BlendMode mode, bool linear, PixelType destination, PixelType source)
    {
        const int w = 30, h = 20;
        using Image dst = Depth(Image16(w, h, Alpha.Varying, seed: 2), destination);
        using Image src = Depth(Image16(w - 7, h - 5, Alpha.Varying, seed: 3), source);
        var at = new Point(4, -2);
        const float opacity = 0.7f;
        using Image result = dst.Clone(x => x.DrawImage(src, at, new DrawImageOptions { Opacity = opacity, BlendMode = mode, LinearLight = linear }));
        await Assert.That(result.PixelType).IsEqualTo(destination);

        int max = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba64 d = Wide(dst, x, y);
                int sx = x - at.X, sy = y - at.Y;
                Rgba64 expected = d;
                if (sx >= 0 && sy >= 0 && sx < src.Width && sy < src.Height && Wide(src, sx, sy).A != 0)
                {
                    var blended = WideReference.Blend(mode, Unit(d, linear), Unit(Wide(src, sx, sy), linear), opacity);
                    expected = Store(blended, linear, destination);
                }

                Rgba64 actual = Wide(result, x, y);
                // Colors under a fully transparent result carry no information.
                max = Math.Max(max, expected.A == 0 ? Math.Abs(actual.A - expected.A) : MaxDifference(actual, expected));
            }
        }

        Console.WriteLine($"max {max} codes at the destination depth");
        await Assert.That(max).IsLessThanOrEqualTo(destination == PixelType.Rgba64 ? 1 : 257);
    }

    private static Image Depth(Image image, PixelType type)
    {
        if (type == PixelType.Rgba32)
        {
            image.Mutate(x => x.ConvertPixelType(PixelType.Rgba32));
        }

        return image;
    }

    private static Rgba64 Wide(Image image, int x, int y) =>
        image.PixelType == PixelType.Rgba64 ? image.GetPixel<Rgba64>(x, y) : image.GetPixel<Rgba32>(x, y);

    private static (double, double, double, double) Unit(Rgba64 p, bool linear)
    {
        double C(ushort v) => linear ? WideReference.Decode(v / 65535.0) : v / 65535.0;
        return (C(p.R), C(p.G), C(p.B), p.A / 65535.0);
    }

    // The reference result rounded once at the destination depth, returned widened.
    private static Rgba64 Store((double R, double G, double B, double A) v, bool linear, PixelType depth)
    {
        double C(double c) => linear ? WideReference.Encode(c) : c;
        return depth == PixelType.Rgba64
            ? new Rgba64(WideReference.Round16(C(v.R)), WideReference.Round16(C(v.G)), WideReference.Round16(C(v.B)), WideReference.Round16(v.A))
            : new Rgba32(WideReference.Round8(C(v.R)), WideReference.Round8(C(v.G)), WideReference.Round8(C(v.B)), WideReference.Round8(v.A));
    }

    private static int MaxDifference(Rgba64 x, Rgba64 y) =>
        Math.Max(Math.Max(Math.Abs(x.R - y.R), Math.Abs(x.G - y.G)), Math.Max(Math.Abs(x.B - y.B), Math.Abs(x.A - y.A)));

    [Test]
    public async Task DrawImage_16BitSourceOn8BitTarget_RoundsOnlyAtTheWrite()
    {
        // 100.5 8-bit levels of red at half alpha: rounding the source first would give 100 or 101 before blending.
        using Image dst = new(1, 1);
        dst.Mutate(x => x.Fill(new Rgba32(0, 0, 0)));
        using Image src = Image.LoadPixelData<Rgba64>([new Rgba64(25829, 0, 0, 32768)], 1, 1);
        dst.Mutate(x => x.DrawImage(src, Point.Empty));
        // 25829 / 65535 · 32768 / 65535 · 255 = 50.25 → 50; with the source rounded to (101, 0, 0, 128): 50.7 → 51.
        await Assert.That(dst.GetPixel<Rgba32>(0, 0)).IsEqualTo(new Rgba32(50, 0, 0, 255));
    }

    [Test]
    public async Task DrawImage_OntoItself_On16Bit()
    {
        using Image image = Image16(16, 16, Alpha.Varying);
        using Image copy = image.Clone();
        image.Mutate(x => x.DrawImage(image, new Point(3, 3)));
        copy.Mutate(x => x.DrawImage(copy.Clone(), new Point(3, 3)));
        await Assert.That(image.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(copy.GetPixelSpan<Rgba64>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(PixelType.Rgba64, PixelType.Rgba64, MaskChannel.Alpha, false)]
    [Arguments(PixelType.Rgba64, PixelType.Rgba64, MaskChannel.Luminance, true)]
    [Arguments(PixelType.Rgba32, PixelType.Rgba64, MaskChannel.Alpha, true)]
    [Arguments(PixelType.Rgba32, PixelType.Rgba64, MaskChannel.Luminance, false)]
    [Arguments(PixelType.Rgba64, PixelType.Rgba32, MaskChannel.Luminance, false)]
    public async Task ApplyMask_IsExactAtEveryDepth(PixelType frameType, PixelType maskType, MaskChannel channel, bool invert)
    {
        using Image image = Depth(Image16(20, 12, Alpha.Varying, seed: 4), frameType);
        using Image mask = Depth(Image16(14, 9, Alpha.Varying, seed: 5), maskType);
        var at = new Point(3, 2);
        var options = new MaskOptions { Channel = channel, Invert = invert, Outside = MaskOutside.Transparent };
        using Image result = image.Clone(x => x.ApplyMask(mask, at, options));
        int frameMax = frameType == PixelType.Rgba64 ? 65535 : 255, maskMax = maskType == PixelType.Rgba64 ? 65535 : 255;
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                (int r, int g, int b, int a) = Native(image, x, y);
                int expected = 0;
                if (x >= at.X && y >= at.Y && x < at.X + 14 && y < at.Y + 9)
                {
                    (int mr, int mg, int mb, int ma) = Native(mask, x - at.X, y - at.Y);
                    long v = channel == MaskChannel.Alpha ? ma
                        : maskMax == 255 ? ((19595 * mr) + (38470 * mg) + (7471 * mb) + 32768) >> 16
                        : ((19595L * mr) + (38470L * mg) + (7471L * mb) + 32768) >> 16;
                    v = invert ? maskMax - v : v;
                    expected = (int)Math.Round((double)a * v / maskMax, MidpointRounding.AwayFromZero);
                }

                (int rr, int rg, int rb, int ra) = Native(result, x, y);
                if ((rr, rg, rb, ra) != (r, g, b, expected))
                {
                    Assert.Fail($"({x}, {y}): {(rr, rg, rb, ra)}, expected {(r, g, b, expected)} (frame max {frameMax})");
                }
            }
        }
    }

    private static (int R, int G, int B, int A) Native(Image image, int x, int y)
    {
        if (image.PixelType == PixelType.Rgba64)
        {
            Rgba64 p = image.GetPixel<Rgba64>(x, y);
            return (p.R, p.G, p.B, p.A);
        }

        Rgba32 q = image.GetPixel<Rgba32>(x, y);
        return (q.R, q.G, q.B, q.A);
    }

    [Test]
    public async Task Opacity_ScalesSixteenBitAlphaExactly()
    {
        using Image image = Image16(32, 8, Alpha.Varying);
        Rgba64[] source = image.GetPixelSpan<Rgba64>().ToArray();
        Rgba64[] actual = Result(image, x => x.Opacity(0.3f));
        for (int i = 0; i < source.Length; i++)
        {
            var expected = source[i] with { A = (ushort)Math.Floor((source[i].A * (double)0.3f) + 0.5) };
            await Assert.That(actual[i]).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments(PixelType.Rgba64)]
    [Arguments(PixelType.Rgba32)]
    public async Task BackgroundColor_MatchesReference(PixelType type)
    {
        using Image image = Depth(Image16(24, 16, Alpha.Varying), type);
        // Between 8-bit levels, so an 8-bit image must not round the color before blending.
        var background = new Rgba64(12850, 40000, 65000, 50000);
        using Image result = image.Clone(x => x.BackgroundColor(background));
        int max = 0;
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 24; x++)
            {
                Rgba64 p = Wide(image, x, y);
                Rgba64 expected = p.A == 0 ? Store(Unit(background, false), false, type)
                    : Store(WideReference.Blend(BlendMode.Normal, Unit(background, false), Unit(p, false), 1), false, type);
                max = Math.Max(max, MaxDifference(Wide(result, x, y), expected));
            }
        }

        await Assert.That(max).IsLessThanOrEqualTo(type == PixelType.Rgba64 ? 1 : 257);
    }

    [Test]
    public async Task Fill_StoresTheColorAtTheImageDepth()
    {
        var color = new Rgba64(1, 2, 65534, 65535);
        using Image wide = Image16(6, 5, Alpha.Varying);
        wide.Mutate(x => x.Fill(color, new Rectangle(1, 1, 3, 2)));
        await Assert.That(wide.GetPixel<Rgba64>(2, 2)).IsEqualTo(color);
        await Assert.That(wide.GetPixel<Rgba64>(0, 0)).IsNotEqualTo(color);

        using Image narrow = new(4, 4);
        narrow.Mutate(x => x.Fill(color));
        await Assert.That(narrow.GetPixel<Rgba32>(3, 3)).IsEqualTo(color.ToRgba32());
        await Assert.That(narrow.HasAlpha).IsFalse();
        narrow.Mutate(x => x.Fill(Color.FromArgb(128, 255, 0, 0)));
        await Assert.That(narrow.GetPixel<Rgba32>(0, 0)).IsEqualTo(new Rgba32(255, 0, 0, 128));
    }

    // ------------------------------------------------------------ the 8-bit path after rounding

    public static IEnumerable<(string Name, int Tolerance)> EightBitCases() =>
    [
        ("resize", 1), ("resize-linear", 1), ("rotate", 2), ("gauss", 2), ("box", 1), ("unsharp", 2), ("sharpen", 1),
        ("vignette", 1), ("draw", 1), ("draw-multiply-linear", 1), ("mask", 1), ("opacity", 0), ("background", 1), ("fill", 0),
    ];

    /// <summary>
    /// An 8-bit image widened to 16 bits gives, after rounding back, the 8-bit result within the stated tolerance: the
    /// 8-bit ports round between passes (box blur), truncate (affine) or use integer alpha compositing.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(EightBitCases))]
    public async Task Rounded16BitResult_MatchesThe8BitPath(string name, int tolerance)
    {
        using Image photo = Image.Load(Infrastructure.Corpus.PathOf("src/photo.png"));
        using Image narrow = photo.Clone(x => x.Resize(160, 120, ResamplingFilter.Bicubic));
        using Image overlay = narrow.Clone(x => x.Flip(FlipMode.Horizontal).Opacity(0.6f));
        using Image wide = narrow.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        using Image wideOverlay = overlay.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        Action<ImageProcessingContext> Operation(Image over) => name switch
        {
            "resize" => x => x.Resize(77, 51),
            "resize-linear" => x => x.Resize(new ResizeOptions { Size = new(77, 51), LinearLight = true }),
            "rotate" => x => x.Rotate(23f),
            "gauss" => x => x.GaussianBlur(2.2f),
            "box" => x => x.BoxBlur(3f),
            // Threshold 0: with a threshold, differences near it switch sides between the rounded 8-bit blur and the
            // exact one, and such a pixel jumps by percent · threshold.
            "unsharp" => x => x.UnsharpMask(2f, 150, 0),
            "sharpen" => x => x.Sharpen(),
            "vignette" => x => x.Vignette(0.7f),
            "draw" => x => x.DrawImage(over, new Point(5, 7), 0.8f),
            "draw-multiply-linear" => x => x.DrawImage(over, new Point(-5, 3), new DrawImageOptions { BlendMode = BlendMode.Multiply, LinearLight = true }),
            "mask" => x => x.ApplyMask(over, new MaskOptions { Channel = MaskChannel.Luminance }),
            "opacity" => x => x.Opacity(0.37f),
            "background" => x => x.Opacity(0.5f).BackgroundColor(new Rgba32(10, 200, 30)),
            _ => x => x.Fill(new Rgba32(1, 2, 3, 4), new Rectangle(10, 10, 50, 50)),
        };

        using Image expected = narrow.Clone(Operation(overlay));
        using Image actual = wide.Clone(Operation(wideOverlay));
        await Assert.That(actual.PixelType).IsEqualTo(PixelType.Rgba64);
        Rgba32[] e = expected.GetPixelSpan<Rgba32>().ToArray();
        Rgba64[] a = actual.GetPixelSpan<Rgba64>().ToArray();
        int max = 0;
        for (int i = 0; i < e.Length; i++)
        {
            Rgba32 r = a[i].ToRgba32();
            max = Math.Max(max, Math.Max(Math.Max(Math.Abs(r.R - e[i].R), Math.Abs(r.G - e[i].G)), Math.Max(Math.Abs(r.B - e[i].B), Math.Abs(r.A - e[i].A))));
        }

        Console.WriteLine($"{name}: max {max}");
        await Assert.That(max).IsLessThanOrEqualTo(tolerance);
    }

    // ------------------------------------------------------------ full ramps and determinism

    [Test]
    public async Task FullRamp_SurvivesKernelsAndComposition()
    {
        var ramp = new Rgba64[65536];
        for (int i = 0; i < ramp.Length; i++)
        {
            ramp[i] = new Rgba64((ushort)i, (ushort)(65535 - i), (ushort)i, ushort.MaxValue);
        }

        using Image image = Image.LoadPixelData<Rgba64>(ramp, 65536, 1);
        using Image opaqueMask = new(65536, 1, PixelType.Rgba64);
        opaqueMask.Mutate(x => x.Fill(Rgba64.White));
        using Image transparent = new(65536, 1, PixelType.Rgba64);
        // Each operation keeps every sample: constant columns, an opaque mask, a transparent source or backdrop.
        using Image result = image.Clone(x => x
            .Resize(65536, 3, ResamplingFilter.Lanczos3)
            .ApplyMask(opaqueMask)
            .DrawImage(transparent, Point.Empty)
            .BackgroundColor(Rgba64.Transparent)
            .Resize(65536, 1, ResamplingFilter.Bicubic));
        await Assert.That(result.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(ramp, CollectionOrdering.Matching);
    }

    [Test]
    [Property("Category", "Slow")]
    [Arguments("resize")]
    [Arguments("resize-linear")]
    [Arguments("rotate")]
    [Arguments("gauss")]
    [Arguments("vignette")]
    public async Task SixteenBitKernels_AreIndependentOfThreadCount(string name)
    {
        using Image image = Image16(700, 520, Alpha.Varying);
        Action<ImageProcessingContext> operation = name switch
        {
            "resize" => x => x.Resize(311, 233),
            "resize-linear" => x => x.Resize(new ResizeOptions { Size = new(311, 233), LinearLight = true }),
            "rotate" => x => x.Rotate(31f),
            "gauss" => x => x.GaussianBlur(6f),
            _ => x => x.Vignette(0.5f),
        };

        Rgba64[] parallel = Result(image, operation);
        Rgba64[] single = await Task.Factory.StartNew(() => Result(image, operation), CancellationToken.None, TaskCreationOptions.None,
            new InlineScheduler());
        await Assert.That(single).IsEquivalentTo(parallel, CollectionOrdering.Matching);
    }

    /// <summary>Runs every task synchronously on the queuing thread, so <see cref="Parallel"/> loops run on one thread.</summary>
    private sealed class InlineScheduler : TaskScheduler
    {
        public override int MaximumConcurrencyLevel => 1;

        protected override void QueueTask(Task task) => TryExecuteTask(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

        protected override IEnumerable<Task>? GetScheduledTasks() => [];
    }
}
