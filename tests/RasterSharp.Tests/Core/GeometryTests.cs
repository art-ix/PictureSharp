using System.Drawing;

namespace RasterSharp.Tests.Core;

public class GeometryTests
{
    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan().ToArray();

    [Theory]
    [InlineData(RotateFlipType.Rotate90FlipNone)]
    [InlineData(RotateFlipType.Rotate270FlipNone)]
    [InlineData(RotateFlipType.Rotate90FlipX)]
    public void QuarterTurn_SwapsDimensions(RotateFlipType type)
    {
        using Image img = ImageTests.Pattern(5, 3);
        img.RotateFlip(type);
        Assert.Equal(new Size(3, 5), img.Size);
    }

    [Fact]
    public void Rotate90_MovesCorners()
    {
        using Image img = ImageTests.Pattern(5, 3);
        Rgba32 topLeft = img[0, 0];
        Rgba32 bottomLeft = img[0, 2];
        img.RotateFlip(RotateFlipType.Rotate90FlipNone);
        Assert.Equal(topLeft, img[2, 0]);      // top-left goes to top-right
        Assert.Equal(bottomLeft, img[0, 0]);   // bottom-left goes to top-left
    }

    [Fact]
    public void FourQuarterTurns_AreIdentity()
    {
        using Image img = ImageTests.Pattern(7, 4);
        Rgba32[] before = Pixels(img);
        for (int i = 0; i < 4; i++)
        {
            img.RotateFlip(RotateFlipType.Rotate90FlipNone);
        }

        Assert.Equal(before, Pixels(img));
    }

    [Theory]
    [InlineData(RotateFlipType.RotateNoneFlipX)]
    [InlineData(RotateFlipType.RotateNoneFlipY)]
    [InlineData(RotateFlipType.Rotate180FlipNone)]
    [InlineData(RotateFlipType.Rotate90FlipX)]
    [InlineData(RotateFlipType.Rotate270FlipX)]
    public void Involutions_TwiceAreIdentity(RotateFlipType type)
    {
        using Image img = ImageTests.Pattern(6, 5);
        Rgba32[] before = Pixels(img);
        img.RotateFlip(type).RotateFlip(type);
        Assert.Equal(before, Pixels(img));
    }

    [Fact]
    public void Rotate90_Then270_IsIdentity()
    {
        using Image img = ImageTests.Pattern(6, 5);
        Rgba32[] before = Pixels(img);
        img.RotateFlip(RotateFlipType.Rotate90FlipNone).RotateFlip(RotateFlipType.Rotate270FlipNone);
        Assert.Equal(before, Pixels(img));
    }

    [Fact]
    public void FlipX_MirrorsRows()
    {
        using Image img = ImageTests.Pattern(4, 2);
        Rgba32 first = img[0, 1];
        img.RotateFlip(RotateFlipType.RotateNoneFlipX);
        Assert.Equal(first, img[3, 1]);
    }

    [Fact]
    public void Rotate90FlipX_IsTranspose()
    {
        using Image img = ImageTests.Pattern(5, 3);
        using Image t = img.Clone();
        t.RotateFlip(RotateFlipType.Rotate90FlipX);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Assert.Equal(img[x, y], t[y, x]);
            }
        }
    }

    [Fact]
    public void RotateFlip_AppliesToAllFrames_AndSwapsDpi()
    {
        using Image a = ImageTests.Pattern(4, 2);
        using Image b = ImageTests.Pattern(4, 2);
        using Image anim = Image.CreateAnimation([a, b], TimeSpan.FromMilliseconds(100));
        anim.SetResolution(300, 72);
        anim.RotateFlip(RotateFlipType.Rotate270FlipNone);
        Assert.All(anim.Frames, f => Assert.Equal((2, 4), (f.Width, f.Height)));
        Assert.Equal(72f, anim.HorizontalResolution);
        Assert.Equal(300f, anim.VerticalResolution);
    }
}
