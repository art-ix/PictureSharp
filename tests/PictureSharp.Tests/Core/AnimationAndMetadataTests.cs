using PictureSharp.Domain.Metadata;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

public class AnimationAndMetadataTests
{
    [Test]
    public async Task CreateAnimation_CopiesFramesAndDurations()
    {
        using Image a = ImageTests.Pattern(3, 3);
        using Image b = new(3, 3, System.Drawing.Color.Blue);
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(40), a, b);
        anim.LoopCount = 2;
        await Assert.That(anim.FrameCount).IsEqualTo(2);
        await Assert.That(anim.IsAnimated).IsTrue();
        await Assert.That(anim.LoopCount).IsEqualTo(2);
        foreach (var f in anim.Frames)
        {
            await Assert.That(f.Duration).IsEqualTo(TimeSpan.FromMilliseconds(40));
        }

        await Assert.That(anim.Frames[1][0, 0]).IsEqualTo(Rgba32.FromColor(System.Drawing.Color.Blue));
        await Assert.That(anim[1, 1]).IsEqualTo(a[1, 1]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => anim.LoopCount = -1);

        using Image fromList = Image.CreateAnimation(TimeSpan.FromMilliseconds(10), new List<Image> { a, b, a });
        await Assert.That(fromList.FrameCount).IsEqualTo(3);
        Assert.ThrowsExactly<ArgumentException>(() => Image.CreateAnimation(TimeSpan.FromMilliseconds(10)));
        using Image small = new(2, 2);
        Assert.ThrowsExactly<ArgumentException>(() => Image.CreateAnimation(TimeSpan.FromMilliseconds(10), a, small));
    }

    [Test]
    public async Task CloneFrame_And_ExportFrame_ReturnStillImages()
    {
        using Image a = ImageTests.Pattern(3, 3);
        using Image b = new(3, 3, System.Drawing.Color.Blue);
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(40), a, b, a);
        anim.Metadata.Xmp = [7];

        using Image second = anim.Frames.CloneFrame(1);
        await Assert.That(second.FrameCount).IsEqualTo(1);
        await Assert.That(second[0, 0]).IsEqualTo(new Rgba32(0, 0, 255));
        await Assert.That(second.Metadata.Xmp).IsEquivalentTo(new byte[] { 7 }, CollectionOrdering.Matching);
        await Assert.That(anim.FrameCount).IsEqualTo(3);

        using Image exported = anim.Frames.ExportFrame(1);
        await Assert.That(exported[0, 0]).IsEqualTo(new Rgba32(0, 0, 255));
        await Assert.That(anim.FrameCount).IsEqualTo(2);
        await Assert.That(anim.Frames[1][2, 2]).IsEqualTo(a[2, 2]);
        await Assert.That(anim.Frames.RootFrame).IsSameReferenceAs(anim.Frames[0]);
    }

    [Test]
    public async Task Frames_RejectMismatchedSize_AndKeepOne()
    {
        using var img = new Image(4, 4);
        Assert.ThrowsExactly<ArgumentException>(() => img.Frames.Add(new ImageFrame(3, 4)));
        img.Frames.Add(new ImageFrame(4, 4) { Duration = TimeSpan.FromSeconds(1) });
        img.Frames.RemoveAt(1);
        await Assert.That(img.FrameCount).IsEqualTo(1);
        Assert.ThrowsExactly<InvalidOperationException>(() => img.Frames.RemoveAt(0));
        Assert.ThrowsExactly<ArgumentException>(() => img.Frames.Add(img.Frames[0]));
    }

    [Test]
    public async Task Frames_Move()
    {
        using var img = new Image(1, 1);
        ImageFrame second = new(1, 1);
        img.Frames.Add(second);
        img.Frames.Move(1, 0);
        await Assert.That(img.Frames[0]).IsSameReferenceAs(second);
    }

    private static byte[] Exif(byte orientation, bool bigEndian)
    {
        // TIFF header + IFD0 with a single SHORT Orientation entry.
        return bigEndian
            ? [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, orientation, 0, 0, 0, 0, 0, 0]
            : [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0, 0, 0, 0, 0];
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Exif_ReadAndPatchOrientation(bool bigEndian)
    {
        byte[] exif = Exif(6, bigEndian);
        await Assert.That(ExifReader.GetOrientation(exif)).IsEqualTo(ExifOrientation.RightTop);
        await Assert.That(ExifReader.TrySetOrientation(exif, ExifOrientation.TopLeft)).IsTrue();
        await Assert.That(ExifReader.GetOrientation(exif)).IsEqualTo(ExifOrientation.TopLeft);
    }

    [Test]
    public async Task Exif_JpegPrefixIsStripped_AndGarbageIsTolerated()
    {
        var meta = new ImageMetadata { Exif = [.. "Exif\0\0"u8, .. Exif(3, false)] };
        await Assert.That(meta.Exif![0]).IsEqualTo((byte)'I');
        await Assert.That(meta.Orientation).IsEqualTo(ExifOrientation.BottomRight);
        await Assert.That(ExifReader.GetOrientation([1, 2, 3])).IsEqualTo(ExifOrientation.Unknown);
        await Assert.That(ExifReader.GetOrientation("II*\0\xff\xff\xff\x7f"u8.ToArray())).IsEqualTo(ExifOrientation.Unknown);
    }
}
