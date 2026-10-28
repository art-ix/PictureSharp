using System.Drawing;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Properties of curves, neutral and automatic white balance, dust removal and color-noise reduction; pixel values are
/// checked by <see cref="PhotoAdjustOracleTests"/>.
/// </summary>
public class PhotoCleanupTests
{
    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan<Rgba32>().ToArray();

    private static int Luma(Rgba32 p) => ((2126 * p.R) + (7152 * p.G) + (722 * p.B) + 5000) / 10000;

    /// <summary>
    /// An opaque gradient with mild noise (±3), bright specks at (20, 20) 1 px and (40, 30) 3 × 3, a dark 2 × 2 speck
    /// at (60, 15), a 1 px bright vertical line at x = 80 and a step edge below y = 50.
    /// </summary>
    internal static Image Dusty(int w, int h, int seed)
    {
        var random = new Random(seed);
        var img = new Image(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int v = 90 + (60 * x / w) + random.Next(-3, 4) + (y * 50 / h >= 50 ? 80 : 0);
                img[x, y] = new Rgba32((byte)Math.Clamp(v + 10, 0, 255), (byte)Math.Clamp(v, 0, 255), (byte)Math.Clamp(v - 15, 0, 255), 255);
            }
        }

        void Speck(int x0, int y0, int size, int value)
        {
            for (int y = y0; y < Math.Min(h, y0 + size); y++)
            {
                for (int x = x0; x < Math.Min(w, x0 + size); x++)
                {
                    img[x, y] = new Rgba32((byte)value, (byte)value, (byte)value, 255);
                }
            }
        }

        Speck(20 % w, 20 % h, 1, 250);
        Speck(40 % w, 30 % h, 3, 245);
        Speck(60 % w, 15 % h, 2, 10);
        for (int y = 0; y < h; y++)
        {
            img[80 % w, y] = new Rgba32(240, 240, 240, 255);
        }

