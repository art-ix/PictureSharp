using System.Drawing;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

/// <summary>
/// <see cref="ImageAnalysisExtensions.FindFilmFrame(Image)"/> and the automatic DevelopNegative on synthetic photographs
/// of a 35 mm strip on a light box: straight, rotated, vertical, cut off and unevenly lit.
/// </summary>
public class FilmFrameTests
{
    private const int Pitch = 40;
    private const float Mm = Pitch / 4.75f;

    // Linear light: a bluish-white light box and the orange base of FilmNegativeTests.
    private static readonly float[] Lamp = [0.8f, 0.9f, 1f];

    private static byte Encode(float linear)
    {
        float v = Math.Clamp(linear, 0f, 1f);
        float s = v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f;
        return (byte)(int)((s * 255) + 0.5f);
    }

    internal static Rgba32 LampColor => new(Encode(Lamp[0]), Encode(Lamp[1]), Encode(Lamp[2]), 255);

    /// <summary>
    /// A light box with a horizontal strip across the middle: two rows of perforation (holes 2.8 × 2 mm every
    /// 4.75 mm), dark edge printing outside them, the frame (36 × 24 mm) in the center and its neighbors 38 mm to
    /// each side. The picture's density is 0.3–1.3 above the base (<paramref name="flat"/>: 0.6 everywhere). The
    /// light falls off by <paramref name="light"/> from the right edge to the left; noise ±1.
    /// </summary>
    internal static Image Strip(float light = 0f, bool flat = false, int seed = 1) => Strip(out _, light, flat, seed);

    /// <summary>The strip of <see cref="Strip(float, bool, int)"/> turned clockwise by <paramref name="degrees"/> on the light box.</summary>
    internal static Image Strip(float light, int seed, float degrees)
    {
        Image strip = Strip(light, seed: seed);
        strip.Mutate(x => x.Rotate(degrees, ResamplingFilter.Bicubic, LampColor));
        return strip;
    }

    /// <summary>The strip of <see cref="Strip(float, bool, int)"/> and the frame's picture in it.</summary>
    internal static Image Strip(out RectangleF frame, float light = 0f, bool flat = false, int seed = 1)
    {
        int width = 16 * Pitch, height = 11 * Pitch;
        float filmTop = (height - (35 * Mm)) / 2, cx = width / 2f;
        frame = new RectangleF(cx - (18 * Mm), filmTop + (5.5f * Mm), 36 * Mm, 24 * Mm);
        ReadOnlySpan<float> gamma = [0.55f, 0.62f, 0.70f];
        var random = new Random(seed);
        var image = new Image(width, height);
        Span<byte> v = stackalloc byte[3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float fy = y + 0.5f - filmTop, fx = x + 0.5f - cx;
                float density = float.NaN;
                if (fy >= 0 && fy < 35 * Mm)
                {
                    density = 0;
                    // Holes centered 3 mm from each edge, one every pitch, symmetric about the frame's center.
                    float along = ((fx / Pitch) + 0.5f) - MathF.Floor((fx / Pitch) + 0.5f) - 0.5f;
                    bool hole = MathF.Abs(along * Pitch) < 1.4f * Mm && (MathF.Abs(fy - (3 * Mm)) < Mm || MathF.Abs(fy - (32 * Mm)) < Mm);
                    if (hole)
                    {
                        density = float.NaN;
                    }
                    else if (fy < 1.6f * Mm && ((int)MathF.Floor(fx / (2 * Mm)) % 3 == 0))
                    {
                        density = 1.2f;   // edge printing
                    }
                    else
                    {
                        float inFrame = fx - (MathF.Round(fx / (38 * Mm)) * 38 * Mm);
                        if (MathF.Abs(inFrame) < 18 * Mm && fy >= 5.5f * Mm && fy < 29.5f * Mm)
                        {
                            density = flat ? 0.6f : 0.8f + (0.5f * MathF.Sin(x / 23f) * MathF.Cos(y / 17f));
                        }
                    }
                }

                float lit = 1f - (light * (1f - ((x + 0.5f) / width)));
                for (int c = 0; c < 3; c++)
                {
                    float transmitted = float.IsNaN(density) ? Lamp[c] : Lamp[c] * FilmNegativeTests.Base[c] * MathF.Pow(10f, -density * gamma[c] / 0.6f);
                    v[c] = (byte)Math.Clamp(Encode(transmitted * lit) + random.Next(-1, 2), 0, 255);
                }

                image[x, y] = new Rgba32(v[0], v[1], v[2], 255);
            }
        }

