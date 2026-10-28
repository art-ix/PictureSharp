using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;
using PictureSharp.Tests.Png;
using static PictureSharp.Tests.Core.HighBitDepthProcessingTests;

namespace PictureSharp.Tests.Core;

/// <summary>
/// 0.9.0 step 3: tone and color operations on 16-bit images against independent double-precision formulas (errors in
/// 16-bit codes), full ramps through identity operations, full-resolution histograms, the 8-bit path after rounding,
/// and the exact equivalence of 8-bit settings migrated to normalized ones (v / 255f).
/// </summary>
public class HighBitDepthToneTests
{
    private const int W = 48, H = 40;

    // ------------------------------------------------------------ reference helpers

    private static double Unit(ushort v) => v / 65535.0;

    private static Rgba64[] MapChannels(Rgba64[] src, Func<double, int, double> map)
    {
        var result = new Rgba64[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            Rgba64 p = src[i];
            result[i] = new Rgba64(WideReference.Round16(map(Unit(p.R), 0)), WideReference.Round16(map(Unit(p.G), 1)),
                WideReference.Round16(map(Unit(p.B), 2)), p.A);
        }

        return result;
    }

    private static Rgba64[] MapPixels(Rgba64[] src, Func<double, double, double, (double R, double G, double B)> map)
    {
        var result = new Rgba64[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            Rgba64 p = src[i];
            (double r, double g, double b) = map(Unit(p.R), Unit(p.G), Unit(p.B));
            result[i] = new Rgba64(WideReference.Round16(r), WideReference.Round16(g), WideReference.Round16(b), p.A);
        }

        return result;
    }

    private static double Gain(double encoded, double gain) => WideReference.Encode(WideReference.Decode(encoded) * gain);

    private static double Luma709(double r, double g, double b) => (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

    private static int Luma16(Rgba64 p) => (int)(((19595L * p.R) + (38470L * p.G) + (7471L * p.B) + 32768) >> 16);

    /// <summary>Shape-preserving cubic (Fritsch–Butland interior slopes, Moler's end conditions) in double precision.</summary>
    internal static double Pchip(double[] xs, double[] ys, double x)
    {
        int n = xs.Length;
        if (x <= xs[0])
        {
            return ys[0];
        }

        if (x >= xs[n - 1])
        {
            return ys[n - 1];
        }

        var h = new double[n - 1];
        var m = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            h[k] = xs[k + 1] - xs[k];
            m[k] = (ys[k + 1] - ys[k]) / h[k];
        }

        var d = new double[n];
        if (n == 2)
        {
            d[0] = d[1] = m[0];
        }
        else
        {
            for (int k = 1; k < n - 1; k++)
            {
                d[k] = m[k - 1] * m[k] <= 0 ? 0
                    : ((2 * h[k]) + h[k - 1] + h[k] + (2 * h[k - 1])) / ((((2 * h[k]) + h[k - 1]) / m[k - 1]) + ((h[k] + (2 * h[k - 1])) / m[k]));
            }

            d[0] = End(h[0], h[1], m[0], m[1]);
            d[n - 1] = End(h[n - 2], h[n - 3], m[n - 2], m[n - 3]);
        }

        int s = 0;
        while (x > xs[s + 1])
        {
            s++;
        }

        double t = (x - xs[s]) / h[s];
        double h00 = ((2 * t) - 3) * t * t + 1, h10 = (((t - 2) * t) + 1) * t, h01 = (3 - (2 * t)) * t * t, h11 = (t - 1) * t * t;
        return (h00 * ys[s]) + (h10 * h[s] * d[s]) + (h01 * ys[s + 1]) + (h11 * h[s] * d[s + 1]);

        static double End(double h0, double h1, double m0, double m1)
        {
            double e = ((((2 * h0) + h1) * m0) - (h0 * m1)) / (h0 + h1);
            if (Math.Sign(e) != Math.Sign(m0))
            {
                return 0;
            }

            return Math.Sign(m0) != Math.Sign(m1) && Math.Abs(e) > 3 * Math.Abs(m0) ? 3 * m0 : e;
        }
    }

    /// <summary>Gaussian of the luma plane as three clipped box means (radii of <see cref="PlaneBlur.BoxRadii"/>), in double.</summary>
    private static double[] BlurredLuma(Rgba64[] src, float sigma)
    {
        double[] plane = [.. src.Select(p => Luma709(Unit(p.R), Unit(p.G), Unit(p.B)))];
        Span<int> radii = stackalloc int[3];
        PlaneBlur.BoxRadii(sigma, radii);
        foreach (int r in radii)
        {
            var horizontal = new double[plane.Length];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int left = Math.Max(0, x - r), right = Math.Min(W - 1, x + r);
                    double sum = 0;
                    for (int k = left; k <= right; k++)
                    {
                        sum += plane[(y * W) + k];
                    }

                    horizontal[(y * W) + x] = sum / (right - left + 1);
                }
            }

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int top = Math.Max(0, y - r), bottom = Math.Min(H - 1, y + r);
                    double sum = 0;
                    for (int k = top; k <= bottom; k++)
                    {
                        sum += horizontal[(k * W) + x];
                    }

