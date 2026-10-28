using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;
using static PictureSharp.Tests.Core.HighBitDepthProcessingTests;

namespace PictureSharp.Tests.Core;

/// <summary>
/// 0.9.0 step 4: film negatives and cleanup on 16-bit images against independent double-precision formulas (errors in
/// 16-bit codes), statistics at full resolution, unchanged samples where the operations change nothing, and the 8-bit
/// path after rounding.
/// </summary>
public class HighBitDepthFilmTests
{
    private const int W = 48, H = 40;

    private static readonly NegativeCalibration Calibration = new()
    {
        FilmBase = new(0.82f, 0.47f, 0.21f),
        DensityRange = new(2.1f, 2.4f, 1.9f),
        MidtoneGamma = new(0.9f, 1.15f, 1.3f),
    };

    private static double Unit(ushort v) => v / 65535.0;

    private static Rgba64[] Pixels16(Image image) => image.GetPixelSpan<Rgba64>().ToArray();

    /// <summary>The maximum and RMS difference of the color channels (16-bit codes); alpha must be equal.</summary>
    private static async Task<(int Max, double Rms)> AssertClose(Rgba64[] actual, Rgba64[] expected, int max, double rms, string name)
    {
        int worst = 0;
        double sum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Rgba64 a = actual[i], e = expected[i];
            await Assert.That(a.A).IsEqualTo(e.A);
            foreach (int d in new[] { a.R - e.R, a.G - e.G, a.B - e.B })
            {
                worst = Math.Max(worst, Math.Abs(d));
                sum += (double)d * d;
            }
        }

