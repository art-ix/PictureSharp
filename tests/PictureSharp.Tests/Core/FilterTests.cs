using System.Drawing;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

/// <summary>Properties of the Pillow-derived filters; pixel exactness is checked by <see cref="ProcessingOracleTests"/>.</summary>
public class FilterTests
{
    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan<Rgba32>().ToArray();

    [Test]
    public async Task NeutralAmounts_AreNoOps()
    {
        using Image img = ProcessingOracleTests.Noise(9, 7, 3);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Brightness(1).Contrast(1).Saturate(1).BoxBlur(0).GaussianBlur(0).Rotate(0f).Rotate(360f));
        await Assert.That(Pixels(img)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Enhancers_ReachTheirDegenerateImages()
    {
        using Image img = ImageTests.Pattern(6, 5);
        using Image black = img.Clone(x => x.Brightness(0));
        foreach (var p in Pixels(black).Zip(Pixels(img)))
        {
            await Assert.That(p.First).IsEqualTo(new Rgba32(0, 0, 0, p.Second.A));
        }

        using Image gray = img.Clone(x => x.Saturate(0));
        using Image expected = img.Clone(x => x.Grayscale());
        await Assert.That(Pixels(gray)).IsEquivalentTo(Pixels(expected), CollectionOrdering.Matching);

        using Image flat = img.Clone(x => x.Contrast(0));
        await Assert.That(Pixels(flat).Select(p => (p.R, p.G, p.B)).Distinct()).HasSingleItem();
    }

    [Test]
    public async Task Blur_UniformOpaqueImage_StaysUniform_AndKeepsSize()
    {
        using var img = new Image(23, 11, Color.FromArgb(255, 40, 120, 200));
        img.Mutate(x => x.GaussianBlur(3).BoxBlur(2.5f).UnsharpMask().Sharpen());
        await Assert.That(img.Size).IsEqualTo(new Size(23, 11));
        foreach (var p in Pixels(img))
        {
            await Assert.That(p).IsEqualTo(new Rgba32(40, 120, 200));
        }
    }

    [Test]
    public async Task Blur_DoesNotBleedColorFromTransparentPixels()
    {
        using var img = new Image(20, 6);
        for (int y = 0; y < 6; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                img[x, y] = x < 10 ? new Rgba32(255, 0, 0, 0) : new Rgba32(0, 0, 255);
            }
        }

        img.Mutate(x => x.GaussianBlur(2));
        foreach (var p in Pixels(img))
        {
            await Assert.That(p.A == 0 || p.R == 0).IsTrue().Because(p.ToString());
        }
    }

    [Test]
    public async Task Filters_ApplyToEveryFrame()
    {
        using Image anim = ImageTests.Pattern(8, 8);
        using Image second = ImageTests.Pattern(8, 8);
        anim.Frames.Add(second, TimeSpan.FromMilliseconds(10));
        anim.Mutate(x => x.GaussianBlur(1).Rotate(30));
        foreach (var f in anim.Frames)
        {
            await Assert.That((f.Width, f.Height)).IsEqualTo((anim.Width, anim.Height));
        }

        await Assert.That(anim.Frames[1].GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(anim.Frames[0].GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Rotate_ByQuarterTurns_IsExact_AndExpandGrowsCanvas()
    {
        using Image img = ImageTests.Pattern(7, 4);
        using Image byAngle = img.Clone(x => x.Rotate(90f));
        using Image byMode = img.Clone(x => x.Rotate(RotateMode.Rotate90));
        await Assert.That(Pixels(byAngle)).IsEquivalentTo(Pixels(byMode), CollectionOrdering.Matching);
        using Image ccw = img.Clone(x => x.Rotate(-90f));
        using Image byMode270 = img.Clone(x => x.Rotate(RotateMode.Rotate270));
        await Assert.That(Pixels(ccw)).IsEquivalentTo(Pixels(byMode270), CollectionOrdering.Matching);

        using Image tilted = img.Clone(x => x.Rotate(45f));
        await Assert.That(tilted.Width > 7 && tilted.Height > 4).IsTrue();
        await Assert.That(tilted.HasAlpha).IsTrue();
        await Assert.That(tilted[0, 0]).IsEqualTo(Rgba32.Transparent);

        using Image cut = img.Clone(x => x.Rotate(45f, ResamplingFilter.Nearest, Color.White, expand: false));
        await Assert.That(cut.Size).IsEqualTo(new Size(7, 4));
        await Assert.That(cut[6, 0]).IsEqualTo(Rgba32.White);   // clockwise: the top-right corner maps above the source
    }

    [Test]
    public async Task Sepia_KeepsAlpha_AndTonesWhite()
    {
        using var img = new Image(1, 1, Color.FromArgb(77, 255, 255, 255));
        img.Mutate(x => x.Sepia());
        await Assert.That(img[0, 0]).IsEqualTo(new Rgba32(255, 255, 239, 77));
    }

    [Test]
    public async Task BoxAndHammingResize_Work()
    {
        using Image img = ProcessingOracleTests.Noise(40, 30, 4);
        using Image box = img.Clone(x => x.Resize(10, 0, ResamplingFilter.Box));
        using Image hamming = img.Clone(x => x.Resize(10, 0, ResamplingFilter.Hamming));
        await Assert.That(box.Size).IsEqualTo(new Size(10, 8));
        await Assert.That(hamming.Size).IsEqualTo(new Size(10, 8));
    }

    [Test]
    public void InvalidArguments_Throw()
    {
        using Image img = ImageTests.Pattern(4, 4);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.GaussianBlur(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.BoxBlur(float.NaN)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Brightness(float.PositiveInfinity)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.UnsharpMask(1, -5)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Rotate(10f, ResamplingFilter.Lanczos3)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Rotate(float.NaN)));
    }
}
