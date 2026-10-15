using System.Drawing;
using System.Text.Json;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Graphics;

/// <summary>ApplyMask against the integer model in graphics_reference.py and hand-computed values.</summary>
public class MaskTests
{
    public static IEnumerable<string> Cases() => GraphicsReference.Names("mask");

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task ApplyMask_MatchesReference(string name)
    {
        JsonElement c = GraphicsReference.Cases("mask").GetProperty(name);
        using Image image = GraphicsReference.Load(c.GetProperty("input").GetString()!);
        using Image mask = GraphicsReference.Load(c.GetProperty("mask").GetString()!);
        var options = new MaskOptions
        {
            Channel = c.GetProperty("channel").GetString() == "luminance" ? MaskChannel.Luminance : MaskChannel.Alpha,
            Invert = c.GetProperty("invert").GetBoolean(),
            Outside = c.GetProperty("outside").GetString() == "transparent" ? MaskOutside.Transparent : MaskOutside.Keep,
        };

        image.Mutate(x => x.ApplyMask(mask, new Point(c.GetProperty("x").GetInt32(), c.GetProperty("y").GetInt32()), options));

        using Image expected = GraphicsReference.Load(c.GetProperty("reference").GetString()!);
        await Assert.That(GraphicsReference.MaxDifference(image.GetPixelSpan(), expected.GetPixelSpan()))
            .IsLessThanOrEqualTo(GraphicsReference.Tolerance("mask_max_abs")).Because(name);
    }

    [Test]
    public async Task ApplyMask_MultipliesAlpha_AndKeepsColor()
    {
        using var image = new Image(4, 1);
        image.Mutate(x => x.Fill(new Rgba32(10, 20, 30, 200)));
        image[3, 0] = new Rgba32(7, 8, 9, 0);
        using var mask = new Image(4, 1);
        mask[0, 0] = new Rgba32(0, 0, 0, 255);
        mask[1, 0] = new Rgba32(0, 0, 0, 128);
        mask[2, 0] = new Rgba32(0, 0, 0, 0);
        mask[3, 0] = new Rgba32(0, 0, 0, 255);

        image.Mutate(x => x.ApplyMask(mask));

        // 200 · 128 / 255 = 100.39 → 100; the color of every pixel, also under alpha 0, stays.
        await Assert.That(image[0, 0]).IsEqualTo(new Rgba32(10, 20, 30, 200));
        await Assert.That(image[1, 0]).IsEqualTo(new Rgba32(10, 20, 30, 100));
        await Assert.That(image[2, 0]).IsEqualTo(new Rgba32(10, 20, 30, 0));
        await Assert.That(image[3, 0]).IsEqualTo(new Rgba32(7, 8, 9, 0));
    }

    [Test]
    public async Task ApplyMask_LuminanceAndInvert()
    {
        using var image = new Image(3, 1);
        image.Mutate(x => x.Fill(new Rgba32(1, 2, 3, 255)));
        using var mask = new Image(3, 1);
        mask[0, 0] = new Rgba32(255, 255, 255, 0);  // white, transparent: luminance ignores the mask's alpha
        mask[1, 0] = new Rgba32(255, 0, 0, 255);    // luma 76
        mask[2, 0] = new Rgba32(0, 0, 0, 255);

        using Image lum = image.Clone(x => x.ApplyMask(mask, new MaskOptions { Channel = MaskChannel.Luminance }));
        using Image inverted = image.Clone(x => x.ApplyMask(mask, new MaskOptions { Channel = MaskChannel.Luminance, Invert = true }));

        await Assert.That(lum[0, 0].A).IsEqualTo((byte)255);
        await Assert.That(lum[1, 0].A).IsEqualTo((byte)76);
        await Assert.That(lum[2, 0].A).IsEqualTo((byte)0);
        await Assert.That(inverted[0, 0].A).IsEqualTo((byte)0);
        await Assert.That(inverted[1, 0].A).IsEqualTo((byte)179);
        await Assert.That(inverted[2, 0].A).IsEqualTo((byte)255);
    }

    [Test]
    public async Task ApplyMask_PositionClippingAndOutside()
    {
        using var image = new Image(4, 4);
        image.Mutate(x => x.Fill(new Rgba32(50, 60, 70, 255)));
        using var mask = new Image(3, 3);
        mask.Mutate(x => x.Fill(new Rgba32(0, 0, 0, 0)));

        // The mask hangs over the bottom-right corner: only (2..3, 2..3) is covered.
        using Image keep = image.Clone(x => x.ApplyMask(mask, new Point(2, 2)));
        using Image clear = image.Clone(x => x.ApplyMask(mask, new Point(2, 2), new MaskOptions { Outside = MaskOutside.Transparent }));
        using Image anchored = image.Clone(x => x.ApplyMask(mask, AnchorPosition.TopLeft, new MaskOptions { Invert = true, Outside = MaskOutside.Transparent }));
        using Image away = image.Clone(x => x.ApplyMask(mask, new Point(-10, 40)));

        await Assert.That(keep[1, 1].A).IsEqualTo((byte)255);
        await Assert.That(keep[2, 2].A).IsEqualTo((byte)0);
        await Assert.That(keep[3, 3].A).IsEqualTo((byte)0);
        await Assert.That(clear[1, 1]).IsEqualTo(new Rgba32(50, 60, 70, 0));
        await Assert.That(clear[3, 3].A).IsEqualTo((byte)0);
        await Assert.That(anchored[0, 0].A).IsEqualTo((byte)255);
        await Assert.That(anchored[3, 3].A).IsEqualTo((byte)0);
        await Assert.That(away.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task ApplyMask_AnimationAndSelfMask()
    {
        using var image = new Image(2, 1);
        image[0, 0] = new Rgba32(9, 9, 9, 128);
        image[1, 0] = new Rgba32(9, 9, 9, 255);
        image.Frames.AddClone(image.Frames.RootFrame);
        image.Frames[1].GetPixelSpan().Fill(new Rgba32(1, 1, 1, 200));
        using var mask = new Image(2, 1);
        mask[1, 0] = new Rgba32(0, 0, 0, 255);

        using Image masked = image.Clone(x => x.ApplyMask(mask));
        await Assert.That(masked.Frames[0].GetPixelSpan()[0].A).IsEqualTo((byte)0);
        await Assert.That(masked.Frames[1].GetPixelSpan()[0].A).IsEqualTo((byte)0);
        await Assert.That(masked.Frames[1].GetPixelSpan()[1].A).IsEqualTo((byte)200);

        // The first frame masks every frame, also when the image masks itself: 128² / 255 = 64.25 → 64.
        image.Mutate(x => x.ApplyMask(image));
        await Assert.That(image.Frames[0].GetPixelSpan()[0].A).IsEqualTo((byte)64);
        await Assert.That(image.Frames[1].GetPixelSpan()[0].A).IsEqualTo((byte)100);
        await Assert.That(image.Frames[1].GetPixelSpan()[1].A).IsEqualTo((byte)200);
    }

    [Test]
    public async Task ApplyMask_InvalidArguments_Throw()
    {
        using var image = new Image(1, 1);
        Assert.ThrowsExactly<ArgumentNullException>(() => image.Mutate(x => x.ApplyMask(null!)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MaskOptions { Channel = (MaskChannel)3 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MaskOptions { Outside = (MaskOutside)3 });
        using var mask = new Image(1, 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => image.Mutate(x => x.ApplyMask(mask), cts.Token));
        await Assert.That(image[0, 0]).IsEqualTo(default(Rgba32));
    }
}
