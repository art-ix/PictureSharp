using System.Drawing;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Properties of <see cref="ImageAnalysisExtensions.AnalyzeNegative(Image, NegativeOptions?)"/>, InvertNegative and
/// DevelopNegative on synthetic negatives made from a known positive; pixel values are checked by
/// <see cref="FilmNegativeOracleTests"/>.
/// </summary>
public class FilmNegativeTests
{
    /// <summary>The orange base of <see cref="Negative"/> in linear light.</summary>
    internal static readonly float[] Base = [0.85f, 0.45f, 0.25f];

    /// <summary>Height of the bare-film band at the top of <see cref="Negative"/>.</summary>
    internal const int Rebate = 8;

    private static float Decode(byte v) => v / 255f <= 0.04045f ? v / 255f / 12.92f : MathF.Pow(((v / 255f) + 0.055f) / 1.055f, 2.4f);

    private static byte Encode(float linear)
    {
        float v = Math.Clamp(linear, 0f, 1f);
        float s = v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f;
        return (byte)(int)((s * 255) + 0.5f);
    }

    /// <summary>
    /// A color negative of <paramref name="positive"/>: each layer's density above an orange base is
    /// γ · log10(L / 0.004) for the scene's linear light L, with γ 0.55, 0.62 and 0.70 for red, green and blue,
    /// and a band of bare film <see cref="Rebate"/> rows high on top (noise ±1 everywhere).
    /// </summary>
    internal static Image Negative(Image positive, int seed)
    {
        ReadOnlySpan<float> gamma = [0.55f, 0.62f, 0.70f];
        var random = new Random(seed);
        var negative = new Image(positive.Width, positive.Height + Rebate);
        Span<byte> v = stackalloc byte[3];
        for (int y = 0; y < negative.Height; y++)
        {
            for (int x = 0; x < negative.Width; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    float density = 0f;
                    if (y >= Rebate)
                    {
                        Rgba32 p = positive[x, y - Rebate];
                        float linear = Decode(c switch { 0 => p.R, 1 => p.G, _ => p.B });
                        density = MathF.Max(0f, gamma[c] * MathF.Log10(MathF.Max(linear, 0.004f) / 0.004f));
                    }

                    v[c] = (byte)Math.Clamp(Encode(Base[c] * MathF.Pow(10f, -density)) + random.Next(-1, 2), 0, 255);
                }

                negative[x, y] = new Rgba32(v[0], v[1], v[2], 255);
            }
        }

