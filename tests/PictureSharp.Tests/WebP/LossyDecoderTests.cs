using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LossyDecoderTests
{
    public static TestCases<string> LossyFiles => Corpus.Files("webp/lossy/");

    public static TestCases<string> AlphaFiles => Corpus.Files("webp/alpha/");

    public static TestCases<string> AnimatedFiles => Corpus.Files("webp/anim/");

    public static TestCases<string> MetadataFiles => Corpus.Files("webp/meta/");

    [Test]
    [MethodDataSource(nameof(LossyFiles))]
    public async Task Lossy_IsBitExactWithDwebp(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file));
        await Assert.That(image.HasAlpha).IsEqualTo(Corpus.References[file].HasAlpha);
        await Assert.That(image.IsOpaque()).IsEqualTo(!image.HasAlpha || image.IsOpaque());
        await LosslessDecoderTests.AssertMatchesReference(file, image);
    }

    [Test]
    [MethodDataSource(nameof(AlphaFiles))]
    public async Task LossyWithAlpha_IsBitExactWithDwebp(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file));
        await Assert.That(image.HasAlpha).IsTrue();
        await LosslessDecoderTests.AssertMatchesReference(file, image);
    }

    [Test]
    [MethodDataSource(nameof(MetadataFiles))]
    public async Task WithMetadata_IsBitExactWithDwebp(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file));
        await LosslessDecoderTests.AssertMatchesReference(file, image);
        await Assert.That(image.Metadata.IsEmpty).IsFalse();
    }

    [Test]
    public async Task Metadata_IccAndXmp()
    {
        using Image image = Image.Load(Corpus.PathOf("webp/meta/alpha_icc_xmp.webp"));
        await Assert.That(image.Metadata.IccProfile).IsEquivalentTo(File.ReadAllBytes(Corpus.PathOf("webp/meta/icc.bin")), CollectionOrdering.Matching);
        await Assert.That(image.Metadata.Xmp).IsEquivalentTo(File.ReadAllBytes(Corpus.PathOf("webp/meta/xmp.bin")), CollectionOrdering.Matching);
        Assert.Null(image.Metadata.Exif);
    }

    [Test]
    [MethodDataSource(nameof(AnimatedFiles))]
    public async Task Animation_FramesMatchWebPAnimDecoder(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        using Image image = Image.Load(Corpus.PathOf(file));
        await Assert.That(image.IsAnimated).IsTrue();
        await Assert.That(image.LoopCount).IsEqualTo(r.Loop);
        await LosslessDecoderTests.AssertMatchesReference(file, image);
        for (int i = 0; i < r.Frames.Count; i++)
        {
            await Assert.That(image.Frames[i].Duration.TotalMilliseconds).IsEqualTo(r.Frames[i].Duration);
        }

        if (r.Background is { } bg)
        {
            Rgba32 c = image.AnimationBackgroundColor;
            await Assert.That(new[] { (int)c.R, c.G, c.B, c.A }).IsEquivalentTo(bg, CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments("webp/anim/alpha_lossy.webp")]
    [Arguments("webp/anim/alpha_lossless.webp")]
    public async Task Animation_FirstFrameOnly(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file), new DecoderOptions { FirstFrameOnly = true });
        await Assert.That(image.FrameCount).IsEqualTo(1);
        await Assert.That(Corpus.Sha256(System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Frames[0].GetPixelSpan()))).IsEqualTo(Corpus.References[file].Frames[0].Sha256);
    }

    [Test]
    public async Task Options_NoFancyUpsampling_AndBypassFiltering_StillDecode()
    {
        string file = Corpus.PathOf("webp/lossy/photo_q75.webp");
        using Image fancy = Image.Load(file);
        using Image simple = Image.Load(file, new DecoderOptions { WebP = new() { FancyUpsampling = false }});
        using Image unfiltered = Image.Load(file, new DecoderOptions { WebP = new() { BypassFiltering = true }});
        await Assert.That(simple.ToPixelData()).IsNotEquivalentTo(fancy.ToPixelData(), CollectionOrdering.Matching);
        await Assert.That(unfiltered.ToPixelData()).IsNotEquivalentTo(fancy.ToPixelData(), CollectionOrdering.Matching);
        // Point-sampled and fancy upsampling only differ in chroma: luma-heavy differences stay small.
        byte[] a = fancy.ToPixelData(PixelFormat.Gray8);
        byte[] b = simple.ToPixelData(PixelFormat.Gray8);
        double mean = a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
        await Assert.That(mean < 3).IsTrue().Because($"mean luma difference {mean}");
    }

    [Test]
    [Arguments("webp/lossy/photo_q75.webp")]
    [Arguments("webp/alpha/photo_alpha_a_default.webp")]
    [Arguments("webp/anim/opaque_lossy.webp")]
    public void Truncated_ThrowsFormatException(string file)
    {
        byte[] data = Corpus.Read(file);
        for (int cut = 1; cut < data.Length - 30; cut += Math.Max(1, data.Length / 40))
        {
            byte[] truncated = data[..^cut];
            Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(truncated));
        }
    }
}
