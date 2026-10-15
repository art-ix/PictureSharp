using System.Drawing;
using System.Text.Json;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Graphics;

/// <summary>AutoCrop and FindTrimBounds against the numpy model (alpha cases cross-checked with Pillow getbbox).</summary>
public class AutoCropTests
{
    public static IEnumerable<string> Cases() => GraphicsReference.Names("trim");

    private static AutoCropOptions Options(JsonElement c)
    {
        var options = new AutoCropOptions
        {
            Mode = c.GetProperty("mode").GetString() == "background" ? AutoCropMode.Background : AutoCropMode.Alpha,
            AlphaThreshold = c.TryGetProperty("alpha_threshold", out JsonElement t) ? t.GetByte() : (byte)0,
            Tolerance = c.TryGetProperty("tolerance", out JsonElement tol) ? tol.GetByte() : (byte)0,
            Padding = c.TryGetProperty("padding", out JsonElement p) ? p.GetInt32() : 0,
        };
        if (c.TryGetProperty("background", out JsonElement bg) && bg.ValueKind == JsonValueKind.Array)
        {
            byte[] v = bg.EnumerateArray().Select(e => e.GetByte()).ToArray();
            options = options with { Background = new Rgba32(v[0], v[1], v[2], v[3]) };
        }

        return options;
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task AutoCrop_MatchesReference(string name)
    {
        JsonElement c = GraphicsReference.Cases("trim").GetProperty(name);
        using Image image = GraphicsReference.Load(c.GetProperty("input").GetString()!);
        AutoCropOptions options = Options(c);
        JsonElement bounds = c.GetProperty("bounds");
        Rectangle? expected = bounds.ValueKind == JsonValueKind.Null
            ? null
            : new Rectangle(bounds[0].GetInt32(), bounds[1].GetInt32(), bounds[2].GetInt32(), bounds[3].GetInt32());

        await Assert.That(image.FindTrimBounds(options)).IsEqualTo(expected).Because(name);

        using Image cropped = image.Clone(x => x.AutoCrop(options));
        Rectangle kept = expected ?? new Rectangle(Point.Empty, image.Size);
        await Assert.That(cropped.Size).IsEqualTo(kept.Size);
        using Image manual = image.Clone(x => x.Crop(kept));
        await Assert.That(cropped.GetPixelSpan().ToArray()).IsEquivalentTo(manual.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task AutoCrop_AlphaThresholdAndPadding_HandComputed()
    {
        using var image = new Image(10, 8);
        image[2, 3] = new Rgba32(255, 0, 0, 255);
        image[6, 5] = new Rgba32(0, 255, 0, 20);

        await Assert.That(image.FindTrimBounds()).IsEqualTo(new Rectangle(2, 3, 5, 3));
        await Assert.That(image.FindTrimBounds(new AutoCropOptions { AlphaThreshold = 20 })).IsEqualTo(new Rectangle(2, 3, 1, 1));
        await Assert.That(image.FindTrimBounds(new AutoCropOptions { Padding = 2 })).IsEqualTo(new Rectangle(0, 1, 9, 7));
        await Assert.That(image.FindTrimBounds(new AutoCropOptions { Padding = 100 })).IsEqualTo(new Rectangle(0, 0, 10, 8));

        image.Mutate(x => x.AutoCrop());
        await Assert.That(image.Size).IsEqualTo(new Size(5, 3));
        await Assert.That(image[0, 0]).IsEqualTo(new Rgba32(255, 0, 0, 255));
        await Assert.That(image[4, 2]).IsEqualTo(new Rgba32(0, 255, 0, 20));
    }

    [Test]
    public async Task AutoCrop_Background_ToleranceAndTransparentColors()
    {
        using var image = new Image(6, 6);
        image.Mutate(x => x.Fill(new Rgba32(250, 250, 250)));
        image[1, 1] = new Rgba32(245, 250, 250);   // within tolerance 5
        image[3, 4] = new Rgba32(100, 100, 100);

        var exact = new AutoCropOptions { Mode = AutoCropMode.Background };
        var tolerant = exact with { Tolerance = 5 };
        await Assert.That(image.FindTrimBounds(exact)).IsEqualTo(new Rectangle(1, 1, 3, 4));
        await Assert.That(image.FindTrimBounds(tolerant)).IsEqualTo(new Rectangle(3, 4, 1, 1));

        // Colored pixels with alpha 0 are invisible and match a transparent background.
        using var transparent = new Image(4, 4);
        transparent[0, 3] = new Rgba32(255, 0, 255, 0);
        transparent[2, 1] = new Rgba32(1, 1, 1, 1);
        await Assert.That(transparent.FindTrimBounds(exact)).IsEqualTo(new Rectangle(2, 1, 1, 1));
    }

    [Test]
    public async Task AutoCrop_NoContent_LeavesImageUnchanged()
    {
        using var empty = new Image(5, 4);
        await Assert.That(empty.FindTrimBounds()).IsNull();
        empty.Mutate(x => x.AutoCrop());
        await Assert.That(empty.Size).IsEqualTo(new Size(5, 4));

        using var uniform = new Image(3, 3);
        uniform.Mutate(x => x.Fill(new Rgba32(9, 9, 9)).AutoCrop(new AutoCropOptions { Mode = AutoCropMode.Background }));
        await Assert.That(uniform.Size).IsEqualTo(new Size(3, 3));
    }

    [Test]
    public async Task AutoCrop_Animation_UsesUnionOfFrames()
    {
        using var image = new Image(10, 10);
        image[2, 2] = new Rgba32(255, 0, 0);
        image.Frames.RootFrame.Duration = TimeSpan.FromMilliseconds(40);
        ImageFrame second = image.Frames.AddClone(image.Frames.RootFrame);
        second.GetPixelSpan().Clear();
        second.GetPixelSpan()[(7 * 10) + 6] = new Rgba32(0, 255, 0);
        second.Duration = TimeSpan.FromMilliseconds(90);

        await Assert.That(image.FindTrimBounds()).IsEqualTo(new Rectangle(2, 2, 5, 6));
        image.Mutate(x => x.AutoCrop());

        await Assert.That(image.Size).IsEqualTo(new Size(5, 6));
        await Assert.That(image.FrameCount).IsEqualTo(2);
        await Assert.That(image.Frames[0].GetPixelSpan()[0]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(image.Frames[1].GetPixelSpan()[(5 * 5) + 4]).IsEqualTo(new Rgba32(0, 255, 0));
        await Assert.That(image.Frames[0].Duration).IsEqualTo(TimeSpan.FromMilliseconds(40));
        await Assert.That(image.Frames[1].Duration).IsEqualTo(TimeSpan.FromMilliseconds(90));
    }

    [Test]
    public async Task AutoCrop_DefaultBackground_IsTopLeftOfFirstFrame()
    {
        using var image = new Image(4, 4);
        image.Mutate(x => x.Fill(new Rgba32(0, 0, 255)));
        image[0, 0] = new Rgba32(255, 255, 255);
        // The top-left pixel itself differs from the rest, so everything but it is content: the box covers the canvas.
        await Assert.That(image.FindTrimBounds(new AutoCropOptions { Mode = AutoCropMode.Background })).IsEqualTo(new Rectangle(0, 0, 4, 4));
        await Assert.That(image.FindTrimBounds(new AutoCropOptions { Mode = AutoCropMode.Background, Background = new Rgba32(0, 0, 255) }))
            .IsEqualTo(new Rectangle(0, 0, 1, 1));
    }

    [Test]
    public async Task AutoCrop_CloneKeepsSource_AndInvalidArgumentsThrow()
    {
        using var image = new Image(6, 6);
        image[3, 3] = new Rgba32(1, 2, 3, 4);
        using Image cropped = image.Clone(x => x.AutoCrop());
        await Assert.That(cropped.Size).IsEqualTo(new Size(1, 1));
        await Assert.That(image.Size).IsEqualTo(new Size(6, 6));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AutoCropOptions { Padding = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AutoCropOptions { Mode = (AutoCropMode)7 });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => image.FindTrimBounds(null, cts.Token));
        Assert.Throws<OperationCanceledException>(() => image.Mutate(x => x.AutoCrop(), cts.Token));
        await Assert.That(image.Size).IsEqualTo(new Size(6, 6));
    }
}