        return img;
    }

    /// <summary>
    /// Bright lettering on a dark plate, 280 × 60: the rings, three-quarter rings and bars of
    /// <see cref="FilmNegativeTests.Plate"/> and a bar slanted by 20° at x = 260 (the strokes of a slanted letter run
    /// between the four directions of the dust test), with bright specks of 3 × 3 at (4, 4), (100, 52) and (238, 6),
    /// 2 × 2 at (58, 54) and 1 px at (140, 3).
    /// </summary>
    internal static Image Lettering()
    {
        using Image plate = FilmNegativeTests.Plate(240, 60);
        var img = new Image(280, 60);
        img.GetPixelSpan<Rgba32>().Fill(plate[0, 0]);
        Rgba32 bright = new(235, 225, 170, 255);
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 280; x++)
            {
                if (x < 240)
                {
                    img[x, y] = plate[x, y];
                }
                else if (Math.Abs((x - 260) - ((y - 30) * Math.Tan(Math.PI / 9))) <= 2.5 && Math.Abs(y - 30) <= 22)
                {
                    img[x, y] = bright;
                }
            }
        }

        foreach ((int x0, int y0, int size) in Specks)
        {
            for (int y = y0; y < y0 + size; y++)
            {
                for (int x = x0; x < x0 + size; x++)
                {
                    img[x, y] = bright;
                }
            }
        }

        return img;
    }

    private static readonly (int X, int Y, int Size)[] Specks = [(4, 4, 3), (100, 52, 3), (238, 6, 3), (58, 54, 2), (140, 3, 1)];

    [Test]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(11)]
    public async Task RemoveDust_RemovesSpecks_AndKeepsLetteringLargerThanTheSize(int size)
    {
        using Image img = Lettering();
        using Image clean = img.Clone(x => x.RemoveDust(size));
        foreach ((int x0, int y0, int speck) in Specks)
        {
            Rgba32 p = clean[x0 + (speck / 2), y0 + (speck / 2)];
            await Assert.That(Luma(p) < 80).IsTrue().Because($"speck at ({x0}, {y0}): {p}");
        }

        int strokes = 0, kept = 0;
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 280; x++)
            {
                bool speck = Specks.Any(s => x >= s.X - 1 && x <= s.X + s.Size && y >= s.Y - 1 && y <= s.Y + s.Size);
                if (!speck && Luma(img[x, y]) > 200)
                {
                    strokes++;
                    kept += clean[x, y] == img[x, y] ? 1 : 0;
                }
            }
        }

        await Assert.That(kept).IsEqualTo(strokes).Because($"{kept} of {strokes} stroke pixels unchanged");
    }

    [Test]
    public async Task NeutralSettings_AreNoOps()
    {
        using Image img = ProcessingOracleTests.Noise(9, 7, 3);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Curves([new(0, 0), new(255, 255)]).AutoWhiteBalance(0).ReduceColorNoise(0));
        await Assert.That(Pixels(img)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Curves_TwoPointsInverted_IsInvert_AndOutsideIsConstant()
    {
        using Image img = ProcessingOracleTests.Noise(16, 12, 4);
        using Image inverted = img.Clone(x => x.Curves([new(0, 255), new(255, 0)]));
        using Image expected = img.Clone(x => x.Invert());
        await Assert.That(Pixels(inverted)).IsEquivalentTo(Pixels(expected), CollectionOrdering.Matching);

        using Image gray = new(2, 1);
        gray[0, 0] = new Rgba32(10, 10, 10, 255);
        gray[1, 0] = new Rgba32(250, 250, 250, 255);
        using Image flat = gray.Clone(x => x.Curves([new(50, 100), new(200, 150)]));
        await Assert.That(flat[0, 0]).IsEqualTo(new Rgba32(100, 100, 100, 255));
        await Assert.That(flat[1, 0]).IsEqualTo(new Rgba32(150, 150, 150, 255));
    }

    [Test]
    public async Task Curves_PassThroughPoints_StayMonotone_AndChangeOnlySelectedChannels()
    {
        using var ramp = new Image(256, 1);
        for (int x = 0; x < 256; x++)
        {
            ramp[x, 0] = new Rgba32((byte)x, (byte)x, (byte)x, (byte)(255 - x));
        }

        PointF[] points = [new(0, 0), new(40, 10), new(128, 160), new(200, 230), new(255, 255)];
        using Image curved = ramp.Clone(x => x.Curves(points, ColorChannels.Green));
        foreach (PointF p in points)
        {
            await Assert.That((int)curved[(int)p.X, 0].G).IsEqualTo((int)p.Y);
        }

        for (int x = 0; x < 256; x++)
        {
            Rgba32 p = curved[x, 0];
            await Assert.That(p.R == x && p.B == x && p.A == 255 - x).IsTrue().Because(p.ToString());
            if (x > 0)
            {
                await Assert.That(p.G >= curved[x - 1, 0].G).IsTrue().Because($"x = {x}");
            }
        }
    }

    [Test]
    public async Task Curves_RejectsInvalidPoints()
    {
        using var img = new Image(2, 2);
        await Assert.That(() => img.Mutate(x => x.Curves([new(0, 0)]))).Throws<ArgumentException>();
        await Assert.That(() => img.Mutate(x => x.Curves([new(10, 0), new(10, 5)]))).Throws<ArgumentException>();
        await Assert.That(() => img.Mutate(x => x.Curves([new(0, 0), new(256, 5)]))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => img.Mutate(x => x.Curves([new(0, float.NaN), new(255, 5)]))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => img.Mutate(x => x.Curves([new(0, 0), new(255, 255)], ColorChannels.None))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => img.Mutate(x => x.Curves([new(0, 0), new(255, 255)], (ColorChannels)8))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WhiteBalance_Neutral_BecomesGrayOfTheSameLuminance()
    {
        var neutral = new Rgba32(200, 170, 120, 255);
        using var img = new Image(2, 1);
        img[0, 0] = neutral;
        img[1, 0] = new Rgba32(90, 80, 60, 7);
        using Image balanced = img.Clone(x => x.WhiteBalance(neutral));
        Rgba32 p = balanced[0, 0];
        await Assert.That(Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)) <= 1).IsTrue().Because(p.ToString());
        await Assert.That(Math.Abs(Luma(p) - Luma(neutral)) <= 2).IsTrue().Because(p.ToString());
        await Assert.That(balanced[1, 0].A).IsEqualTo((byte)7);
        await Assert.That(() => img.Mutate(x => x.WhiteBalance(new Rgba32(0, 10, 10, 255)))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task AutoWhiteBalance_RemovesACast_AndPartialStrengthCorrectsLess()
    {
        var random = new Random(5);
        using var img = new Image(64, 48);
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int v = 40 + random.Next(170);
                img[x, y] = new Rgba32((byte)Math.Min(252, v * 6 / 5), (byte)v, (byte)(v * 3 / 4), 255);
            }
        }

        static double Cast(Image image)
        {
            double r = 0, b = 0;
            foreach (Rgba32 p in image.GetPixelSpan<Rgba32>())
            {
                r += p.R;
                b += p.B;
            }

            return (r - b) / image.GetPixelSpan<Rgba32>().Length;
        }

        using Image full = img.Clone(x => x.AutoWhiteBalance());
        using Image half = img.Clone(x => x.AutoWhiteBalance(0.5f));
        await Assert.That(Math.Abs(Cast(full)) < 4).IsTrue().Because($"{Cast(full)}");
        await Assert.That(Cast(half) > Cast(full) + 5 && Cast(half) < Cast(img) - 5).IsTrue().Because($"{Cast(img)} {Cast(half)} {Cast(full)}");
        await Assert.That(() => img.Mutate(x => x.AutoWhiteBalance(1.5f))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task AutoWhiteBalance_WithoutUsablePixels_KeepsTheImage()
    {
        using var img = new Image(8, 8);
        img.GetPixelSpan<Rgba32>().Fill(new Rgba32(255, 120, 0, 255));
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.AutoWhiteBalance());
        await Assert.That(Pixels(img)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AutoWhiteBalance_MeasuresAnAnimationAsAWhole()
    {
        using Image anim = ProcessingOracleTests.Noise(24, 18, 5);
        using Image second = ProcessingOracleTests.Noise(24, 18, 6);
        anim.Frames.Add(second, TimeSpan.FromMilliseconds(10));
        using Image together = anim.Clone(x => x.AutoWhiteBalance());
        using Image alone = second.Clone(x => x.AutoWhiteBalance());
        await Assert.That(together.Frames[1].GetPixelSpan<Rgba32>().SequenceEqual(alone.GetPixelSpan<Rgba32>())).IsFalse();
    }

    [Test]
    public async Task RemoveDust_RemovesSpecks_AndKeepsLinesAndEdges()
    {
        using Image img = Dusty(96, 64, 7);
        using Image clean = img.Clone(x => x.RemoveDust(3, 0.1f));
        foreach ((int x, int y) in new[] { (20, 20), (41, 31), (60, 15), (61, 16) })
        {
            int around = Luma(img[x - 4, y]);
            await Assert.That(Math.Abs(Luma(clean[x, y]) - around) <= 10).IsTrue().Because($"({x}, {y}): {clean[x, y]} vs {around}");
        }

        for (int y = 0; y < 64; y++)
        {
            await Assert.That(clean[80, y]).IsEqualTo(img[80, y]);
            await Assert.That(clean[10, y]).IsEqualTo(img[10, y]);
        }
    }

    [Test]
    public async Task RemoveDust_KeepsAnImageWithoutSpecks_AndValidates()
    {
        using var img = new Image(40, 30);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                img[x, y] = new Rgba32((byte)(x * 6), (byte)(y * 8), 100, (byte)(x * 5));
            }
        }

        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.RemoveDust(5));
        await Assert.That(Pixels(img)).IsEquivalentTo(before, CollectionOrdering.Matching);
        await Assert.That(() => img.Mutate(x => x.RemoveDust(0))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => img.Mutate(x => x.RemoveDust(65))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => img.Mutate(x => x.RemoveDust(3, 0f))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ReduceColorNoise_FlattensChroma_KeepsLuma_AndTransparentColor()
    {
        var random = new Random(11);
        using var img = new Image(48, 40);
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 48; x++)
            {
                int d = random.Next(-20, 21);
                // Opposite red and blue changes with a green that keeps the luma almost constant: pure color noise.
                int r = 128 + d, b = 128 - (3 * d), g = 128 - ((2126 * d) - (3 * 722 * d)) / 7152;
                img[x, y] = new Rgba32((byte)r, (byte)g, (byte)b, (byte)(x == 0 && y == 0 ? 0 : 255));
            }
        }

        using Image smooth = img.Clone(x => x.ReduceColorNoise());
        static double Spread(Image image)
        {
            double sum = 0, square = 0;
            int n = 0;
            foreach (Rgba32 p in image.GetPixelSpan<Rgba32>()[1..])
            {
                sum += p.R - p.B;
                square += (p.R - p.B) * (double)(p.R - p.B);
                n++;
            }

            return Math.Sqrt((square / n) - ((sum / n) * (sum / n)));
        }

        await Assert.That(Spread(smooth) < Spread(img) / 4).IsTrue().Because($"{Spread(img)} → {Spread(smooth)}");
        Rgba32[] before = Pixels(img), after = Pixels(smooth);
        for (int i = 1; i < before.Length; i++)
        {
            await Assert.That(Math.Abs(Luma(after[i]) - Luma(before[i])) <= 1).IsTrue().Because($"{before[i]} → {after[i]}");
        }

        await Assert.That(after[0]).IsEqualTo(before[0]);
        await Assert.That(() => img.Mutate(x => x.ReduceColorNoise(-0.1f))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => img.Mutate(x => x.ReduceColorNoise(1f, -1))).Throws<ArgumentOutOfRangeException>();
    }
}
