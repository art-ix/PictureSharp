using System.Drawing;
using System.Text.Json;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.ColorManagement;

/// <summary>Linear-light resize and compositing against Pillow float resampling and a double-precision model.</summary>
public class LinearLightTests
{
    private static JsonElement Cases => IccConversionTests.Manifest.GetProperty("linear_light").GetProperty("cases");

    public static IEnumerable<string> Names() => Cases.EnumerateObject().Select(p => p.Name);

    private static Image LoadInput(string name) =>
        Image.Load(name == "checker" ? IccConversionTests.Color("checker.png") : Corpus.PathOf($"src/{name}"));

    [Test]
    [MethodDataSource(nameof(Names))]
    public async Task LinearLight_MatchesReference(string name)
    {
        JsonElement c = Cases.GetProperty(name);
        using Image image = LoadInput(c.GetProperty("input").GetString()!);
        if (c.GetProperty("op").GetString() == "resize")
        {
            var filter = c.GetProperty("filter").GetString() switch
            {
                "lanczos" => ResamplingFilter.Lanczos3,
                "bicubic" => ResamplingFilter.Bicubic,
                "bilinear" => ResamplingFilter.Bilinear,
                "box" => ResamplingFilter.Box,
                _ => ResamplingFilter.Hamming,
            };
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new(c.GetProperty("width").GetInt32(), c.GetProperty("height").GetInt32()), Filter = filter, LinearLight = true,
            }));
        }
        else
        {
            using Image overlay = LoadInput(c.GetProperty("overlay").GetString()!);
            var options = new DrawImageOptions { Opacity = c.GetProperty("opacity").GetSingle(), LinearLight = true };
            image.Mutate(x => x.DrawImage(overlay, new Point(c.GetProperty("x").GetInt32(), c.GetProperty("y").GetInt32()), options));
        }

        using Image expected = Image.Load(IccConversionTests.Color(c.GetProperty("reference").GetString()!));
        await Assert.That(image.Size).IsEqualTo(expected.Size);
        await Assert.That(MaxDifference(image.Frames.RootFrame.GetPixelSpan(), expected.Frames.RootFrame.GetPixelSpan())).IsLessThanOrEqualTo(1).Because(name);
    }

    /// <summary>The largest per-channel difference including alpha, which float rounding may also move by one.</summary>
    private static int MaxDifference(ReadOnlySpan<Rgba32> a, ReadOnlySpan<Rgba32> e)
    {
        int worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(a[i].R - e[i].R), Math.Abs(a[i].G - e[i].G)),
                Math.Max(Math.Abs(a[i].B - e[i].B), Math.Abs(a[i].A - e[i].A))));
        }

        return worst;
    }

    [Test]
    public async Task LinearResize_AverageOfBlackAndWhite_IsHalfTheLight()
    {
        using Image image = Image.Load(IccConversionTests.Color("checker.png"));
        using Image encoded = image.Clone(x => x.Resize(new ResizeOptions { Size = new(1, 1), Filter = ResamplingFilter.Box }));
        using Image linear = image.Clone(x => x.Resize(new ResizeOptions { Size = new(1, 1), Filter = ResamplingFilter.Box, LinearLight = true }));
        await Assert.That(encoded[0, 0].R).IsEqualTo((byte)128);
        await Assert.That(linear[0, 0]).IsEqualTo(new Rgba32(188, 188, 188));
    }

    [Test]
    public async Task LinearResize_TransparentNeighbors_DoNotTintEdges()
    {
        // Opaque red next to transparent green: premultiplication keeps the green out of the edge pixels.
        using var image = new Image(8, 1);
        for (int x = 0; x < 8; x++) image[x, 0] = x < 4 ? new Rgba32(255, 0, 0) : new Rgba32(0, 255, 0, 0);
        image.Mutate(x => x.Resize(new ResizeOptions { Size = new(3, 1), LinearLight = true }));
        foreach (int x in new[] { 0, 1, 2 })
        {
            Rgba32 p = image[x, 0];
            if (p.A > 0) await Assert.That((p.G, p.B)).IsEqualTo(((byte)0, (byte)0)).Because($"pixel {x}");
        }
    }

    [Test]
    public async Task LinearOptionsOff_KeepPillowResults()
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo_alpha.png"));
        using Image byOptions = image.Clone(x => x.Resize(new ResizeOptions { Size = new(67, 53) }));
        using Image byOverload = image.Clone(x => x.Resize(67, 53));
        await Assert.That(byOptions.ToPixelData()).IsEquivalentTo(byOverload.ToPixelData(), CollectionOrdering.Matching);

        using Image overlay = Image.Load(Corpus.PathOf("src/odd_alpha.png"));
        using Image a = image.Clone(x => x.DrawImage(overlay, new Point(3, 4), 0.5f));
        using Image b = image.Clone(x => x.DrawImage(overlay, new Point(3, 4), new DrawImageOptions { Opacity = 0.5f }));
        await Assert.That(a.ToPixelData()).IsEquivalentTo(b.ToPixelData(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task LinearComposite_TransparentSourceLeavesDestination()
    {
        using var image = new Image(2, 1);
        image[0, 0] = new Rgba32(10, 20, 30, 0);
        image[1, 0] = new Rgba32(40, 50, 60, 128);
        using var overlay = new Image(2, 1); // Fully transparent.
        image.Mutate(x => x.DrawImage(overlay, Point.Empty, new DrawImageOptions { LinearLight = true }));
        await Assert.That(image[0, 0]).IsEqualTo(new Rgba32(10, 20, 30, 0));
        await Assert.That(image[1, 0]).IsEqualTo(new Rgba32(40, 50, 60, 128));
    }

    [Test]
    public async Task DrawImageOptions_ValidatesOpacity()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new DrawImageOptions { Opacity = 1.5f });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new DrawImageOptions { Opacity = float.NaN });
        await Assert.That(DrawImageOptions.Default.Opacity).IsEqualTo(1f);
    }
}