                    plane[(y * W) + x] = sum / (bottom - top + 1);
                }
            }
        }

        return plane;
    }

    private static Rgba64[] AutoContrastReference(Rgba64[] src, bool preserveTone)
    {
        var maps = new Func<double, double>[3];
        for (int c = 0; c < 3; c++)
        {
            int[] values = preserveTone ? [.. src.Select(Luma16)] : [.. src.Select(p => (int)(c == 0 ? p.R : c == 1 ? p.G : p.B))];
            int low = values.Min(), high = values.Max();
            maps[c] = u => Math.Clamp(((u * 65535) - low) / (high - low), 0, 1);
        }

        return MapChannels(src, (u, c) => maps[c](u));
    }

    private static Rgba64[] EqualizeReference(Rgba64[] src)
    {
        var maps = new ushort[3][];
        for (int c = 0; c < 3; c++)
        {
            int[] values = [.. src.Select(p => (int)(c == 0 ? p.R : c == 1 ? p.G : p.B))];
            int top = values.Max();
            long spread = values.Length - values.Count(v => v == top);
            maps[c] = new ushort[65536];
            int[] sorted = [.. values.Order()];
            int index = 0;
            for (int v = 0; v < 65536; v++)
            {
                while (index < sorted.Length && sorted[index] < v)
                {
                    index++;
                }

                maps[c][v] = (ushort)Math.Min(65535, Math.Floor((65535.0 * index / spread) + 0.5));
            }
        }

        return [.. src.Select(p => new Rgba64(maps[0][p.R], maps[1][p.G], maps[2][p.B], p.A))];
    }

    private static Rgba64[] AutoWhiteBalanceReference(Rgba64[] src, double strength)
    {
        static bool Candidate(Rgba64 p) => p.A != 0 && new[] { p.R, p.G, p.B }.All(v => (v + 128) / 257 is >= 3 and <= 252);
        static int Luma(Rgba64 p) => ((2126 * p.R) + (7152 * p.G) + (722 * p.B) + 5000) / 10000;
        int[] lumas = [.. src.Where(Candidate).Select(Luma).Order()];
        int Rank(int numerator) => lumas[(int)Math.Ceiling(lumas.Length * numerator / 10.0) - 1];
        int low = Rank(1), high = Rank(9);
        Rgba64[] selected = [.. src.Where(p => Candidate(p) && Luma(p) >= low && Luma(p) <= high)];
        double[] mean = [.. new Func<Rgba64, ushort>[] { p => p.R, p => p.G, p => p.B }
            .Select(channel => selected.Average(p => WideReference.Decode(Unit(channel(p)))))];
        double y = Luma709(mean[0], mean[1], mean[2]);
        double[] gains = [.. mean.Select(m => Math.Pow(y / m, strength))];
        double normalize = y / Luma709(gains[0] * mean[0], gains[1] * mean[1], gains[2] * mean[2]);
        return MapChannels(src, (u, c) => Gain(u, gains[c] * normalize));
    }

    // ------------------------------------------------------------ against the reference

    public static IEnumerable<string> ReferenceCases() =>
    [
        "brightness", "brightness-extrapolate", "contrast", "saturate", "gamma", "levels", "levels-inverted", "curves",
        "curves-linear", "exposure", "white-balance", "white-balance-neutral", "invert", "grayscale", "sepia", "vibrance",
        "vibrance-negative", "auto-contrast", "auto-contrast-tone", "equalize", "auto-white-balance", "shadows-highlights",
        "clarity",
    ];

    [Test]
    [MethodDataSource(nameof(ReferenceCases))]
    public async Task Operation_MatchesDoubleReference(string name)
    {
        using Image image = Image16(W, H, Alpha.Varying, seed: 3);
        Rgba64[] src = image.GetPixelSpan<Rgba64>().ToArray();
        double[] curveX = [0, 0.2, 0.55, 0.8, 1], curveY = [0.05, 0.15, 0.6, 0.85, 0.97];
        (Action<ImageProcessingContext> operation, Func<Rgba64[]> reference) = name switch
        {
            "brightness" => ((Action<ImageProcessingContext>)(x => x.Brightness(0.73f)), (Func<Rgba64[]>)(() => MapChannels(src, (u, _) => u * 0.73f))),
            "brightness-extrapolate" => (x => x.Brightness(1.6f), () => MapChannels(src, (u, _) => u * 1.6f)),
            "contrast" => (x => x.Contrast(1.4f), () =>
            {
                double mean = src.Average(p => (double)Luma16(p)) / 65535;
                return MapChannels(src, (u, _) => mean + (1.4f * (u - mean)));
            }),
            "saturate" => (x => x.Saturate(1.7f), () => MapPixels(src, (r, g, b) =>
            {
                double l = ((19595 * r) + (38470 * g) + (7471 * b)) / 65536;
                return (l + (1.7f * (r - l)), l + (1.7f * (g - l)), l + (1.7f * (b - l)));
            })),
            "gamma" => (x => x.Gamma(1.8f), () => MapChannels(src, (u, _) => Math.Pow(u, 1 / 1.8f))),
            // Settings off the 1/256-code grid: 0.9f, for example, is the float nearest 58981.5 / 65535 and snaps to that tie.
            "levels" => (x => x.Levels(new LevelsOptions { InputBlackNormalized = 0.11f, InputWhiteNormalized = 0.83f, Gamma = 0.7f, OutputBlackNormalized = 0.07f, OutputWhiteNormalized = 0.91f }),
                () => MapChannels(src, (u, _) => 0.07f + ((0.91f - (double)0.07f) * Math.Pow(Math.Clamp((u - 0.11f) / (0.83f - (double)0.11f), 0, 1), 1 / (double)0.7f)))),
            "levels-inverted" => (x => x.Levels(new LevelsOptions { OutputBlackNormalized = 1f, OutputWhiteNormalized = 0.2f, Channels = ColorChannels.Green }),
                () => MapChannels(src, (u, c) => c == 1 ? 1 - (0.8f * u) : u)),
            "curves" => (x => x.CurvesNormalized([.. curveX.Select((v, i) => new PointF((float)v, (float)curveY[i]))], ColorChannels.Red | ColorChannels.Blue),
                () => MapChannels(src, (u, c) => c == 1 ? u : Pchip([.. curveX.Select(v => (double)(float)v)], [.. curveY.Select(v => (double)(float)v)], u))),
            "curves-linear" => (x => x.CurvesNormalized([new(0.25f, 0.1f), new(0.75f, 0.9f)]),
                () => MapChannels(src, (u, _) => Math.Clamp(0.1 + ((u - 0.25) * 1.6), 0.1, 0.9))),
            "exposure" => (x => x.Exposure(0.6f), () => MapChannels(src, (u, _) => Gain(u, Math.Pow(2, 0.6f)))),
            "white-balance" => (x => x.WhiteBalance(0.4f, -0.3f), () =>
            {
                double[] g = [Math.Pow(2, 0.5 * 0.4f), Math.Pow(2, 0.5 * 0.3f), Math.Pow(2, -0.5 * 0.4f)];
                double normalize = 1 / Luma709(g[0], g[1], g[2]);
                return MapChannels(src, (u, c) => Gain(u, g[c] * normalize));
            }),
            "white-balance-neutral" => (x => x.WhiteBalance(new Rgba64(51000, 47123, 40001, 9)), () =>
            {
                double[] n = [WideReference.Decode(51000 / 65535.0), WideReference.Decode(47123 / 65535.0), WideReference.Decode(40001 / 65535.0)];
                double y = Luma709(n[0], n[1], n[2]);
                return MapChannels(src, (u, c) => Gain(u, y / n[c]));
            }),
            "invert" => (x => x.Invert(), () => MapChannels(src, (u, _) => 1 - u)),
            "grayscale" => (x => x.Grayscale(), () => [.. src.Select(p => new Rgba64((ushort)Luma16(p), (ushort)Luma16(p), (ushort)Luma16(p), p.A))]),
            "sepia" => (x => x.Sepia(), () => MapPixels(src, (r, g, b) =>
                ((0.393 * r) + (0.769 * g) + (0.189 * b), (0.349 * r) + (0.686 * g) + (0.168 * b), (0.272 * r) + (0.534 * g) + (0.131 * b)))),
            "vibrance" => (x => x.Vibrance(0.6f), () => MapPixels(src, (r, g, b) => Vibrance(r, g, b, 0.6f))),
            "vibrance-negative" => (x => x.Vibrance(-0.45f), () => MapPixels(src, (r, g, b) => Vibrance(r, g, b, -0.45f))),
            "auto-contrast" => (x => x.AutoContrast(), () => AutoContrastReference(src, preserveTone: false)),
            "auto-contrast-tone" => (x => x.AutoContrast(0f, preserveTone: true), () => AutoContrastReference(src, preserveTone: true)),
            "equalize" => (x => x.Equalize(), () => EqualizeReference(src)),
            "auto-white-balance" => (x => x.AutoWhiteBalance(0.8f), () => AutoWhiteBalanceReference(src, 0.8f)),
            "shadows-highlights" => (x => x.ShadowsHighlights(0.5f, -0.4f, 2.5f), () =>
            {
                double[] blurred = BlurredLuma(src, 2.5f);
                return [.. src.Select((p, i) =>
                {
                    double b = blurred[i], gain = Math.Pow(2, 1.5 * ((0.5f * (1 - b) * (1 - b)) + (-0.4f * b * b)));
                    return new Rgba64(WideReference.Round16(Gain(Unit(p.R), gain)), WideReference.Round16(Gain(Unit(p.G), gain)),
                        WideReference.Round16(Gain(Unit(p.B), gain)), p.A);
                })];
            }),
            _ => (x => x.Clarity(0.7f, 3f), () =>
            {
                double[] blurred = BlurredLuma(src, 3f);
                return [.. src.Select((p, i) =>
                {
                    double luma = Luma709(Unit(p.R), Unit(p.G), Unit(p.B));
                    double delta = 0.7f * (luma - blurred[i]) * (4 * luma * (1 - luma));
                    return new Rgba64(WideReference.Round16(Unit(p.R) + delta), WideReference.Round16(Unit(p.G) + delta),
                        WideReference.Round16(Unit(p.B) + delta), p.A);
                })];
            }),
        };

        using Image result = image.Clone(x => operation(x));
        await AssertClose(result.GetPixelSpan<Rgba64>().ToArray(), reference(), name);
    }

    private static (double, double, double) Vibrance(double r, double g, double b, double amount)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max == min)
        {
            return (r, g, b);
        }

        double gain = amount >= 0 ? 1 + (amount * (1 - ((max - min) / max))) : 1 + amount;
        double y = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
        return (y + (gain * (r - y)), y + (gain * (g - y)), y + (gain * (b - y)));
    }

    /// <summary>Alpha must be kept exactly; colors within 1 code of 65535, RMS below 0.3.</summary>
    private static async Task AssertClose(Rgba64[] actual, Rgba64[] expected, string name)
    {
        int max = 0, alpha = 0;
        double sum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Rgba64 a = actual[i], e = expected[i];
            alpha = Math.Max(alpha, Math.Abs(a.A - e.A));
            foreach (int d in new[] { a.R - e.R, a.G - e.G, a.B - e.B })
            {
                max = Math.Max(max, Math.Abs(d));
                sum += d * d;
            }
        }

        double rms = Math.Sqrt(sum / (expected.Length * 3));
        foreach (int i in Enumerable.Range(0, expected.Length).Where(i => actual[i] != expected[i]).Take(3))
        {
            Console.WriteLine($"{name}: [{i}] {actual[i]} expected {expected[i]}");
        }

        Console.WriteLine($"{name}: color max {max}, RMS {rms:F4} (16-bit codes)");
        await Assert.That(alpha).IsEqualTo(0).Because(name);
        await Assert.That(max).IsLessThanOrEqualTo(1).Because(name);
        await Assert.That(rms).IsLessThanOrEqualTo(0.3).Because(name);
    }

    // ------------------------------------------------------------ full ramp

    /// <summary>Every channel holds each of the 65536 values once; alpha varies.</summary>
    private static Image FullRamp()
    {
        var pixels = new Rgba64[65536];
        for (int v = 0; v < pixels.Length; v++)
        {
            pixels[v] = new Rgba64((ushort)v, (ushort)(65535 - v), (ushort)(v * 40503), (ushort)(v * 7));
        }

        return Image.LoadPixelData<Rgba64>(pixels, 256, 256);
    }

    private static readonly PointF[] LinearCurve = [new(0, 0), new(0.5f, 0.5f), new(1, 1)];

    private static readonly PointF[] SCurve = [new(0, 0), new(64 / 255f, 52 / 255f), new(192 / 255f, 206 / 255f), new(1, 1)];

    public static IEnumerable<Func<ImageProcessingContext, ImageProcessingContext>> IdentityOperations() =>
    [
        x => x.Levels(new LevelsOptions { InputBlackNormalized = 0f, Gamma = 1f }),
        x => x.CurvesNormalized(LinearCurve),
        x => x.Invert().Invert(),
        x => x.AutoContrast(),
        x => x.Equalize(),
        x => x.MakeTransparent(new Rgba64(1, 2, 3, 4)),
        x => x.Brightness(1f).Contrast(1f).Saturate(1f).Exposure(0f).Vibrance(0f),
    ];

    [Test]
    [MethodDataSource(nameof(IdentityOperations))]
    public async Task FullRamp_SurvivesIdentityOperations(Func<ImageProcessingContext, ImageProcessingContext> operation)
    {
        using Image image = FullRamp();
        Rgba64[] before = image.GetPixelSpan<Rgba64>().ToArray();
        image.Mutate(x => operation(x));
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(image.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FullRamp_GammaKeepsEveryLevelDistinct()
    {
        // A gentle curve over a 16-bit ramp keeps far more than 256 levels (an 8-bit table would leave 256).
        using Image image = FullRamp();
        image.Mutate(x => x.Gamma(1.1f, ColorChannels.Red));
        int distinct = image.GetPixelSpan<Rgba64>().ToArray().Select(p => p.R).Distinct().Count();
        await Assert.That(distinct).IsGreaterThan(50000);
    }

    // ------------------------------------------------------------ histogram

    [Test]
    public async Task Histogram_FullResolution_CountsEveryValueOnBothDepths()
    {
        using Image wide = FullRamp();
        ImageHistogram exact = wide.GetHistogram(new HistogramOptions { FullResolution = true });
        await Assert.That(exact.BinCount).IsEqualTo(65536);
        await Assert.That(exact.Red.ToArray().All(c => c == 1)).IsTrue();
        await Assert.That(exact.Blue.ToArray().All(c => c == 1)).IsTrue();
        await Assert.That(exact.MeanNormalized(HistogramChannel.Red)).IsEqualTo(0.5);
        await Assert.That(exact.PercentileNormalized(HistogramChannel.Green, 50)).IsEqualTo(32767 / 65535.0);
        await Assert.That(exact.PercentileNormalized(HistogramChannel.Red, 100)).IsEqualTo(1.0);

        ImageHistogram coarse = wide.GetHistogram();
        await Assert.That(coarse.BinCount).IsEqualTo(256);
        for (int bin = 0; bin < 256; bin++)
        {
            // The nearest bin floor(v · 255 / 65535 + 0.5) of every value.
            long expected = Enumerable.Range(0, 65536).Count(v => (int)Math.Floor((v * 255.0 / 65535) + 0.5) == bin);
            await Assert.That(coarse.Red[bin]).IsEqualTo(expected);
        }

        using Image narrow = wide.Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        ImageHistogram narrowFull = narrow.GetHistogram(new HistogramOptions { FullResolution = true });
        ImageHistogram narrowDefault = narrow.GetHistogram();
        await Assert.That(narrowFull.BinCount).IsEqualTo(256);
        await Assert.That(narrowFull.Luminance.ToArray()).IsEquivalentTo(narrowDefault.Luminance.ToArray(), CollectionOrdering.Matching);
        // Rounded to 8 bits, the 16-bit samples fall into the same 256 bins.
        await Assert.That(narrowDefault.Red.ToArray()).IsEquivalentTo(coarse.Red.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Histogram_SixteenBit_RegionTransparencyAndLuma()
    {
        using Image image = Image16(W, H, Alpha.Varying, seed: 4);
        Rgba64[] pixels = image.GetPixelSpan<Rgba64>().ToArray();
        var region = new Rectangle(3, 5, 20, 7);
        ImageHistogram histogram = image.GetHistogram(new HistogramOptions
        {
            FullResolution = true, Region = region, Transparency = HistogramTransparency.Exclude,
        });
        Rgba64[] counted = [.. Enumerable.Range(region.Y, region.Height).SelectMany(y => Enumerable.Range(region.X, region.Width).Select(x => pixels[(y * W) + x])).Where(p => p.A != 0)];
        await Assert.That(histogram.PixelCount).IsEqualTo((long)counted.Length);
        await Assert.That(histogram.Alpha[0]).IsEqualTo(0L);
        foreach (Rgba64 p in counted.Take(20))
        {
            await Assert.That(histogram.Luminance[Luma16(p)]).IsEqualTo((long)counted.Count(q => Luma16(q) == Luma16(p)));
        }

        double mean = counted.Average(p => (double)p.G) / 65535;
        await Assert.That(Math.Abs(histogram.MeanNormalized(HistogramChannel.Green) - mean)).IsLessThan(1e-12);
    }

    // ------------------------------------------------------------ 8-bit path after rounding

    public static IEnumerable<(string Name, Func<ImageProcessingContext, ImageProcessingContext> Operation, int Tolerance)> NarrowCases() =>
    [
        ("levels", x => x.Levels(new LevelsOptions { InputBlackNormalized = 12 / 255f, InputWhiteNormalized = 231 / 255f, Gamma = 1.3f }), 1),
        ("curves", x => x.CurvesNormalized(SCurve), 1),
        ("exposure", x => x.Exposure(-0.7f), 1),
        ("white-balance", x => x.WhiteBalance(-0.3f, 0.2f), 1),
        ("white-balance-neutral", x => x.WhiteBalance(new Rgba32(212, 196, 170)), 1),
        ("auto-white-balance", x => x.AutoWhiteBalance(), 1),
        ("brightness", x => x.Brightness(1.3f), 1),
        ("contrast", x => x.Contrast(0.6f), 1),
        ("saturate", x => x.Saturate(1.5f), 1),
        ("grayscale", x => x.Grayscale(), 1),
        ("invert", x => x.Invert(), 0),
        ("sepia", x => x.Sepia(), 1),
        ("vibrance", x => x.Vibrance(0.5f), 1),
        ("auto-contrast", x => x.AutoContrast(1f), 1),
        ("auto-contrast-tone", x => x.AutoContrast(2f, 1f, preserveTone: true), 1),
        // Pillow divides the counts into steps of (N − L) / 255 pixels; the exact 16-bit formula differs by part of a step.
        ("equalize", x => x.Equalize(), 3),
        ("shadows-highlights", x => x.ShadowsHighlights(0.5f, -0.4f), 1),
        ("clarity", x => x.Clarity(0.6f), 1),
        ("make-transparent", x => x.MakeTransparent(new Rgba32(10, 20, 30)), 0),
    ];

    [Test]
    [MethodDataSource(nameof(NarrowCases))]
    public async Task WidenedImage_RoundsToTheEightBitResult(string name, Func<ImageProcessingContext, ImageProcessingContext> operation, int tolerance)
    {
        using Image source = Image16(W, H, Alpha.Varying, seed: 6);
        using Image narrow = source.Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        narrow[0, 0] = new Rgba32(10, 20, 30);
        using Image wide = narrow.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        using Image expected = narrow.Clone(x => operation(x));
        using Image actual = wide.Clone(x => operation(x).ConvertPixelType(PixelType.Rgba32));
        ReadOnlySpan<Rgba32> e = expected.GetPixelSpan<Rgba32>(), a = actual.GetPixelSpan<Rgba32>();
        int max = 0;
        for (int i = 0; i < e.Length; i++)
        {
            max = Math.Max(max, Math.Max(Math.Max(Math.Abs(a[i].R - e[i].R), Math.Abs(a[i].G - e[i].G)),
                Math.Max(Math.Abs(a[i].B - e[i].B), Math.Abs(a[i].A - e[i].A))));
        }

        Console.WriteLine($"{name}: max {max} 8-bit levels");
        await Assert.That(max).IsLessThanOrEqualTo(tolerance).Because(name);
    }

    // ------------------------------------------------------------ migration of 8-bit settings (v / 255f)

    [Test]
    public async Task NormalizedLevel_RecoversEveryEightBitLevelExactly()
    {
        for (int v = 0; v <= 255; v++)
        {
            await Assert.That(NormalizedLevel.Threshold(v / 255f, 255)).IsEqualTo(v);
            await Assert.That(NormalizedLevel.Threshold(v / 255f, 65535)).IsEqualTo(v * 257);
        }

        for (int quarter = 0; quarter <= 1020; quarter++)
        {
            double level = quarter / 4.0;
            await Assert.That(NormalizedLevel.ToCodes((float)level / 255f, 255)).IsEqualTo(level);
        }

        // A value that is not a migrated level stays unrounded.
        await Assert.That(NormalizedLevel.ToCodes(0.123f, 255)).IsEqualTo(0.123f * 255.0);
    }

    // The formula of ToneLut.Levels before 0.9.0, with byte levels.
    private static byte[] LegacyLevels(byte ib, byte iw, byte ob, byte ow, float gamma)
    {
        var map = new byte[256];
        double range = iw - ib, span = ow - ob, exponent = 1.0 / gamma;
        for (int x = 0; x < 256; x++)
        {
            double t = Math.Clamp((x - (double)ib) / range, 0.0, 1.0);
            map[x] = (byte)(int)Math.Floor(ob + (span * Math.Pow(t, exponent)) + 0.5);
        }

        return map;
    }

    private static Image GrayRamp()
    {
        var image = new Image(256, 1);
        for (int x = 0; x < 256; x++)
        {
            image[x, 0] = new Rgba32((byte)x, (byte)x, (byte)x, (byte)(255 - x));
        }

        return image;
    }

    [Test]
    public async Task Levels_MigratedSettings_GiveTheLegacyTables()
    {
        using Image ramp = GrayRamp();
        var random = new Random(12);
        float[] gammas = [1f, 0.5f, 0.7f, 1.4f, 2.2f, 3.1f];
        for (int i = 0; i < 2000; i++)
        {
            byte ib = (byte)random.Next(255), iw = (byte)random.Next(ib + 1, 256), ob = (byte)random.Next(256), ow = (byte)random.Next(256);
            float gamma = gammas[i % gammas.Length];
            using Image result = ramp.Clone(x => x.Levels(new LevelsOptions
            {
                InputBlackNormalized = ib / 255f, InputWhiteNormalized = iw / 255f, OutputBlackNormalized = ob / 255f,
                OutputWhiteNormalized = ow / 255f, Gamma = gamma,
            }));
            byte[] expected = LegacyLevels(ib, iw, ob, ow, gamma);
            byte[] actual = [.. result.GetPixelSpan<Rgba32>().ToArray().Select(p => p.G)];
            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{ib} {iw} {ob} {ow} {gamma}");
        }
    }

    // ToneCurve.Fill before 0.9.0, with PointF levels 0–255 (same operation order, so ties round alike).
    private static byte[] LegacyCurve(PointF[] points)
    {
        int n = points.Length;
        var slopes = new double[n];
        var d = new double[n];
        for (int k = 0; k < n - 1; k++)
        {
            slopes[k] = ((double)points[k + 1].Y - points[k].Y) / ((double)points[k + 1].X - points[k].X);
        }

        if (n == 2)
        {
            d[0] = d[1] = slopes[0];
        }
        else
        {
            for (int k = 1; k < n - 1; k++)
            {
                double h0 = (double)points[k].X - points[k - 1].X, h1 = (double)points[k + 1].X - points[k].X;
                if (Math.Sign(slopes[k - 1]) != Math.Sign(slopes[k]) || slopes[k - 1] == 0 || slopes[k] == 0)
                {
                    d[k] = 0;
                    continue;
                }

                double w1 = (2 * h1) + h0, w2 = h1 + (2 * h0);
                d[k] = 1.0 / (((w1 / slopes[k - 1]) + (w2 / slopes[k])) / (w1 + w2));
            }

            d[0] = Edge((double)points[1].X - points[0].X, (double)points[2].X - points[1].X, slopes[0], slopes[1]);
            d[n - 1] = Edge((double)points[n - 1].X - points[n - 2].X, (double)points[n - 2].X - points[n - 3].X, slopes[n - 2], slopes[n - 3]);
        }

        var map = new byte[256];
        int segment = 0;
        for (int x = 0; x < 256; x++)
        {
            double value;
            if (x <= points[0].X)
            {
                value = points[0].Y;
            }
            else if (x >= points[n - 1].X)
            {
                value = points[n - 1].Y;
            }
            else
            {
                while (x > points[segment + 1].X)
                {
                    segment++;
                }

                PointF p0 = points[segment], p1 = points[segment + 1];
                double h = (double)p1.X - p0.X;
                double t = (x - (double)p0.X) / h, t2 = t * t, t3 = t2 * t;
                value = (((2 * t3) - (3 * t2) + 1) * p0.Y) + ((t3 - (2 * t2) + t) * h * d[segment]) + (((-2 * t3) + (3 * t2)) * p1.Y)
                    + ((t3 - t2) * h * d[segment + 1]);
            }

            map[x] = (byte)(int)Math.Floor(Math.Clamp(value, 0.0, 255.0) + 0.5);
        }

        return map;

        static double Edge(double h0, double h1, double m0, double m1)
        {
            double e = (((2 * h0) + h1) * m0 - (h0 * m1)) / (h0 + h1);
            if (Math.Sign(e) != Math.Sign(m0))
            {
                return 0;
            }

            return Math.Sign(m0) != Math.Sign(m1) && Math.Abs(e) > 3 * Math.Abs(m0) ? 3 * m0 : e;
        }
    }

    [Test]
    public async Task Curves_MigratedPoints_GiveTheLegacyTables()
    {
        using Image ramp = GrayRamp();
        var random = new Random(7);
        for (int i = 0; i < 1500; i++)
        {
            int n = random.Next(2, 7);
            // Integer, half and quarter levels: the values a 0–255 PointF held exactly.
            float[] xs = [.. Enumerable.Range(0, 1021).OrderBy(_ => random.Next()).Take(n).Order().Select(q => q / 4f)];
            PointF[] points = [.. xs.Select(x => new PointF(x, random.Next(0, 1021) / 4f))];
            using Image result = ramp.Clone(x => x.CurvesNormalized([.. points.Select(p => new PointF(p.X / 255f, p.Y / 255f))]));
            byte[] actual = [.. result.GetPixelSpan<Rgba32>().ToArray().Select(p => p.B)];
            await Assert.That(actual).IsEquivalentTo(LegacyCurve(points), CollectionOrdering.Matching)
                .Because(string.Join(" ", points.Select(p => $"({p.X}, {p.Y})")));
        }
    }

    [Test]
    public async Task AutoCrop_MigratedThresholds_MatchByteSemanticsOnBothDepths()
    {
        var alpha = new Image(256, 1);
        var color = new Image(256, 1);
        for (int x = 0; x < 256; x++)
        {
            alpha[x, 0] = new Rgba32(9, 9, 9, (byte)x);
            color[x, 0] = new Rgba32((byte)x, 0, 0);
        }

        using (alpha)
        using (color)
        using (Image wideAlpha = alpha.Clone(x => x.ConvertPixelType(PixelType.Rgba64)))
        using (Image wideColor = color.Clone(x => x.ConvertPixelType(PixelType.Rgba64)))
        {
            for (int t = 0; t < 256; t++)
            {
                // Content lies above the threshold: x > t.
                Rectangle? expected = t == 255 ? null : new Rectangle(t + 1, 0, 255 - t, 1);
                var byAlpha = new AutoCropOptions { AlphaThresholdNormalized = t / 255f };
                var byColor = new AutoCropOptions { Mode = AutoCropMode.Background, ToleranceNormalized = t / 255f, Background = Rgba32.Black };
                await Assert.That(alpha.FindTrimBounds(byAlpha)).IsEqualTo(expected);
                await Assert.That(wideAlpha.FindTrimBounds(byAlpha)).IsEqualTo(expected);
                await Assert.That(color.FindTrimBounds(byColor)).IsEqualTo(expected);
                await Assert.That(wideColor.FindTrimBounds(byColor)).IsEqualTo(expected);
            }
        }
    }

    // ------------------------------------------------------------ colors, processors and validation

    [Test]
    public async Task MakeTransparent_MatchesExactlyAtTheImageDepth()
    {
        var pixels = new[] { new Rgba64(1000, 2000, 3000), new Rgba64(1001, 2000, 3000), new Rgba64(1000, 2000, 3000, 65534) };
        using Image wide = Image.LoadPixelData<Rgba64>(pixels, 3, 1);
        wide.Mutate(x => x.MakeTransparent(new Rgba64(1000, 2000, 3000)));
        Rgba64[] result = wide.GetPixelSpan<Rgba64>().ToArray();
        await Assert.That(result).IsEquivalentTo(new[] { new Rgba64(1000, 2000, 3000, 0), pixels[1], pixels[2] }, CollectionOrdering.Matching);

        // On an 8-bit image the key is rounded to 8 bits: 1000 / 257 = 3.9 → 4.
        using var narrow = new Image(2, 1);
        narrow[0, 0] = new Rgba32(4, 8, 12);
        narrow[1, 0] = new Rgba32(3, 8, 12);
        narrow.Mutate(x => x.MakeTransparent(new Rgba64(1000, 2056, 3084)));
        await Assert.That(narrow[0, 0]).IsEqualTo(new Rgba32(4, 8, 12, 0));
        await Assert.That(narrow[1, 0]).IsEqualTo(new Rgba32(3, 8, 12));
    }

    [Test]
    public async Task WhiteBalance_NeutralBetweenEightBitLevels_MakesThatColorGray()
    {
        // A neutral measured on a 16-bit image, between 8-bit levels, becomes gray within one code.
        var neutral = new Rgba64(40100, 36000, 30123);
        using Image image = Image.LoadPixelData<Rgba64>([neutral], 1, 1);
        image.Mutate(x => x.WhiteBalance(neutral));
        Rgba64 p = image.GetPixel<Rgba64>(0, 0);
        await Assert.That(Math.Max(Math.Abs(p.R - p.G), Math.Abs(p.G - p.B))).IsLessThanOrEqualTo(1);
    }

    private sealed class WideMirror : IFrameProcessor<Rgba64>
    {
        public Size GetOutputSize(Size source) => source;

        public void Process(ReadOnlySpan<Rgba64> source, Size sourceSize, Span<Rgba64> destination, Size destinationSize, CancellationToken cancellationToken)
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[source.Length - 1 - i] = source[i];
            }
        }
    }

    private readonly record struct Impostor(ulong Value);

    private sealed class ImpostorProcessor : IFrameProcessor<Impostor>
    {
        public Size GetOutputSize(Size source) => source;

        public void Process(ReadOnlySpan<Impostor> source, Size sourceSize, Span<Impostor> destination, Size destinationSize, CancellationToken cancellationToken)
        {
        }
    }

    [Test]
    public async Task ApplyProcessor_RunsOnItsPixelType_AndRejectsOthers()
    {
        using Image wide = Png16Tests.Ramp(5, 3);
        Rgba64[] before = wide.GetPixelSpan<Rgba64>().ToArray();
        wide.Mutate(x => x.ApplyProcessor(new WideMirror()));
        await Assert.That(wide.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(before.Reverse().ToArray(), CollectionOrdering.Matching);

        using var narrow = new Image(2, 2, new Rgba32(1, 2, 3));
        Assert.ThrowsExactly<InvalidOperationException>(() => narrow.Mutate(x => x.ApplyProcessor(new WideMirror())));
        Assert.ThrowsExactly<InvalidOperationException>(() => wide.Mutate(x => x.ApplyProcessor(new Extensibility.TransposeProcessor())));
        Assert.ThrowsExactly<NotSupportedException>(() => wide.Mutate(x => x.ApplyProcessor(new ImpostorProcessor())));
        await Assert.That(narrow[1, 1]).IsEqualTo(new Rgba32(1, 2, 3));
        await Assert.That(wide.GetPixelSpan<Rgba64>()[0]).IsEqualTo(before[^1]);
    }

    [Test]
    public async Task NormalizedSettings_RejectNaNInfinityAndOutOfRange()
    {
        float[] invalid = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.001f, 1.001f, 255f];
        using Image image = Png16Tests.Ramp(2, 2);
        foreach (float v in invalid)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LevelsOptions { InputBlackNormalized = v });
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LevelsOptions { InputWhiteNormalized = v });
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LevelsOptions { OutputBlackNormalized = v });
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LevelsOptions { OutputWhiteNormalized = v });
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AutoCropOptions { AlphaThresholdNormalized = v });
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AutoCropOptions { ToleranceNormalized = v });
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.UnsharpMask(1f, 100, v)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.CurvesNormalized([new(0, 0), new(v, 1)])));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.CurvesNormalized([new(0, v), new(1, 1)])));
        }

        Assert.ThrowsExactly<ArgumentException>(() => image.Mutate(x => x.Levels(new LevelsOptions { InputBlackNormalized = 0.5f, InputWhiteNormalized = 0.5f })));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.WhiteBalance(new Rgba64(0, 1, 1))));
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
    }

    [Test]
    public async Task ToneOperations_ApplyToEveryFrame_AndCancellationLeavesFramesUnchanged()
    {
        using Image image = Png16Tests.Ramp(8, 8);
        image.Frames.Add(Png16Tests.Ramp(8, 8, seed: 3), TimeSpan.Zero);
        Rgba64[] second = image.Frames[1].GetPixelSpan<Rgba64>().ToArray();
        using Image equalized = image.Clone(x => x.Equalize().Gamma(1.3f));
        await Assert.That(equalized.Frames[1].GetPixelSpan<Rgba64>().ToArray()).IsNotEquivalentTo(second, CollectionOrdering.Matching);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => image.Mutate(x => x.Clarity(0.5f), cts.Token));
        await Assert.That(image.Frames[1].GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(second, CollectionOrdering.Matching);
    }
}
