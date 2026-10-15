using System.Text.Json;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Graphics;

/// <summary>Levels and gamma against the exact double-precision LUT model and hand-computed values.</summary>
public class ToneTests
{
    public static IEnumerable<string> LevelsCases() =>
        GraphicsReference.Names("tone", c => c.GetProperty("op").GetString() is "levels" or "gamma");

    private static ColorChannels Channels(string name) => name switch
    {
        "red" => ColorChannels.Red,
        "green" => ColorChannels.Green,
        "blue" => ColorChannels.Blue,
        _ => ColorChannels.Rgb,
    };

    [Test]
    [MethodDataSource(nameof(LevelsCases))]
    public async Task Levels_MatchesReference(string name)
    {
        JsonElement c = GraphicsReference.Cases("tone").GetProperty(name);
        using Image image = GraphicsReference.Load(c.GetProperty("input").GetString()!);
        float gamma = c.GetProperty("gamma").GetSingle();
        ColorChannels channels = Channels(c.GetProperty("channels").GetString()!);
        if (c.GetProperty("op").GetString() == "gamma")
        {
            image.Mutate(x => x.Gamma(gamma, channels));
        }
        else
        {
            image.Mutate(x => x.Levels(new LevelsOptions
            {
                InputBlack = c.GetProperty("in_black").GetByte(),
                InputWhite = c.GetProperty("in_white").GetByte(),
                Gamma = gamma,
                OutputBlack = c.GetProperty("out_black").GetByte(),
                OutputWhite = c.GetProperty("out_white").GetByte(),
                Channels = channels,
            }));
        }

        using Image expected = GraphicsReference.Load(c.GetProperty("reference").GetString()!);
        await Assert.That(GraphicsReference.MaxDifference(image.GetPixelSpan(), expected.GetPixelSpan()))
            .IsLessThanOrEqualTo(GraphicsReference.Tolerance("tone_max_abs")).Because(name);
    }

    [Test]
    public async Task Levels_HandComputedValues()
    {
        using var image = new Image(5, 1);
        byte[] inputs = [0, 20, 125, 230, 255];
        for (int i = 0; i < inputs.Length; i++) image[i, 0] = new Rgba32(inputs[i], inputs[i], inputs[i], (byte)(i * 60));

        // (x − 20) / 210 stretched to 0..255; 125 → 127.5 rounds half up to 128.
        image.Mutate(x => x.Levels(new LevelsOptions { InputBlack = 20, InputWhite = 230 }));

        byte[] expected = [0, 0, 128, 255, 255];
        for (int i = 0; i < expected.Length; i++)
        {
            await Assert.That(image[i, 0]).IsEqualTo(new Rgba32(expected[i], expected[i], expected[i], (byte)(i * 60)));
        }
    }

    [Test]
    public async Task Levels_OutputRangeAndInversion()
    {
        using var image = new Image(3, 1);
        image[0, 0] = new Rgba32(0, 0, 0);
        image[1, 0] = new Rgba32(128, 128, 128);
        image[2, 0] = new Rgba32(255, 255, 255);

        using Image range = image.Clone(x => x.Levels(new LevelsOptions { OutputBlack = 50, OutputWhite = 150 }));
        using Image inverted = image.Clone(x => x.Levels(new LevelsOptions { OutputBlack = 255, OutputWhite = 0 }));

        // 50 + 100 · 128/255 = 100.196 → 100; 255 − 255 · 128/255 = 127.
        await Assert.That(range[0, 0].R).IsEqualTo((byte)50);
        await Assert.That(range[1, 0].R).IsEqualTo((byte)100);
        await Assert.That(range[2, 0].R).IsEqualTo((byte)150);
        await Assert.That(inverted[0, 0].R).IsEqualTo((byte)255);
        await Assert.That(inverted[1, 0].R).IsEqualTo((byte)127);
        await Assert.That(inverted[2, 0].R).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Gamma_BrightensAboveOne_AndKeepsEndpoints()
    {
        using var image = new Image(3, 1);
        image[0, 0] = new Rgba32(0, 0, 0, 10);
        image[1, 0] = new Rgba32(64, 64, 64, 0);
        image[2, 0] = new Rgba32(255, 255, 255, 200);

        image.Mutate(x => x.Gamma(2f));

        // 255 · (64/255)^0.5 = 127.75 → 128; fully transparent pixels are mapped too, alpha is kept.
        await Assert.That(image[0, 0]).IsEqualTo(new Rgba32(0, 0, 0, 10));
        await Assert.That(image[1, 0]).IsEqualTo(new Rgba32(128, 128, 128, 0));
        await Assert.That(image[2, 0]).IsEqualTo(new Rgba32(255, 255, 255, 200));
    }

    [Test]
    public async Task Gamma_SelectedChannelsOnly()
    {
        using var image = new Image(1, 1);
        image[0, 0] = new Rgba32(64, 64, 64, 128);
        image.Mutate(x => x.Gamma(2f, ColorChannels.Red | ColorChannels.Blue));
        await Assert.That(image[0, 0]).IsEqualTo(new Rgba32(128, 64, 128, 128));
    }

    [Test]
    public async Task Gamma_One_LeavesPixelsAndBufferUntouched()
    {
        using var image = new Image(4, 4);
        image.Mutate(x => x.Fill(new Rgba32(10, 100, 200, 50)));
        using Image clone = image.Clone(x => x.Gamma(1f).Levels(new LevelsOptions()));
        await Assert.That(clone.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Levels_AppliesToEveryFrame_AndCloneKeepsSource()
    {
        using var image = new Image(2, 2);
        image.Mutate(x => x.Fill(new Rgba32(64, 64, 64)));
        image.Frames.AddClone(image.Frames.RootFrame);

        using Image result = image.Clone(x => x.Gamma(2f));

        await Assert.That(result.FrameCount).IsEqualTo(2);
        await Assert.That(result.Frames[0].GetPixelSpan()[0]).IsEqualTo(new Rgba32(128, 128, 128));
        await Assert.That(result.Frames[1].GetPixelSpan()[3]).IsEqualTo(new Rgba32(128, 128, 128));
        await Assert.That(image[0, 0]).IsEqualTo(new Rgba32(64, 64, 64));
    }

    [Test]
    public async Task Levels_InvalidArguments_Throw()
    {
        using var image = new Image(1, 1);
        Assert.ThrowsExactly<ArgumentException>(() => image.Mutate(x => x.Levels(new LevelsOptions { InputBlack = 100, InputWhite = 100 })));
        Assert.ThrowsExactly<ArgumentException>(() => image.Mutate(x => x.Levels(new LevelsOptions { InputBlack = 200, InputWhite = 10 })));
        Assert.ThrowsExactly<ArgumentNullException>(() => image.Mutate(x => x.Levels(null!)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.Gamma(0f)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.Gamma(-1f)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.Gamma(float.NaN)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.Gamma(float.PositiveInfinity)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.Gamma(2f, ColorChannels.None)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Mutate(x => x.Gamma(2f, (ColorChannels)8)));
        await Assert.That(image[0, 0]).IsEqualTo(default(Rgba32));
    }
}
