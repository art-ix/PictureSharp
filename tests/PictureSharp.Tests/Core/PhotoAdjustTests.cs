using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

/// <summary>Properties of the photographic adjustments; pixel values are checked by <see cref="PhotoAdjustOracleTests"/>.</summary>
public class PhotoAdjustTests
{
    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan().ToArray();

    private static Image Solid(int w, int h, Rgba32 color)
    {
        var img = new Image(w, h);
        img.GetPixelSpan().Fill(color);
        return img;
    }

    public static IEnumerable<Func<Action<ImageProcessingContext>>> Operations() =>
    [
        () => x => x.Exposure(0.8f),
        () => x => x.WhiteBalance(0.6f, -0.3f),
        () => x => x.Vibrance(0.7f),
        () => x => x.Vignette(0.9f),
        () => x => x.ShadowsHighlights(0.6f, -0.6f, 2f),
        () => x => x.Clarity(0.8f, 2f),
        () => x => x.Denoise(0.8f, 2),
        () => x => x.Curves([new(0, 20), new(128, 150), new(255, 230)]),
        () => x => x.WhiteBalance(new Rgba32(200, 160, 120, 255)),
        () => x => x.AutoWhiteBalance(),
        () => x => x.ReduceColorNoise(1f, 2),
    ];

    [Test]
    public async Task NeutralAmounts_AreNoOps()
    {
        using Image img = ProcessingOracleTests.Noise(9, 7, 3);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Exposure(0).WhiteBalance(0, 0).Vibrance(0).Vignette(0).Vignette(new VignetteOptions { Midpoint = 0 })
            .ShadowsHighlights(0, 0).Clarity(0).Denoise(0));
        await Assert.That(Pixels(img)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Operations))]
    public async Task Adjustment_KeepsAlpha_ChangesPixels_AndAppliesToEveryFrame(Action<ImageProcessingContext> operation)
    {
        using Image anim = ProcessingOracleTests.Noise(24, 18, 5);
        anim.Frames.Add(ProcessingOracleTests.Noise(24, 18, 5), TimeSpan.FromMilliseconds(10));
        Rgba32[] before = Pixels(anim);
        anim.Mutate(operation);
        Rgba32[] after = Pixels(anim);
        await Assert.That(after.Select(p => p.A)).IsEquivalentTo(before.Select(p => p.A), CollectionOrdering.Matching);
        await Assert.That(after.SequenceEqual(before)).IsFalse();
        await Assert.That(anim.Frames[1].GetPixelSpan().ToArray()).IsEquivalentTo(anim.Frames[0].GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Operations))]
    public async Task Clone_LeavesSourceUnchanged(Action<ImageProcessingContext> operation)
    {
        using Image img = ProcessingOracleTests.Noise(16, 12, 6);
        Rgba32[] before = Pixels(img);
        using Image result = img.Clone(operation);
        await Assert.That(Pixels(img)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Exposure_ScalesLinearLight()
    {
        using Image img = Solid(2, 2, new Rgba32(0, 64, 128, 255));
        using Image up = img.Clone(x => x.Exposure(1));
        // Linear 0.0513 and 0.2158 doubled: 0.1026 → 90 (89.7), 0.4316 → 176 (175.6).
        await Assert.That(up[0, 0]).IsEqualTo(new Rgba32(0, 90, 176, 255));
        using Image white = img.Clone(x => x.Exposure(10));
        await Assert.That(white[1, 1]).IsEqualTo(new Rgba32(0, 255, 255, 255));
        using Image down = up.Clone(x => x.Exposure(-1));
        await Assert.That(down[0, 0]).IsEqualTo(img[0, 0]);
    }

    [Test]
    public async Task WhiteBalance_WarmsAndTints_Gray()
    {
        using Image gray = Solid(1, 1, new Rgba32(128, 128, 128, 255));
        using Image warm = gray.Clone(x => x.WhiteBalance(1));
        await Assert.That(warm[0, 0].R > warm[0, 0].G && warm[0, 0].G > warm[0, 0].B).IsTrue().Because(warm[0, 0].ToString());
        using Image cool = gray.Clone(x => x.WhiteBalance(-1));
        await Assert.That(cool[0, 0].B > cool[0, 0].G && cool[0, 0].G > cool[0, 0].R).IsTrue().Because(cool[0, 0].ToString());
        using Image magenta = gray.Clone(x => x.WhiteBalance(0, 1));
        await Assert.That(magenta[0, 0].R == magenta[0, 0].B && magenta[0, 0].G < magenta[0, 0].R).IsTrue().Because(magenta[0, 0].ToString());
    }

    [Test]
    public async Task Vibrance_BoostsMutedColorsMore_AndMinusOneIsGray()
    {
        using var img = new Image(2, 1);
        img[0, 0] = new Rgba32(140, 120, 110, 255);
        img[1, 0] = new Rgba32(250, 20, 10, 255);
        using Image boosted = img.Clone(x => x.Vibrance(1));
        int Spread(Rgba32 p) => Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B));
        double muted = (double)Spread(boosted[0, 0]) / Spread(img[0, 0]);
        double vivid = (double)Spread(boosted[1, 0]) / Spread(img[1, 0]);
        await Assert.That(muted > 1.5 && vivid < 1.1).IsTrue().Because($"{muted} {vivid}");
        using Image gray = img.Clone(x => x.Vibrance(-1));
        foreach (Rgba32 p in Pixels(gray))
        {
            await Assert.That(p.R == p.G && p.G == p.B).IsTrue().Because(p.ToString());
        }
    }

    [Test]
    public async Task Vignette_KeepsCenter_AndDarkensCorners()
    {
        using Image img = Solid(31, 21, new Rgba32(160, 160, 160, 255));
        using Image dark = img.Clone(x => x.Vignette(1));
        await Assert.That(dark[15, 10]).IsEqualTo(img[15, 10]);
        await Assert.That(dark[0, 0].R < 100 && dark[30, 20].R == dark[0, 0].R).IsTrue().Because(dark[0, 0].ToString());
        using Image light = img.Clone(x => x.Vignette(-1));
        await Assert.That(light[0, 0].R > 200).IsTrue();
        using Image hard = img.Clone(x => x.Vignette(new VignetteOptions { Amount = 1, Midpoint = 0.5f, Feather = 0 }));
        // The corners lie at 0.96 of the half diagonal: the default soft transition has not reached full strength.
        await Assert.That(hard[0, 0].R < dark[0, 0].R && hard[15, 10] == img[15, 10]).IsTrue().Because($"{hard[0, 0]} {dark[0, 0]}");
    }

    [Test]
    public async Task LocalTone_UniformImage_ShadowsLift_ClarityNoOp()
    {
        using Image dark = Solid(12, 10, new Rgba32(30, 40, 50, 255));
        using Image lifted = dark.Clone(x => x.ShadowsHighlights(1, 0));
        await Assert.That(Pixels(lifted).Distinct()).HasSingleItem();
        await Assert.That(lifted[0, 0].R > 30 && lifted[0, 0].B > 50).IsTrue();
        using Image light = Solid(12, 10, new Rgba32(230, 220, 210, 255));
        using Image recovered = light.Clone(x => x.ShadowsHighlights(0, -1));
        await Assert.That(recovered[5, 5].R < 230).IsTrue();
        using Image clear = light.Clone(x => x.Clarity(1));
        await Assert.That(Pixels(clear)).IsEquivalentTo(Pixels(light), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Clarity_IncreasesContrastAcrossAnEdge()
    {
        using var img = new Image(20, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                img[x, y] = x < 10 ? new Rgba32(100, 100, 100, 255) : new Rgba32(150, 150, 150, 255);
            }
        }

        using Image clear = img.Clone(x => x.Clarity(1, 2));
        await Assert.That(clear[9, 0].R < 100 && clear[10, 0].R > 150).IsTrue().Because($"{clear[9, 0]} {clear[10, 0]}");
        await Assert.That(clear[0, 0].R).IsEqualTo((byte)100);
    }

    [Test]
    public async Task Denoise_SmoothsNoise_AndKeepsAStrongEdge()
    {
        var random = new Random(7);
        using var img = new Image(32, 16);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                byte v = (byte)((x < 16 ? 40 : 210) + random.Next(-6, 7));
                img[x, y] = new Rgba32(v, v, v, 255);
            }
        }

        using Image smooth = img.Clone(x => x.Denoise(1, 3));
        static double Deviation(Image image, int from, int to)
        {
            var values = new List<double>();
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = from; x < to; x++)
                {
                    values.Add(image[x, y].R);
                }
            }

            double mean = values.Average();
            return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        }

        await Assert.That(Deviation(smooth, 0, 13) < Deviation(img, 0, 13) / 2).IsTrue();
        await Assert.That(smooth[15, 8].R < 60 && smooth[16, 8].R > 190).IsTrue().Because($"{smooth[15, 8]} {smooth[16, 8]}");
    }

    [Test]
    public async Task Denoise_KeepsColorUnderTransparentPixels()
    {
        using Image img = ProcessingOracleTests.Noise(10, 10, 8);
        using Image smooth = img.Clone(x => x.Denoise(1));
        Rgba32[] before = Pixels(img), after = Pixels(smooth);
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i].A == 0)
            {
                await Assert.That(after[i]).IsEqualTo(before[i]);
            }
        }
    }

    [Test]
    public void InvalidArguments_Throw()
    {
        using Image img = ProcessingOracleTests.Noise(4, 4, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Exposure(float.NaN)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.WhiteBalance(1.1f)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.WhiteBalance(0, float.NaN)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Vibrance(-2)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Vignette(1.5f)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new VignetteOptions { Feather = -0.1f });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new VignetteOptions { Midpoint = float.NaN });
        Assert.ThrowsExactly<ArgumentNullException>(() => img.Mutate(x => x.Vignette(null!)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.ShadowsHighlights(0.5f, 0, -1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Clarity(0.5f, float.PositiveInfinity)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Denoise(1.5f)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Denoise(0.5f, 0)));
    }
}
