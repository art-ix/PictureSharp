using System.Runtime.InteropServices;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Pcx;

public class PcxCorpusTests
{
    public static TestCases<string> PcxFiles => Corpus.Files("pcx/");

    [Test]
    public async Task EveryCorpusFile_HasAReference()
    {
        string[] files = Directory.EnumerateFiles(Path.Combine(Corpus.CorpusDir, "pcx"), "*.pcx")
            .Select(file => Path.GetRelativePath(Corpus.CorpusDir, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        await Assert.That(files).IsNotEmpty();
        await Assert.That(Corpus.References.Keys.Where(file => file.StartsWith("pcx/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray()).IsEquivalentTo(files, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(PcxFiles))]
    public async Task Decode_MatchesReference(string file)
    {
        ReferenceEntry reference = Corpus.References[file];
        using Image image = Image.Load(Corpus.PathOf(file));
        await Assert.That(image.SourceFormat).IsEqualTo(ImageFormat.Pcx);
        await Assert.That((image.Width, image.Height)).IsEqualTo((reference.Width, reference.Height));
        await Assert.That(image.HasAlpha).IsEqualTo(reference.HasAlpha);
        await Assert.That(image.FrameCount).IsEqualTo(reference.Frames.Count);
        await Assert.That(Corpus.Sha256(MemoryMarshal.AsBytes(image.GetPixelSpan<Rgba32>()))).IsEqualTo(reference.Frames[0].Sha256);
        if (reference.Dpi is { } dpi)
        {
            await Assert.That(image.Metadata.HorizontalResolution).IsEqualTo(dpi[0]);
            await Assert.That(image.Metadata.VerticalResolution).IsEqualTo(dpi[1]);
        }
    }

    [Test]
    [MethodDataSource(nameof(PcxFiles))]
    public async Task Identify_MatchesReference(string file)
    {
        ReferenceEntry reference = Corpus.References[file];
        ImageInfo info = Image.Identify(Corpus.PathOf(file));
        await Assert.That(info.Format).IsEqualTo(ImageFormat.Pcx);
        await Assert.That((info.Width, info.Height)).IsEqualTo((reference.Width, reference.Height));
        await Assert.That(info.Color.HasAlpha).IsEqualTo(reference.HasAlpha);
        await Assert.That(info.FrameCount).IsEqualTo(reference.Frames.Count);
        await Assert.That(info.IsAnimated).IsEqualTo(reference.Animated);
        if (reference.Dpi is { } dpi)
        {
            await Assert.That(info.Resolution).IsEqualTo(new ImageResolution(dpi[0], dpi[1]));
        }
    }
}
