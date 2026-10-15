using System.Drawing;

namespace PictureSharp.Tests.Core;

public class GeometryTests
{
    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan().ToArray();

    [Theory]
    [InlineData(RotateMode.Rotate90, FlipMode.None)]
    [InlineData(RotateMode.Rotate270, FlipMode.None)]
    [InlineData(RotateMode.Rotate90, FlipMode.Horizontal)]
    [InlineData(RotateMode.Rotate90, FlipMode.Vertical)]
    public void QuarterTurn_SwapsDimensions(RotateMode rotate, FlipMode flip)
    {
        using Image img = ImageTests.Pattern(5, 3);
        img.Mutate(x => x.RotateFlip(rotate, flip));
        Assert.Equal(new Size(3, 5), img.Size);
    }

    [Fact]
    public void Rotate90_MovesCorners()
    {
        using Image img = ImageTests.Pattern(5, 3);
        Rgba32 topLeft = img[0, 0];
        Rgba32 bottomLeft = img[0, 2];
        img.Mutate(x => x.Rotate(RotateMode.Rotate90));
        Assert.Equal(topLeft, img[2, 0]);      // top-left goes to top-right
        Assert.Equal(bottomLeft, img[0, 0]);   // bottom-left goes to top-left
    }

    [Fact]
    public void FourQuarterTurns_AreIdentity()
    {
        using Image img = ImageTests.Pattern(7, 4);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Rotate(RotateMode.Rotate90).Rotate(RotateMode.Rotate90).Rotate(RotateMode.Rotate90).Rotate(RotateMode.Rotate90));
        Assert.Equal(before, Pixels(img));
    }

    [Theory]
    [InlineData(RotateMode.None, FlipMode.Horizontal)]
    [InlineData(RotateMode.None, FlipMode.Vertical)]
    [InlineData(RotateMode.Rotate180, FlipMode.None)]
    [InlineData(RotateMode.Rotate90, FlipMode.Horizontal)]
    [InlineData(RotateMode.Rotate270, FlipMode.Horizontal)]
    public void Involutions_TwiceAreIdentity(RotateMode rotate, FlipMode flip)
    {
        using Image img = ImageTests.Pattern(6, 5);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.RotateFlip(rotate, flip).RotateFlip(rotate, flip));
        Assert.Equal(before, Pixels(img));
    }

    [Fact]
    public void Rotate90_Then270_IsIdentity()
    {
        using Image img = ImageTests.Pattern(6, 5);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Rotate(RotateMode.Rotate90).Rotate(RotateMode.Rotate270));
        Assert.Equal(before, Pixels(img));
    }

    [Fact]
    public void FlipHorizontal_MirrorsRows()
    {
        using Image img = ImageTests.Pattern(4, 2);
        Rgba32 first = img[0, 1];
        img.Mutate(x => x.Flip(FlipMode.Horizontal));
        Assert.Equal(first, img[3, 1]);
    }

    [Fact]
    public void FlipVertical_MirrorsColumns()
    {
        using Image img = ImageTests.Pattern(4, 3);
        Rgba32 first = img[1, 0];
        img.Mutate(x => x.Flip(FlipMode.Vertical));
        Assert.Equal(first, img[1, 2]);
    }

    [Fact]
    public void Rotate90FlipHorizontal_IsTranspose()
    {
        using Image img = ImageTests.Pattern(5, 3);
        using Image t = img.Clone(x => x.RotateFlip(RotateMode.Rotate90, FlipMode.Horizontal));
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
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), a, b);
        anim.Metadata.HorizontalResolution = 300;
        anim.Metadata.VerticalResolution = 72;
        anim.Mutate(x => x.Rotate(RotateMode.Rotate270));
        Assert.All(anim.Frames, f => Assert.Equal((2, 4), (f.Width, f.Height)));
        Assert.Equal(72f, anim.Metadata.HorizontalResolution);
        Assert.Equal(300f, anim.Metadata.VerticalResolution);
    }

    [Fact]
    public void UnknownModes_Throw()
    {
        using Image img = ImageTests.Pattern(2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Rotate((RotateMode)45)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Flip((FlipMode)7)));
    }
}
