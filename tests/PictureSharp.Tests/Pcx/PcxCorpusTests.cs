using System.Runtime.InteropServices;
using PictureSharp.IO;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Pcx;

public class PcxCorpusTests
{
    public static TheoryData<string> PcxFiles => Corpus.Files("pcx/");

    [Fact]
    public void EveryCorpusFile_HasAReference()
    {
        string[] files = Directory.EnumerateFiles(Path.Combine(Corpus.CorpusDir, "pcx"), "*.pcx")
            .Select(file => Path.GetRelativePath(Corpus.CorpusDir, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        Assert.Equal(files, Corpus.References.Keys.Where(file => file.StartsWith("pcx/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [MemberData(nameof(PcxFiles))]
    public void Decode_MatchesReference(string file)
    {
        ReferenceEntry reference = Corpus.References[file];
        using Image image = ImageReader.Load(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Pcx, image.SourceFormat);
        Assert.Equal((reference.Width, reference.Height), (image.Width, image.Height));
        Assert.Equal(reference.HasAlpha, image.HasAlpha);
        Assert.Equal(reference.Frames.Count, image.FrameCount);
        Assert.Equal(reference.Frames[0].Sha256, Corpus.Sha256(MemoryMarshal.AsBytes(image.GetPixelSpan())));
        if (reference.Dpi is { } dpi)
        {
            Assert.Equal(dpi[0], image.Metadata.HorizontalResolution);
            Assert.Equal(dpi[1], image.Metadata.VerticalResolution);
        }
    }

    [Theory]
    [MemberData(nameof(PcxFiles))]
    public void Identify_MatchesReference(string file)
    {
        ReferenceEntry reference = Corpus.References[file];
        ImageInfo info = ImageReader.Identify(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Pcx, info.Format);
        Assert.Equal((reference.Width, reference.Height), (info.Width, info.Height));
        Assert.Equal(reference.HasAlpha, info.HasAlpha);
        Assert.Equal(reference.Frames.Count, info.FrameCount);
        Assert.Equal(reference.Animated, info.IsAnimated);
        if (reference.Dpi is { } dpi)
        {
            Assert.Equal(dpi[0], info.HorizontalResolution);
            Assert.Equal(dpi[1], info.VerticalResolution);
        }
    }
}
