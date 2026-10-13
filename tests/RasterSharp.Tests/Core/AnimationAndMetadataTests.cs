using RasterSharp.Formats.Metadata;

namespace RasterSharp.Tests.Core;

public class AnimationAndMetadataTests
{
    [Fact]
    public void CreateAnimation_CopiesFramesAndDurations()
    {
        using Image a = ImageTests.Pattern(3, 3);
        using Image b = new(3, 3, System.Drawing.Color.Blue);
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(40), a, b);
        anim.LoopCount = 2;
        Assert.Equal(2, anim.FrameCount);
        Assert.True(anim.IsAnimated);
        Assert.Equal(2, anim.LoopCount);
        Assert.All(anim.Frames, f => Assert.Equal(TimeSpan.FromMilliseconds(40), f.Duration));
        Assert.Equal(Rgba32.FromColor(System.Drawing.Color.Blue), anim.Frames[1][0, 0]);
        Assert.Equal(a[1, 1], anim[1, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => anim.LoopCount = -1);

        using Image fromList = Image.CreateAnimation(TimeSpan.FromMilliseconds(10), new List<Image> { a, b, a });
        Assert.Equal(3, fromList.FrameCount);
        Assert.Throws<ArgumentException>(() => Image.CreateAnimation(TimeSpan.FromMilliseconds(10)));
        using Image small = new(2, 2);
        Assert.Throws<ArgumentException>(() => Image.CreateAnimation(TimeSpan.FromMilliseconds(10), a, small));
    }

    [Fact]
    public void CloneFrame_And_ExportFrame_ReturnStillImages()
    {
        using Image a = ImageTests.Pattern(3, 3);
        using Image b = new(3, 3, System.Drawing.Color.Blue);
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(40), a, b, a);
        anim.Metadata.Xmp = [7];

        using Image second = anim.Frames.CloneFrame(1);
        Assert.Equal(1, second.FrameCount);
        Assert.Equal(new Rgba32(0, 0, 255), second[0, 0]);
        Assert.Equal(new byte[] { 7 }, second.Metadata.Xmp);
        Assert.Equal(3, anim.FrameCount);

        using Image exported = anim.Frames.ExportFrame(1);
        Assert.Equal(new Rgba32(0, 0, 255), exported[0, 0]);
        Assert.Equal(2, anim.FrameCount);
        Assert.Equal(a[2, 2], anim.Frames[1][2, 2]);
        Assert.Same(anim.Frames[0], anim.Frames.RootFrame);
    }

    [Fact]
    public void Frames_RejectMismatchedSize_AndKeepOne()
    {
        using var img = new Image(4, 4);
        Assert.Throws<ArgumentException>(() => img.Frames.Add(new ImageFrame(3, 4)));
        img.Frames.Add(new ImageFrame(4, 4) { Duration = TimeSpan.FromSeconds(1) });
        img.Frames.RemoveAt(1);
        Assert.Equal(1, img.FrameCount);
        Assert.Throws<InvalidOperationException>(() => img.Frames.RemoveAt(0));
        Assert.Throws<ArgumentException>(() => img.Frames.Add(img.Frames[0]));
    }

    [Fact]
    public void Frames_Move()
    {
        using var img = new Image(1, 1);
        ImageFrame second = new(1, 1);
        img.Frames.Add(second);
        img.Frames.Move(1, 0);
        Assert.Same(second, img.Frames[0]);
    }

    private static byte[] Exif(byte orientation, bool bigEndian)
    {
        // TIFF header + IFD0 with a single SHORT Orientation entry.
        return bigEndian
            ? [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, orientation, 0, 0, 0, 0, 0, 0]
            : [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0, 0, 0, 0, 0];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exif_ReadAndPatchOrientation(bool bigEndian)
    {
        byte[] exif = Exif(6, bigEndian);
        Assert.Equal(ExifOrientation.RightTop, ExifReader.GetOrientation(exif));
        Assert.True(ExifReader.TrySetOrientation(exif, ExifOrientation.TopLeft));
        Assert.Equal(ExifOrientation.TopLeft, ExifReader.GetOrientation(exif));
    }

    [Fact]
    public void Exif_JpegPrefixIsStripped_AndGarbageIsTolerated()
    {
        var meta = new ImageMetadata { Exif = [.. "Exif\0\0"u8, .. Exif(3, false)] };
        Assert.Equal((byte)'I', meta.Exif![0]);
        Assert.Equal(ExifOrientation.BottomRight, meta.Orientation);
        Assert.Equal(ExifOrientation.Unknown, ExifReader.GetOrientation([1, 2, 3]));
        Assert.Equal(ExifOrientation.Unknown, ExifReader.GetOrientation("II*\0\xff\xff\xff\x7f"u8.ToArray()));
    }

    [Fact]
    public void AutoOrient_RotatesAndResetsTag()
    {
        using Image img = ImageTests.Pattern(5, 3);
        Rgba32 bottomLeft = img[0, 2];
        img.Metadata.Exif = Exif(6, false); // stored rotated: needs 90° clockwise
        img.Mutate(x => x.AutoOrient());
        Assert.Equal((3, 5), (img.Width, img.Height));
        Assert.Equal(bottomLeft, img[0, 0]);
        Assert.Equal(ExifOrientation.TopLeft, img.Metadata.Orientation);
    }
}
