using System.Drawing;
using System.Text.Json;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Graphics;

/// <summary>DrawImage blend modes against the double-precision W3C model in graphics_reference.py and hand-computed values.</summary>
public class BlendModeTests
{
    public static IEnumerable<string> Cases() => GraphicsReference.Names("blend");

    private static BlendMode Mode(string name) => Enum.Parse<BlendMode>(name.Replace("_", ""), ignoreCase: true);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Blend_MatchesW3CModel(string name)
    {
        JsonElement c = GraphicsReference.Cases("blend").GetProperty(name);
        using Image image = GraphicsReference.Load(c.GetProperty("input").GetString()!);
        using Image source = GraphicsReference.Load(c.GetProperty("source").GetString()!);
        var options = new DrawImageOptions
        {
            BlendMode = Mode(c.GetProperty("mode").GetString()!),
            Opacity = c.GetProperty("opacity").GetSingle(),
            LinearLight = c.GetProperty("linear_light").GetBoolean(),
        };

        image.Mutate(x => x.DrawImage(source, new Point(c.GetProperty("x").GetInt32(), c.GetProperty("y").GetInt32()), options));

        using Image expected = GraphicsReference.Load(c.GetProperty("reference").GetString()!);
        await Assert.That(GraphicsReference.MaxDifference(image.GetPixelSpan<Rgba32>(), expected.GetPixelSpan<Rgba32>()))
            .IsLessThanOrEqualTo(GraphicsReference.Tolerance("blend_max_abs")).Because(name);
    }

    private static Rgba32 BlendOne(Rgba32 backdrop, Rgba32 source, BlendMode mode, float opacity = 1f)
    {
        using var image = new Image(1, 1);
        image[0, 0] = backdrop;
        using var overlay = new Image(1, 1);
        overlay[0, 0] = source;
        image.Mutate(x => x.DrawImage(overlay, Point.Empty, new DrawImageOptions { BlendMode = mode, Opacity = opacity }));
        return image[0, 0];
    }

    public static IEnumerable<(BlendMode, byte, byte, byte)> OpaqueVectors() =>
    [
        // Opaque pixels, Cb = 0.8 (204) and Cs = 0.4 (102) unless noted; B(Cb, Cs) · 255 rounded half up.
        (BlendMode.Multiply, 204, 102, 82),       // 0.32 → 81.6
        (BlendMode.Screen, 204, 102, 224),        // 0.88 → 224.4
        (BlendMode.Overlay, 204, 102, 194),       // HardLight(0.4, 0.8) = Screen(0.4, 0.6) = 0.76 → 193.8
        (BlendMode.Darken, 204, 102, 102),
        (BlendMode.Lighten, 204, 102, 204),
        (BlendMode.Difference, 204, 102, 102),    // 0.4
        (BlendMode.HardLight, 204, 102, 163),     // 0.8 · 0.8 = 0.64 → 163.2
        (BlendMode.SoftLight, 204, 102, 196),     // 0.8 − 0.2 · 0.8 · 0.2 = 0.768 → 195.84
        (BlendMode.ColorDodge, 102, 60, 133),     // 0.4 / (1 − 60/255) → 133.38
        (BlendMode.ColorBurn, 204, 153, 170),     // 1 − 0.2 / 0.6 → 170
        (BlendMode.Exclusion, 204, 102, 143),     // 0.8 + 0.4 − 0.64 = 0.56 → 142.8
    ];

    [Test]
    [MethodDataSource(nameof(OpaqueVectors))]
    public async Task Blend_OpaqueVectors(BlendMode mode, byte backdrop, byte source, byte expected)
    {
        Rgba32 result = BlendOne(new Rgba32(backdrop, backdrop, backdrop), new Rgba32(source, source, source), mode);
        await Assert.That(result).IsEqualTo(new Rgba32(expected, expected, expected)).Because(mode.ToString());
    }

    [Test]
    public async Task Blend_TransparentBackdrop_ShowsTheSource()
    {
        // αb = 0: Cs' = Cs whatever the mode, so the result is the plain source.
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            Rgba32 result = BlendOne(new Rgba32(255, 255, 255, 0), new Rgba32(30, 60, 90, 128), mode);
            await Assert.That(result).IsEqualTo(new Rgba32(30, 60, 90, 128)).Because(mode.ToString());
        }
    }

    [Test]
    public async Task Blend_SemiTransparent_HandComputed()
    {
        // Multiply, Cb = 0.8 with αb = 0.6 (153), Cs = 0.4 with αs = 128/255 · opacity 0.8 = 0.4016:
        // Cs' = 0.4 · 0.4 + 0.6 · 0.32 = 0.352; αo = αs + 0.6 · (1 − αs) = 0.7606 → 194;
        // Co = (αs · 0.352 + 0.6 · (1 − αs) · 0.8) / αo = 0.5637 → 144.
        Rgba32 result = BlendOne(new Rgba32(204, 204, 204, 153), new Rgba32(102, 102, 102, 128), BlendMode.Multiply, 0.8f);
        await Assert.That(result).IsEqualTo(new Rgba32(144, 144, 144, 194));
    }

    [Test]
    public async Task Blend_TransparentSource_LeavesBackdropUntouched()
    {
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            Rgba32 result = BlendOne(new Rgba32(1, 2, 3, 4), new Rgba32(200, 100, 50, 0), mode);
            await Assert.That(result).IsEqualTo(new Rgba32(1, 2, 3, 4)).Because(mode.ToString());
        }
    }

    [Test]
    public async Task Blend_Normal_KeepsPillowPathByteForByte()
    {
        using Image backdrop = GraphicsReference.Load("photo_alpha.png");
        using Image source = GraphicsReference.Load("source.png");
        using Image legacy = backdrop.Clone(x => x.DrawImage(source, new Point(20, 12), 0.6f));
        using Image normal = backdrop.Clone(x => x.DrawImage(source, new Point(20, 12), new DrawImageOptions { Opacity = 0.6f, BlendMode = BlendMode.Normal }));
        await Assert.That(normal.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(legacy.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Blend_SelfAsSourceAndAnimation()
    {
        using var image = new Image(2, 1);
        image[0, 0] = new Rgba32(102, 102, 102);
        image[1, 0] = new Rgba32(204, 204, 204);
        image.Frames.AddClone(image.Frames.RootFrame);
        image.Frames[1].GetPixelSpan<Rgba32>().Fill(new Rgba32(255, 255, 255));

        // Multiplying the first frame by itself: 0.4² = 0.16 → 41, 0.8² = 0.64 → 163; the second frame gets the first frame's colors.
        image.Mutate(x => x.DrawImage(image, Point.Empty, new DrawImageOptions { BlendMode = BlendMode.Multiply }));
        await Assert.That(image.Frames[0].GetPixelSpan<Rgba32>()[0]).IsEqualTo(new Rgba32(41, 41, 41));
        await Assert.That(image.Frames[0].GetPixelSpan<Rgba32>()[1]).IsEqualTo(new Rgba32(163, 163, 163));
        await Assert.That(image.Frames[1].GetPixelSpan<Rgba32>()[0]).IsEqualTo(new Rgba32(102, 102, 102));
    }

    [Test]
    public async Task Blend_InvalidMode_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DrawImageOptions { BlendMode = (BlendMode)99 });
        await Assert.That(DrawImageOptions.Default.BlendMode).IsEqualTo(BlendMode.Normal);
    }
}
