using System.Text.Json;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Graphics;

/// <summary>AutoContrast and Equalize against Pillow 10.2.0 <c>ImageOps</c> (bit-exact).</summary>
public class AutoToneTests
{
    public static IEnumerable<string> Cases() =>
        GraphicsReference.Names("tone", c => c.GetProperty("op").GetString() is "autocontrast" or "equalize");

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task AutoTone_MatchesPillow(string name)
    {
        JsonElement c = GraphicsReference.Cases("tone").GetProperty(name);
        using Image image = GraphicsReference.Load(c.GetProperty("input").GetString()!);
        if (c.GetProperty("op").GetString() == "equalize")
        {
            image.Mutate(x => x.Equalize());
        }
        else
        {
            image.Mutate(x => x.AutoContrast(c.GetProperty("cutoff_low").GetSingle(), c.GetProperty("cutoff_high").GetSingle(),
                c.GetProperty("preserve_tone").GetBoolean()));
        }

        using Image expected = GraphicsReference.Load(c.GetProperty("reference").GetString()!);
        await Assert.That(GraphicsReference.MaxDifference(image.GetPixelSpan(), expected.GetPixelSpan()))
            .IsLessThanOrEqualTo(GraphicsReference.Tolerance("tone_max_abs")).Because(name);
    }

    [Test]
    public async Task AutoContrast_StretchesEachChannel()
    {
        using var image = new Image(2, 1);
        image[0, 0] = new Rgba32(50, 10, 0, 7);
        image[1, 0] = new Rgba32(150, 20, 0, 200);

        image.Mutate(x => x.AutoContrast());

        // Red 50..150 and green 10..20 stretch to 0..255; blue has a single value and stays; alpha is kept.
        await Assert.That(image[0, 0]).IsEqualTo(new Rgba32(0, 0, 0, 7));
        await Assert.That(image[1, 0]).IsEqualTo(new Rgba32(255, 255, 0, 200));
    }

    [Test]
    public async Task AutoContrast_PreserveTone_UsesOneMapping()
    {
        using var image = new Image(2, 1);
        image[0, 0] = new Rgba32(50, 50, 50);
        image[1, 0] = new Rgba32(150, 150, 100);
        using Image result = image.Clone(x => x.AutoContrast(preserveTone: true));

        // Luma 50 and 144 map to 0 and 255; every channel goes through (x − 50) · 255 / 94.
        await Assert.That(result[0, 0]).IsEqualTo(new Rgba32(0, 0, 0));
        await Assert.That(result[1, 0]).IsEqualTo(new Rgba32(255, 255, 135));
    }

    [Test]
    public async Task Equalize_ClipsLargeSteps_AsPillow()
    {
        // 509 black pixels and one white: the step is 1 and the cumulative count exceeds 255; Pillow clips to 255.
        using var image = new Image(510, 1);
        image.Mutate(x => x.Fill(new Rgba32(0, 0, 0)));
        image[509, 0] = new Rgba32(255, 255, 255);
        image.Mutate(x => x.Equalize());
        await Assert.That(image[0, 0]).IsEqualTo(new Rgba32(0, 0, 0));
        await Assert.That(image[509, 0]).IsEqualTo(new Rgba32(255, 255, 255));
    }

    [Test]
    public async Task AutoTone_Animation_SharesOneMapping()
    {
        using var image = new Image(1, 1);
        image[0, 0] = new Rgba32(100, 100, 100);
        image.Frames.AddClone(image.Frames.RootFrame);
        image.Frames[1].GetPixelSpan()[0] = new Rgba32(200, 200, 200);

        image.Mutate(x => x.AutoContrast());

        // As Pillow: 200 · 2.55 − 100 · 2.55 = 254.99… truncates to 254.
        await Assert.That(image.Frames[0].GetPixelSpan()[0]).IsEqualTo(new Rgba32(0, 0, 0));
        await Assert.That(image.Frames[1].GetPixelSpan()[0]).IsEqualTo(new Rgba32(254, 254, 254));
    }

    [Test]
    public async Task AutoTone_UniformImage_IsUnchanged()
    {
        using var image = new Image(3, 3);
        image.Mutate(x => x.Fill(new Rgba32(40, 80, 120, 90)));
        using Image result = image.Clone(x => x.AutoContrast(2).Equalize());
        await Assert.That(result.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task AutoContrast_InvalidCutoff_Throws()
    {
        using var image = new Image(1, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.AutoContrast(-1f)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.AutoContrast(0f, 101f)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.AutoContrast(float.NaN)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => image.Mutate(x => x.Equalize(), cts.Token));
    }
}