        double error = Math.Sqrt(sum / (expected.Length * 3));
        Console.WriteLine($"{name}: max {worst}, RMS {error:F4} (16-bit codes)");
        await Assert.That(worst).IsLessThanOrEqualTo(max);
        await Assert.That(error).IsLessThanOrEqualTo(rms);
        return (worst, error);
    }

    // ------------------------------------------------------------ negatives against the reference

    /// <summary>The printed value of an encoded 16-bit value, in double precision (NegativePrinter's formula).</summary>
    private static double PrintReference(double density, double range, double gamma, NegativePrintOptions print)
    {
        double t = Math.Clamp(density / range, 0, 1.05);
        double linear = Math.Pow(10, print.Contrast * (Math.Pow(t, gamma) - 1)) * Math.Pow(2, print.Exposure);
        double s = WideReference.Encode(linear);
        s += print.Curve * s * (1 - s) * ((2 * s) - 1);
        return Math.Clamp(s, 0, 1);
    }

    private static double Density(double filmBase, ushort value) =>
        Math.Log10(filmBase / Math.Max(WideReference.Decode(Unit(value)), NegativeCalibrator.DensityFloor));

    private static int Luma709(Rgba64 p) => ((2126 * p.R) + (7152 * p.G) + (722 * p.B) + 5000) / 10000;

    [Test]
    [Arguments(FilmType.ColorNegative)]
    [Arguments(FilmType.BlackAndWhiteNegative)]
    public async Task InvertNegative_MatchesTheReference(FilmType filmType)
    {
        NegativeCalibration calibration = Calibration with { FilmType = filmType };
        var print = new NegativePrintOptions { Contrast = 1.9f, Exposure = 0.3f, Curve = 0.45f };
        using Image image = Image16(W, H, Alpha.Varying, seed: 41);
        Rgba64[] source = Pixels16(image);
        image.Mutate(x => x.InvertNegative(calibration, print));
        var expected = new Rgba64[source.Length];
        var printed = new ushort[3];
        for (int i = 0; i < source.Length; i++)
        {
            Rgba64 p = source[i];
            ushort luma = (ushort)Luma709(p);
            ushort[] values = filmType == FilmType.BlackAndWhiteNegative ? [luma, luma, luma] : [p.R, p.G, p.B];
            for (int c = 0; c < 3; c++)
            {
                printed[c] = WideReference.Round16(PrintReference(Density(calibration.FilmBase[c], values[c]), calibration.DensityRange[c],
                    calibration.MidtoneGamma[c], print));
            }

            expected[i] = new Rgba64(printed[0], printed[1], printed[2], p.A);
        }

        await AssertClose(Pixels16(image), expected, 1, 0.05, $"InvertNegative {filmType}");
    }

    /// <summary>The backlight-corrected print interpolates a 65536-step table of the print curve: within a code of exact.</summary>
    [Test]
    public async Task LitPrint_MatchesTheReference()
    {
        Rgba64[] source = Pixels(W, H, Alpha.Varying, seed: 42);
        var destination = new Rgba64[source.Length];
        var slopeX = new ChannelValues(0.004f, -0.003f, 0.002f);
        var slopeY = new ChannelValues(-0.002f, 0.005f, 0.001f);
        var offset = new ChannelValues(0.03f, -0.05f, 0.02f);
        NegativePrintOptions print = NegativePrintOptions.Default;
        NegativePrinter.PrintLit(source, destination, W, H, Calibration, print, slopeX, slopeY, offset, CancellationToken.None);
        var expected = new Rgba64[source.Length];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Rgba64 p = source[(y * W) + x];
                ushort[] values = [p.R, p.G, p.B];
                var printed = new ushort[3];
                for (int c = 0; c < 3; c++)
                {
                    double d = Density(Calibration.FilmBase[c], values[c]) + offset[c] + (slopeX[c] * (x + 0.5)) + (slopeY[c] * (y + 0.5));
                    printed[c] = WideReference.Round16(PrintReference(d, Calibration.DensityRange[c], Calibration.MidtoneGamma[c], print));
                }

                expected[(y * W) + x] = new Rgba64(printed[0], printed[1], printed[2], p.A);
            }
        }

        await AssertClose(destination, expected, 1, 0.1, "PrintLit");
    }

    /// <summary>
    /// The calibration counts every 16-bit value: a film base between 8-bit levels is measured exactly, not as the
    /// nearest 8-bit level.
    /// </summary>
    [Test]
    public async Task AnalyzeNegative_MeasuresAtFullResolution()
    {
        ushort[] values = [40001, 30003, 20005];
        using Image image = new(32, 24, PixelType.Rgba64);
        image.Mutate(x => x.Fill(new Rgba64(values[0], values[1], values[2])));
        NegativeCalibration calibration = image.AnalyzeNegative().Calibration;
        for (int c = 0; c < 3; c++)
        {
            await Assert.That(calibration.FilmBase[c]).IsEqualTo(SrgbTransfer.Decode16(values[c]));
            await Assert.That(calibration.FilmBase[c]).IsNotEqualTo(SrgbTransfer.Decode(new Rgba64(values[c], 0, 0).ToRgba32().R));
        }
    }

    /// <summary>A widened 8-bit negative selects the same pixels and percentiles, so its calibration is the 8-bit one.</summary>
    [Test]
    public async Task AnalyzeNegative_OfAWidenedImage_EqualsThe8BitCalibration()
    {
        using Image photo = Image.Load(Infrastructure.Corpus.PathOf("jpeg/q90_444.jpg"));
        using Image negative = FilmNegativeTests.Negative(photo, 3);
        using Image wide = negative.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        var options = new NegativeOptions { FilmBaseRegion = FilmNegativeTests.RebateArea(negative), Region = FilmNegativeTests.ImageArea(negative) };
        NegativeAnalysis narrow = negative.AnalyzeNegative(options), widened = wide.AnalyzeNegative(options);
        await Assert.That(widened.Calibration).IsEqualTo(narrow.Calibration);
        await Assert.That(widened.ClippedFraction).IsEqualTo(narrow.ClippedFraction);
        await Assert.That(widened.Warnings).IsEqualTo(narrow.Warnings);
    }

    /// <summary>The frame of a widened 8-bit strip is found in the same place; the rebate is measured without rounding.</summary>
    [Test]
    public async Task FindFilmFrame_OfAWidenedStrip_EqualsThe8BitFrame()
    {
        using Image strip = FilmFrameTests.Strip(0.15f, seed: 5, degrees: 1.5f);
        using Image wide = strip.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        FilmFrame narrow = strip.FindFilmFrame()!, widened = wide.FindFilmFrame()!;
        await Assert.That(widened.Bounds).IsEqualTo(narrow.Bounds);
        await Assert.That(widened.Angle).IsEqualTo(narrow.Angle);
        await Assert.That(widened.PerforationsVertical).IsEqualTo(narrow.PerforationsVertical);
        await Assert.That(widened.CutEdges).IsEqualTo(narrow.CutEdges);
        await Assert.That(widened.MeasuredRegion).IsEqualTo(narrow.MeasuredRegion);
        for (int c = 0; c < 3; c++)
        {
            // The 8-bit rebate is read through a table at 1/16 of a level, the 16-bit one exactly.
            await Assert.That(Math.Abs(Math.Log(widened.FilmBase!.Value[c] / narrow.FilmBase!.Value[c]))).IsLessThan(0.002);
        }
    }

    /// <summary>
    /// A widened strip develops, after rounding, like the 8-bit strip: the same calibration, backlight correction, dust
    /// and color noise reduction. Straightening is compared separately: the 8-bit bicubic port truncates (as Pillow),
    /// the 16-bit one rounds, and the inversion amplifies that half level in the dense parts of the negative.
    /// </summary>
    [Test]
    public async Task DevelopNegative_OfAWidenedStrip_MatchesThe8BitPath()
    {
        using Image strip = FilmFrameTests.Strip(0.15f, seed: 5, degrees: 1.5f);
        using Image wide = strip.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        var options = new NegativeOptions { Crop = false };
        using Image expected = strip.Clone(x => x.DevelopNegative(options));
        using Image actual = wide.Clone(x => x.DevelopNegative(options));
        await Assert.That(actual.PixelType).IsEqualTo(PixelType.Rgba64);
        Rgba32[] e = expected.GetPixelSpan<Rgba32>().ToArray();
        Rgba64[] a = Pixels16(actual);
        int max = 0;
        double sum = 0;
        for (int i = 0; i < e.Length; i++)
        {
            Rgba32 r = a[i].ToRgba32();
            int d = Math.Max(Math.Max(Math.Abs(r.R - e[i].R), Math.Abs(r.G - e[i].G)), Math.Max(Math.Abs(r.B - e[i].B), Math.Abs(r.A - e[i].A)));
            max = Math.Max(max, d);
            sum += d;
        }

        Console.WriteLine($"DevelopNegative widened: max {max}, mean {sum / e.Length:F4} (8-bit levels)");
        await Assert.That(max).IsLessThanOrEqualTo(1);
        await Assert.That(sum / e.Length).IsLessThan(0.3);
    }

    /// <summary>On 16 bits the straightened crop is exactly <c>Rotate(frame.Angle).Crop(frame.Bounds)</c>, as on 8 bits.</summary>
    [Test]
    public async Task DevelopNegative_StraightensSixteenBitsAsRotateAndCrop()
    {
        using Image strip = FilmFrameTests.Strip(0.15f, seed: 5, degrees: 1.5f);
        using Image wide = strip.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        FilmFrame frame = wide.FindFilmFrame()!;
        await Assert.That(frame.Angle).IsNotEqualTo(0f);
        NegativeCalibration calibration = wide.AnalyzeNegative().Calibration;
        var options = new NegativeOptions { Calibration = calibration, CorrectIllumination = false, DustSize = 0, ColorNoiseReduction = 0f };
        using Image developed = wide.Clone(x => x.DevelopNegative(options));
        using Image expected = wide.Clone(x => x.Rotate(frame.Angle).Crop(frame.Bounds).InvertNegative(calibration));
        await Assert.That(Pixels16(developed)).IsEquivalentTo(Pixels16(expected), CollectionOrdering.Matching);
    }

    // ------------------------------------------------------------ cleanup against the reference

    // The mean of each (2r + 1)² window clipped to the plane.
    private static double[] BoxMean(double[] v, int w, int h, int r)
    {
        var result = new double[v.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double sum = 0;
                int n = 0;
                for (int sy = Math.Max(0, y - r); sy <= Math.Min(h - 1, y + r); sy++)
                {
                    for (int sx = Math.Max(0, x - r); sx <= Math.Min(w - 1, x + r); sx++)
                    {
                        sum += v[(sy * w) + sx];
                        n++;
                    }
                }

                result[(y * w) + x] = sum / n;
            }
        }

        return result;
    }

    private static double Premultiplied(Rgba64 p, int c) => (c switch { 0 => p.R, 1 => p.G, _ => p.B }) * (double)p.A / (65535.0 * 65535.0);

    private static ushort Straight(double premultiplied, int alpha) => WideReference.Round16(premultiplied * 65535 / alpha);

    [Test]
    [Arguments(0.5f, 2, Alpha.Varying)]
    [Arguments(1f, 4, Alpha.Opaque)]
    [Arguments(0.2f, 1, Alpha.Low)]
    public async Task Denoise_MatchesTheReference(float strength, int radius, Alpha alpha)
    {
        using Image image = Image16(W, H, alpha, seed: 43);
        Rgba64[] source = Pixels16(image);
        image.Mutate(x => x.Denoise(strength, radius));
        double epsilon = 0.05f * strength * (0.05f * strength);
        var expected = (Rgba64[])source.Clone();
        for (int c = 0; c < 3; c++)
        {
            double[] i0 = [.. source.Select(p => Premultiplied(p, c))];
            double[] mean = BoxMean(i0, W, H, radius), square = BoxMean([.. i0.Select(v => v * v)], W, H, radius);
            double[] a = new double[i0.Length], b = new double[i0.Length];
            for (int i = 0; i < i0.Length; i++)
            {
                double variance = Math.Max(0, square[i] - (mean[i] * mean[i]));
                a[i] = variance / (variance + epsilon);
                b[i] = mean[i] * (1 - a[i]);
            }

            double[] meanA = BoxMean(a, W, H, radius), meanB = BoxMean(b, W, H, radius);
            for (int i = 0; i < i0.Length; i++)
            {
                Rgba64 p = expected[i];
                if (p.A == 0)
                {
                    continue;
                }

                ushort v = Straight((meanA[i] * i0[i]) + meanB[i], p.A);
                expected[i] = c switch { 0 => p with { R = v }, 1 => p with { G = v }, _ => p with { B = v } };
            }
        }

        await AssertClose(Pixels16(image), expected, 1, 0.05, $"Denoise {strength} r{radius} {alpha}");
    }

    [Test]
    [Arguments(1f, 3, Alpha.Varying)]
    [Arguments(0.6f, 2, Alpha.Opaque)]
    public async Task ReduceColorNoise_MatchesTheReference(float strength, int radius, Alpha alpha)
    {
        using Image image = Image16(W, H, alpha, seed: 44);
        Rgba64[] source = Pixels16(image);
        image.Mutate(x => x.ReduceColorNoise(strength, radius));
        const double LumaR = PhotoAdjust.LumaR, LumaG = PhotoAdjust.LumaG, LumaB = PhotoAdjust.LumaB;
        double epsilon = ChromaFilter.Epsilon;
        double[] y0 = [.. source.Select(p => (LumaR * Premultiplied(p, 0)) + (LumaG * Premultiplied(p, 1)) + (LumaB * Premultiplied(p, 2)))];
        double[] meanY = BoxMean(y0, W, H, radius), squareY = BoxMean([.. y0.Select(v => v * v)], W, H, radius);
        double[] varY = [.. meanY.Select((m, i) => Math.Max(0, squareY[i] - (m * m)))];
        double[] Filter(int c)
        {
            double[] p = [.. source.Select((q, i) => Premultiplied(q, c) - y0[i])];
            double[] meanP = BoxMean(p, W, H, radius), meanYP = BoxMean([.. p.Select((v, i) => v * y0[i])], W, H, radius);
            double[] a = new double[p.Length], b = new double[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                a[i] = (meanYP[i] - (meanY[i] * meanP[i])) / (varY[i] + epsilon);
                b[i] = meanP[i] - (a[i] * meanY[i]);
            }

            double[] meanA = BoxMean(a, W, H, radius), meanB = BoxMean(b, W, H, radius);
            return [.. p.Select((v, i) => v + (strength * ((meanA[i] * y0[i]) + meanB[i] - v)))];
        }

        double[] cr = Filter(0), cb = Filter(2);
        var expected = new Rgba64[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            Rgba64 p = source[i];
            if (p.A == 0)
            {
                expected[i] = p;
                continue;
            }

            double r = y0[i] + cr[i], b = y0[i] + cb[i], g = (y0[i] - (LumaR * r) - (LumaB * b)) / LumaG;
            expected[i] = new Rgba64(Straight(r, p.A), Straight(g, p.A), Straight(b, p.A), p.A);
        }

        await AssertClose(Pixels16(image), expected, 1, 0.1, $"ReduceColorNoise {strength} r{radius} {alpha}");
    }

    /// <summary>Without specks every 16-bit sample stays exactly, including values between 8-bit levels.</summary>
    [Test]
    public async Task RemoveDust_KeepsAFullRampExactly()
    {
        using Image ramp = Png.Png16Tests.Ramp(256, 256);
        Rgba64[] before = Pixels16(ramp);
        ramp.Mutate(x => x.RemoveDust(4));
        await Assert.That(Pixels16(ramp)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The specks of a widened dusty image are found and replaced as on the 8-bit image, and a speck on a 16-bit
    /// background takes a 16-bit median of its surroundings, not an 8-bit level.
    /// </summary>
    [Test]
    public async Task RemoveDust_OnSixteenBits_MatchesThe8BitPathAndKeepsPrecision()
    {
        using Image dusty = PhotoCleanupTests.Dusty(120, 100, 7);
        using Image wide = dusty.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        using Image expected = dusty.Clone(x => x.RemoveDust(3));
        using Image actual = wide.Clone(x => x.RemoveDust(3));
        Rgba32[] e = expected.GetPixelSpan<Rgba32>().ToArray();
        Rgba64[] a = Pixels16(actual);
        int max = 0;
        for (int i = 0; i < e.Length; i++)
        {
            Rgba32 r = a[i].ToRgba32();
            max = Math.Max(max, Math.Max(Math.Abs(r.R - e[i].R), Math.Max(Math.Abs(r.G - e[i].G), Math.Abs(r.B - e[i].B))));
        }

        await Assert.That(max).IsEqualTo(0);

        // A bright 2 × 2 speck on a background whose samples fall between 8-bit levels.
        var pixels = new Rgba64[64 * 64];
        var random = new Random(3);
        for (int i = 0; i < pixels.Length; i++)
        {
            ushort v = (ushort)(30000 + random.Next(-40, 41));
            pixels[i] = new Rgba64(v, (ushort)(v - 1001), (ushort)(v + 999));
        }

        var speck = new Rgba64(62000, 62000, 62000);
        foreach (int i in new[] { (30 * 64) + 30, (30 * 64) + 31, (31 * 64) + 30, (31 * 64) + 31 })
        {
            pixels[i] = speck;
        }

        using Image background = Image.LoadPixelData<Rgba64>(pixels, 64, 64);
        background.Mutate(x => x.RemoveDust(3));
        Rgba64 cleaned = background.GetPixel<Rgba64>(30, 30);
        await Assert.That(cleaned.R is >= 29960 and <= 30040).IsTrue();
        await Assert.That(cleaned.G).IsNotEqualTo(cleaned.R);
        // Every sample far from the speck is kept.
        await Assert.That(background.GetPixel<Rgba64>(5, 5)).IsEqualTo(pixels[(5 * 64) + 5]);
    }
}
