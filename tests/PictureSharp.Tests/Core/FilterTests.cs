using System.Drawing;

namespace PictureSharp.Tests.Core;

/// <summary>Properties of the Pillow-derived filters; pixel exactness is checked by <see cref="ProcessingOracleTests"/>.</summary>
public class FilterTests
{
    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan().ToArray();

    [Fact]
    public void NeutralAmounts_AreNoOps()
    {
        using Image img = ProcessingOracleTests.Noise(9, 7, 3);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Brightness(1).Contrast(1).Saturate(1).BoxBlur(0).GaussianBlur(0).Rotate(0f).Rotate(360f));
        Assert.Equal(before, Pixels(img));
    }

    [Fact]
    public void Enhancers_ReachTheirDegenerateImages()
    {
        using Image img = ImageTests.Pattern(6, 5);
        using Image black = img.Clone(x => x.Brightness(0));
        Assert.All(Pixels(black).Zip(Pixels(img)), p => Assert.Equal(new Rgba32(0, 0, 0, p.Second.A), p.First));

        using Image gray = img.Clone(x => x.Saturate(0));
        using Image expected = img.Clone(x => x.Grayscale());
        Assert.Equal(Pixels(expected), Pixels(gray));

        using Image flat = img.Clone(x => x.Contrast(0));
        Assert.Single(Pixels(flat).Select(p => (p.R, p.G, p.B)).Distinct());
    }

    [Fact]
    public void Blur_UniformOpaqueImage_StaysUniform_AndKeepsSize()
    {
        using var img = new Image(23, 11, Color.FromArgb(255, 40, 120, 200));
        img.Mutate(x => x.GaussianBlur(3).BoxBlur(2.5f).UnsharpMask().Sharpen());
        Assert.Equal(new Size(23, 11), img.Size);
        Assert.All(Pixels(img), p => Assert.Equal(new Rgba32(40, 120, 200), p));
    }

    [Fact]
    public void Blur_DoesNotBleedColorFromTransparentPixels()
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
        Assert.All(Pixels(img), p => Assert.True(p.A == 0 || p.R == 0, p.ToString()));
    }

    [Fact]
    public void Filters_ApplyToEveryFrame()
    {
        using Image anim = ImageTests.Pattern(8, 8);
        using Image second = ImageTests.Pattern(8, 8);
        anim.Frames.Add(second, TimeSpan.FromMilliseconds(10));
        anim.Mutate(x => x.GaussianBlur(1).Rotate(30));
        Assert.All(anim.Frames, f => Assert.Equal((anim.Width, anim.Height), (f.Width, f.Height)));
        Assert.Equal(anim.Frames[0].GetPixelSpan().ToArray(), anim.Frames[1].GetPixelSpan().ToArray());
    }

    [Fact]
    public void Rotate_ByQuarterTurns_IsExact_AndExpandGrowsCanvas()
    {
        using Image img = ImageTests.Pattern(7, 4);
        using Image byAngle = img.Clone(x => x.Rotate(90f));
        using Image byMode = img.Clone(x => x.Rotate(RotateMode.Rotate90));
        Assert.Equal(Pixels(byMode), Pixels(byAngle));
        using Image ccw = img.Clone(x => x.Rotate(-90f));
        using Image byMode270 = img.Clone(x => x.Rotate(RotateMode.Rotate270));
        Assert.Equal(Pixels(byMode270), Pixels(ccw));

        using Image tilted = img.Clone(x => x.Rotate(45f));
        Assert.True(tilted.Width > 7 && tilted.Height > 4);
        Assert.True(tilted.HasAlpha);
        Assert.Equal(Rgba32.Transparent, tilted[0, 0]);

        using Image cut = img.Clone(x => x.Rotate(45f, ResamplingFilter.Nearest, Color.White, expand: false));
        Assert.Equal(new Size(7, 4), cut.Size);
        Assert.Equal(Rgba32.White, cut[6, 0]);   // clockwise: the top-right corner maps above the source
    }

    [Fact]
    public void Sepia_KeepsAlpha_AndTonesWhite()
    {
        using var img = new Image(1, 1, Color.FromArgb(77, 255, 255, 255));
        img.Mutate(x => x.Sepia());
        Assert.Equal(new Rgba32(255, 255, 239, 77), img[0, 0]);
    }

    [Fact]
    public void BoxAndHammingResize_Work()
    {
        using Image img = ProcessingOracleTests.Noise(40, 30, 4);
        using Image box = img.Clone(x => x.Resize(10, 0, ResamplingFilter.Box));
        using Image hamming = img.Clone(x => x.Resize(10, 0, ResamplingFilter.Hamming));
        Assert.Equal(new Size(10, 8), box.Size);
        Assert.Equal(new Size(10, 8), hamming.Size);
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        using Image img = ImageTests.Pattern(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.GaussianBlur(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.BoxBlur(float.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Brightness(float.PositiveInfinity)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.UnsharpMask(1, -5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Rotate(10f, ResamplingFilter.Lanczos3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Rotate(float.NaN)));
    }
}
