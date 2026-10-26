using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class WebPIdentifyTests
{
    public static TestCases<string> WebPFiles => Corpus.Files("webp/");

    [Test]
    [MethodDataSource(nameof(WebPFiles))]
    public async Task Identify_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        ImageInfo info = Image.Identify(Corpus.PathOf(file));
        await Assert.That(info.Format).IsEqualTo(ImageFormat.WebP);
        await Assert.That(info.Width).IsEqualTo(r.Width);
        await Assert.That(info.Height).IsEqualTo(r.Height);
        await Assert.That(info.Color.HasAlpha).IsEqualTo(r.HasAlpha);
        await Assert.That(info.IsAnimated).IsEqualTo(r.Animated);
        await Assert.That(info.FrameCount).IsEqualTo(r.Frames.Count);
        await Assert.That(info.Compression.ToString()).IsEqualTo(r.Compression);
    }

    [Test]
    public async Task Identify_ReadsExifOrientation()
    {
        ImageInfo info = Image.Identify(Corpus.PathOf("webp/meta/lossless_exif.webp"));
        await Assert.That(info.Orientation).IsEqualTo(ExifOrientation.RightTop);
    }
}
