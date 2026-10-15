using PictureSharp.Formats.WebP;
using PictureSharp.IO;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class WebPIdentifyTests
{
    public static TheoryData<string> WebPFiles => Corpus.Files("webp/");

    [Theory]
    [MemberData(nameof(WebPFiles))]
    public void Identify_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        ImageInfo info = ImageReader.Identify(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.WebP, info.Format);
        Assert.Equal(r.Width, info.Width);
        Assert.Equal(r.Height, info.Height);
        Assert.Equal(r.HasAlpha, info.HasAlpha);
        Assert.Equal(r.Animated, info.IsAnimated);
        Assert.Equal(r.Frames.Count, info.FrameCount);
        Assert.Equal(r.Compression, info.WebPCompression.ToString());
    }

    [Fact]
    public void Identify_ReadsExifOrientation()
    {
        ImageInfo info = ImageReader.Identify(Corpus.PathOf("webp/meta/lossless_exif.webp"));
        Assert.Equal(ExifOrientation.RightTop, info.Orientation);
    }
}