        return negative;
    }

    internal static Rectangle ImageArea(Image negative) => new(0, Rebate, negative.Width, negative.Height - Rebate);

    internal static Rectangle RebateArea(Image negative) => new(0, 0, negative.Width, Rebate);

    private static Image Photo() => Image.Load(Corpus.PathOf("jpeg/q90_444.jpg"));

    [Test]
    public async Task Analyze_MeasuresTheFilmBase_AndLeavesTheImage()
    {
        using Image photo = Photo();
        using Image negative = Negative(photo, 1);
        Rgba32[] before = negative.GetPixelSpan<Rgba32>().ToArray();
        NegativeAnalysis analysis = negative.AnalyzeNegative(new NegativeOptions { Region = ImageArea(negative), FilmBaseRegion = RebateArea(negative) });
        ChannelValues filmBase = analysis.Calibration.FilmBase;
        for (int c = 0; c < 3; c++)
        {
            float measured = c switch { 0 => filmBase.Red, 1 => filmBase.Green, _ => filmBase.Blue };
            await Assert.That(Math.Abs(measured - Base[c]) / Base[c] < 0.02f).IsTrue().Because($"channel {c}: {measured}");
        }

        await Assert.That(analysis.Warnings).IsEqualTo(NegativeWarnings.None);
        await Assert.That(negative.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
        // The blue layer has the steepest gamma, so it spans the most density.
        ChannelValues range = analysis.Calibration.DensityRange;
        await Assert.That(range.Blue > range.Green && range.Green > range.Red).IsTrue().Because(range.ToString());
    }

    [Test]
    public async Task Analyze_ReportsEstimatedBase_ClippingAndThinNegatives()
    {
        using Image photo = Photo();
        using Image negative = Negative(photo, 2);
        NegativeAnalysis estimated = negative.AnalyzeNegative();
        await Assert.That(estimated.Warnings).IsEqualTo(NegativeWarnings.FilmBaseEstimated);

        using Image clipped = negative.Clone(x => x.Apply(f =>
        {
            Span<Rgba32> pixels = f.GetPixelSpan<Rgba32>();
            for (int i = 0; i < pixels.Length / 20; i++)
            {
                pixels[i].B = 0;
            }
        }));
        NegativeAnalysis withClipping = clipped.AnalyzeNegative(new NegativeOptions { FilmBaseRegion = new Rectangle(0, 20, 30, 1) });
        await Assert.That(withClipping.Warnings.HasFlag(NegativeWarnings.ChannelClipped)).IsTrue();
        await Assert.That(withClipping.ClippedFraction.Blue > 0.04f && withClipping.ClippedFraction.Red == 0f).IsTrue()
            .Because(withClipping.ClippedFraction.ToString());

        using var thin = new Image(32, 32);
        thin.GetPixelSpan<Rgba32>().Fill(new Rgba32(200, 120, 80, 255));
        thin[3, 3] = new Rgba32(190, 112, 74, 255);
        await Assert.That(thin.AnalyzeNegative().Warnings.HasFlag(NegativeWarnings.LowDensityRange)).IsTrue();
    }

    /// <summary>The test photo with a 12-step gray wedge from white down the left 24 columns.</summary>
    internal static Image PhotoWithWedge()
    {
        Image photo = Photo();
        for (int y = 0; y < photo.Height; y++)
        {
            byte w = (byte)(255 - (y * 12 / photo.Height * 22));
            for (int x = 0; x < 24; x++)
            {
                photo[x, y] = new Rgba32(w, w, w, 255);
            }
        }

        return photo;
    }

    [Test]
    public async Task Develop_WithAPrintMatchingTheFilm_RecoversThePositive_AndKeepsGrayGray()
    {
        // The synthetic film spans log10(1 / 0.004) ≈ 2.4 decades with a pure power law per layer, so a paper of the
        // same contrast without S curve or midtone balance prints the scene back (plan_film.md, stage B quality bar).
        using Image photo = PhotoWithWedge();
        using Image negative = Negative(photo, 3);
        var options = new NegativeOptions
        {
            Region = ImageArea(negative),
            FilmBaseRegion = RebateArea(negative),
            MidtoneBalance = 0,
            Print = new NegativePrintOptions { Contrast = 2.4f, Curve = 0 },
        };
        using Image developed = negative.Clone(x => x.DevelopNegative(options).Crop(ImageArea(negative)));
        double squared = 0;
        ReadOnlySpan<Rgba32> a = photo.GetPixelSpan<Rgba32>(), b = developed.GetPixelSpan<Rgba32>();
        for (int i = 0; i < a.Length; i++)
        {
            squared += ((b[i].R - a[i].R) * (b[i].R - a[i].R)) + ((b[i].G - a[i].G) * (b[i].G - a[i].G)) + ((b[i].B - a[i].B) * (b[i].B - a[i].B));
        }

        double psnr = 10 * Math.Log10(255.0 * 255 / (squared / (3.0 * a.Length)));
        await Assert.That(psnr >= 30).IsTrue().Because($"PSNR {psnr:F1} dB");
        for (int step = 0; step < 12; step++)
        {
            Rgba32 p = developed[12, ((2 * step) + 1) * developed.Height / 24];
            int chroma = Math.Max(Math.Abs(p.R - p.G), Math.Max(Math.Abs(p.G - p.B), Math.Abs(p.R - p.B)));
            await Assert.That(chroma <= 8).IsTrue().Because($"step {step}: {p}");
        }
    }

    [Test]
    public async Task Invert_IsMonotone_PrintsTheBaseBlack_AndKeepsAlpha()
    {
        var calibration = new NegativeCalibration
        {
            FilmBase = new(0.85f, 0.45f, 0.25f),
            DensityRange = new(1.2f, 1.4f, 1.6f),
            MidtoneGamma = new(1.1f, 1f, 0.9f),
        };
        using var ramp = new Image(256, 1);
        for (int x = 0; x < 256; x++)
        {
            ramp[x, 0] = new Rgba32((byte)x, (byte)x, (byte)x, (byte)x);
        }

        using Image positive = ramp.Clone(x => x.InvertNegative(calibration));
        for (int x = 1; x < 256; x++)
        {
            Rgba32 p = positive[x, 0], q = positive[x - 1, 0];
            await Assert.That(p.R <= q.R && p.G <= q.G && p.B <= q.B && p.A == x).IsTrue().Because($"x = {x}: {q} → {p}");
        }

        // The base is printed at 10^-1.6 of white, then deepened by the S curve.
        using var bare = new Image(1, 1);
        bare[0, 0] = new Rgba32(237, 179, 137, 255);
        using Image black = bare.Clone(x => x.InvertNegative(calibration));
        await Assert.That(black[0, 0].R < 40 && black[0, 0].G < 40 && black[0, 0].B < 40).IsTrue().Because(black[0, 0].ToString());
    }

    [Test]
    public async Task BlackAndWhite_GivesGray()
    {
        using Image photo = Photo();
        using Image negative = Negative(photo, 4);
        using Image developed = negative.Clone(x => x.DevelopNegative(new NegativeOptions { FilmType = FilmType.BlackAndWhiteNegative }));
        Rgba32[] pixels = developed.GetPixelSpan<Rgba32>().ToArray();
        await Assert.That(pixels.All(p => p.R == p.G && p.G == p.B)).IsTrue();
    }

    [Test]
    public async Task Develop_WithACalibration_EqualsAnalyzeThenDevelop_AndKeepsTheProfile()
    {
        using Image photo = Photo();
        using Image negative = Negative(photo, 5);
        byte[] profile = File.ReadAllBytes(Corpus.PathOf("color/p3_v4_para0.icc"));
        var options = new NegativeOptions { Region = ImageArea(negative), DustSize = 0 };
        NegativeCalibration calibration = negative.AnalyzeNegative(options).Calibration;
        using Image direct = negative.Clone(x => x.DevelopNegative(options));
        using Image calibrated = negative.Clone(x => x.DevelopNegative(options with { Calibration = calibration }));
        await Assert.That(calibrated.GetPixelSpan<Rgba32>().SequenceEqual(direct.GetPixelSpan<Rgba32>())).IsTrue();

        negative.Metadata.IccProfile = profile;
        using Image withProfile = negative.Clone(x => x.DevelopNegative(options));
        await Assert.That(withProfile.Metadata.IccProfile).IsEquivalentTo(profile, CollectionOrdering.Matching);
        await Assert.That(withProfile.GetPixelSpan<Rgba32>().SequenceEqual(direct.GetPixelSpan<Rgba32>())).IsTrue();
    }

    [Test]
    public async Task Validation()
    {
        using Image photo = Photo();
        using Image negative = Negative(photo, 6);
        await Assert.That(() => negative.AnalyzeNegative(new NegativeOptions { Region = new Rectangle(0, 0, 999, 10) })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => negative.AnalyzeNegative(new NegativeOptions { FilmBaseRegion = new Rectangle(5, 5, 0, 3) })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new NegativeOptions { HighlightPercentile = 40 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new NegativeOptions { DustSize = 65 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new NegativeOptions { Print = null! }).Throws<ArgumentNullException>();
        await Assert.That(() => new NegativePrintOptions { Contrast = 0.2f }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new NegativeCalibration { FilmBase = new(0f, 0.5f, 0.5f), DensityRange = new(1, 1, 1) }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new NegativeCalibration { FilmBase = new(1, 1, 1), DensityRange = new(1, float.NaN, 1) }).Throws<ArgumentOutOfRangeException>();

        using Image animation = negative.Clone(x => { });
        animation.Frames.Add(negative.Clone(x => { }), TimeSpan.FromMilliseconds(10));
        await Assert.That(() => animation.AnalyzeNegative()).Throws<NotSupportedException>();
        await Assert.That(() => animation.Mutate(x => x.DevelopNegative())).Throws<NotSupportedException>();
    }

    [Test]
    public async Task Combine_TakesMedianBaseAndGamma_AndLargestRange()
    {
        NegativeCalibration Make(float b, float r, float g) =>
            new() { FilmBase = new(b, b, b), DensityRange = new(r, r, r), MidtoneGamma = new(g, g, g) };
        NegativeCalibration roll = NegativeCalibration.Combine([Make(0.5f, 1f, 1f), Make(0.7f, 2f, 0.8f), Make(0.6f, 1.5f, 1.2f)]);
        await Assert.That(roll.FilmBase).IsEqualTo(new ChannelValues(0.6f, 0.6f, 0.6f));
        await Assert.That(roll.DensityRange).IsEqualTo(new ChannelValues(2f, 2f, 2f));
        await Assert.That(roll.MidtoneGamma).IsEqualTo(new ChannelValues(1f, 1f, 1f));
        await Assert.That(() => NegativeCalibration.Combine([])).Throws<ArgumentException>();
        await Assert.That(() => NegativeCalibration.Combine([Make(0.5f, 1f, 1f), Make(0.5f, 1f, 1f) with { FilmType = FilmType.BlackAndWhiteNegative }]))
            .Throws<ArgumentException>();
    }

    /// <summary>
    /// A dark plate (luma about 40) with bright lettering-like strokes 5 pixels wide: rings of 12 pixels radius, half
    /// rings and short bars, like the letters S, 8, 3 and P that a too large dust size erased in 0.7.11.
    /// </summary>
    internal static Image Plate(int width, int height)
    {
        var plate = new Image(width, height);
        plate.GetPixelSpan<Rgba32>().Fill(new Rgba32(45, 40, 38, 255));
        var bright = new Rgba32(235, 225, 170, 255);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int cell = x / 40, cx = (cell * 40) + 20, cy = height / 2;
                double r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                bool stroke = (cell % 3) switch
                {
                    0 => Math.Abs(r - 12) <= 2.5,                                 // ring (O, 8)
                    1 => Math.Abs(r - 12) <= 2.5 && (x < cx || y < cy),           // three-quarter ring (S, 3)
                    _ => Math.Abs(x - cx) <= 2 && Math.Abs(y - cy) <= 15,         // bar (I, P)
                };
                if (stroke)
                {
                    plate[x, y] = bright;
                }
            }
        }

        return plate;
    }

    [Test]
    public async Task Develop_ByDefault_RemovesNoDust_AndKeepsLettering()
    {
        using Image positive = Plate(240, 60);
        using Image negative = Negative(positive, 8);
        var options = new NegativeOptions { Region = ImageArea(negative), FilmBaseRegion = RebateArea(negative) };
        using Image developed = negative.Clone(x => x.DevelopNegative(options));
        using Image withoutDust = negative.Clone(x => x.DevelopNegative(options with { DustSize = 0 }));
        await Assert.That(new NegativeOptions().DustSize).IsEqualTo(0);
        await Assert.That(developed.GetPixelSpan<Rgba32>().SequenceEqual(withoutDust.GetPixelSpan<Rgba32>())).IsTrue();

        int strokes = 0, kept = 0;
        for (int y = 0; y < positive.Height; y++)
        {
            for (int x = 0; x < positive.Width; x++)
            {
                if (positive[x, y].R > 200)
                {
                    strokes++;
                    Rgba32 p = developed[x, y + Rebate];
                    kept += (2126 * p.R) + (7152 * p.G) + (722 * p.B) > 1_400_000 ? 1 : 0;
                }
            }
        }

        await Assert.That(kept * 100 >= strokes * 98).IsTrue().Because($"{kept} of {strokes} stroke pixels stay bright");
    }
}