        return image;
    }

    // The frame cut out as FilmFrame documents it.
    private static Image CutOut(Image image, FilmFrame frame) => image.Clone(x => x.Rotate(frame.Angle, ResamplingFilter.Bicubic, LampColor).Crop(frame.Bounds));

    // The share of pixels as bright as the bare base or the lamp (the picture is at least 0.3 denser in green).
    private static double BaseShare(Image image)
    {
        byte limit = Encode(Lamp[1] * FilmNegativeTests.Base[1] * MathF.Pow(10f, -0.15f));
        int bright = 0;
        foreach (Rgba32 p in image.GetPixelSpan<Rgba32>())
        {
            bright += p.G > limit ? 1 : 0;
        }

        return (double)bright / (image.Width * image.Height);
    }

    [Test]
    public async Task Find_LocatesTheFrame_InsideItsPicture()
    {
        using Image strip = Strip(out RectangleF expected);
        FilmFrame? frame = strip.FindFilmFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Angle).IsEqualTo(0f);
        await Assert.That(frame.PerforationsVertical).IsFalse();
        await Assert.That(frame.CutEdges).IsEqualTo(FilmFrameEdges.None);
        Rectangle b = frame.Bounds;
        await Assert.That(b.Left >= expected.Left - 1 && b.Top >= expected.Top - 1 && b.Right <= expected.Right + 1 && b.Bottom <= expected.Bottom + 1).IsTrue();
        await Assert.That(b.Width >= 0.96 * expected.Width && b.Height >= 0.96 * expected.Height).IsTrue();
    }

    [Test]
    [Arguments(2f)]
    [Arguments(-1.3f)]
    [Arguments(6f)]
    public async Task Find_StraightensARotatedStrip(float degrees)
    {
        using Image strip = Strip(out RectangleF expected);
        strip.Mutate(x => x.Rotate(degrees, ResamplingFilter.Bicubic, LampColor));
        FilmFrame frame = strip.FindFilmFrame()!;
        await Assert.That(frame.Angle).IsEqualTo(-degrees).Within(0.1f);
        using Image picture = CutOut(strip, frame);
        await Assert.That(picture.Width >= 0.95 * expected.Width && picture.Width <= expected.Width).IsTrue();
        await Assert.That(picture.Height >= 0.95 * expected.Height && picture.Height <= expected.Height).IsTrue();
        await Assert.That(BaseShare(picture)).IsLessThan(0.002);
    }

    [Test]
    public async Task Find_ReportsAVerticalStrip()
    {
        using Image strip = Strip(out RectangleF expected);
        strip.Mutate(x => x.Rotate(RotateMode.Rotate90));
        FilmFrame frame = strip.FindFilmFrame()!;
        await Assert.That(frame.PerforationsVertical).IsTrue();
        await Assert.That(frame.Bounds.Width <= expected.Height && frame.Bounds.Width >= 0.95 * expected.Height).IsTrue();
        await Assert.That(frame.Bounds.Height <= expected.Width && frame.Bounds.Height >= 0.95 * expected.Width).IsTrue();
        using Image picture = CutOut(strip, frame);
        await Assert.That(BaseShare(picture)).IsLessThan(0.002);
    }

    [Test]
    [Arguments(RotateMode.None, FilmFrameEdges.Left)]
    [Arguments(RotateMode.Rotate90, FilmFrameEdges.Top)]
    [Arguments(RotateMode.Rotate180, FilmFrameEdges.Right)]
    [Arguments(RotateMode.Rotate270, FilmFrameEdges.Bottom)]
    public async Task Find_ReportsTheEdgeThePhotographCuts(RotateMode turn, FilmFrameEdges cut)
    {
        using Image strip = Strip(out RectangleF expected);
        // Keep 70% of the frame: the photograph starts inside it.
        int left = (int)(expected.Left + (0.3f * expected.Width));
        strip.Mutate(x => x.Crop(new Rectangle(left, 0, strip.Width - left, strip.Height)).Rotate(turn));
        FilmFrame frame = strip.FindFilmFrame()!;
        await Assert.That(frame.CutEdges).IsEqualTo(cut);
        NegativeAnalysis analysis = strip.AnalyzeNegative(new NegativeOptions { Frame = frame });
        await Assert.That(analysis.Warnings.HasFlag(NegativeWarnings.FrameCut)).IsTrue();
        using Image picture = CutOut(strip, frame);
        await Assert.That(BaseShare(picture)).IsLessThan(0.002);
    }

    [Test]
    public async Task Find_ReturnsNull_WithoutPerforation()
    {
        using Image plate = FilmNegativeTests.Plate(240, 60);
        using Image noise = ProcessingOracleTests.Noise(77, 51, 3);
        using Image blank = new(300, 200);
        await Assert.That(plate.FindFilmFrame()).IsNull();
        await Assert.That(noise.FindFilmFrame()).IsNull();
        await Assert.That(blank.FindFilmFrame()).IsNull();
    }

    [Test]
    public async Task Analyze_TakesTheBaseFromTheRebate()
    {
        using Image strip = Strip();
        NegativeAnalysis analysis = strip.AnalyzeNegative();
        await Assert.That(analysis.Frame).IsNotNull();
        await Assert.That(analysis.Warnings).IsEqualTo(NegativeWarnings.None);
        await Assert.That(analysis.FilmBaseUniformity!.Value).IsLessThan(2f);
        ChannelValues filmBase = analysis.Calibration.FilmBase;
        for (int c = 0; c < 3; c++)
        {
            float expected = Lamp[c] * FilmNegativeTests.Base[c];
            float measured = c switch { 0 => filmBase.Red, 1 => filmBase.Green, _ => filmBase.Blue };
            await Assert.That(measured).IsEqualTo(expected).Within(0.02f * expected);
        }

        // Fixed regions turn the search off.
        NegativeAnalysis manual = strip.AnalyzeNegative(new NegativeOptions { Region = new Rectangle(0, 0, 10, 10) });
        await Assert.That(manual.Frame).IsNull();
        await Assert.That(manual.Warnings.HasFlag(NegativeWarnings.FilmBaseEstimated)).IsTrue();
    }

    [Test]
    public async Task Develop_CropsToTheFrame_UnlessCropIsOff()
    {
        using Image strip = Strip();
        strip.Mutate(x => x.Rotate(1.5f, ResamplingFilter.Bicubic, LampColor));
        FilmFrame frame = strip.FindFilmFrame()!;
        using Image developed = strip.Clone(x => x.DevelopNegative());
        await Assert.That(developed.Size).IsEqualTo(frame.Bounds.Size);
        // The base prints black: none of it is left at the edges.
        int dark = 0;
        foreach (Rgba32 p in developed.GetPixelSpan<Rgba32>())
        {
            dark += p.R + p.G + p.B < 30 ? 1 : 0;
        }

        await Assert.That(dark).IsLessThan(developed.Width * developed.Height / 500);
        using Image whole = strip.Clone(x => x.DevelopNegative(new NegativeOptions { Crop = false }));
        await Assert.That(whole.Size).IsEqualTo(strip.Size);

        // Straightening and cropping in one resampling is Rotate(Angle).Crop(Bounds) exactly.
        NegativeCalibration calibration = strip.AnalyzeNegative().Calibration;
        using Image direct = strip.Clone(x => x.DevelopNegative(new NegativeOptions
        {
            Frame = frame, Calibration = calibration, CorrectIllumination = false, ColorNoiseReduction = 0,
        }));
        using Image stepwise = strip.Clone(x => x.Rotate(frame.Angle).Crop(frame.Bounds).InvertNegative(calibration));
        await Assert.That(direct.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(stepwise.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Develop_EvensOutTheBacklight()
    {
        using Image strip = Strip(light: 0.5f, flat: true);
        NegativeAnalysis analysis = strip.AnalyzeNegative();
        await Assert.That(analysis.Warnings.HasFlag(NegativeWarnings.UnevenIllumination)).IsTrue();
        var options = new NegativeOptions { Calibration = analysis.Calibration, ColorNoiseReduction = 0 };
        using Image even = strip.Clone(x => x.DevelopNegative(options));
        using Image uneven = strip.Clone(x => x.DevelopNegative(options with { CorrectIllumination = false }));
        await Assert.That(Spread(even)).IsLessThan(Spread(uneven) / 4);

        // The green mean of the left and right tenths of the frame: an even print has the same density everywhere.
        static double Spread(Image image)
        {
            double Mean(int from, int to)
            {
                double sum = 0;
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = from; x < to; x++)
                    {
                        sum += image[x, y].G;
                    }
                }

                return sum / ((to - from) * image.Height);
            }

            return Math.Abs(Mean(0, image.Width / 10) - Mean(image.Width - (image.Width / 10), image.Width));
        }
    }

    [Test]
    public async Task Develop_RejectsAFrameOfAnotherImage()
    {
        using Image strip = Strip();
        FilmFrame frame = strip.FindFilmFrame()!;
        using Image other = new(strip.Width + 1, strip.Height);
        await Assert.That(() => other.Mutate(x => x.DevelopNegative(new NegativeOptions { Frame = frame }))).Throws<ArgumentException>();
        await Assert.That(() => other.AnalyzeNegative(new NegativeOptions { Frame = frame })).Throws<ArgumentException>();
    }

    [Test]
    public async Task Find_RejectsAnimations_AndStopsWhenCanceled()
    {
        using Image animation = new(64, 64);
        using (Image second = new(64, 64))
        {
            animation.Frames.Add(second, TimeSpan.FromMilliseconds(50));
        }
        await Assert.That(() => animation.FindFilmFrame()).Throws<NotSupportedException>();
        using Image strip = Strip();
        await Assert.That(() => strip.FindFilmFrame(new CancellationToken(true))).Throws<OperationCanceledException>();
    }
}
