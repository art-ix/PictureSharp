using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LossyDecoderTests
{
    public static TheoryData<string> LossyFiles => Corpus.Files("webp/lossy/");

    public static TheoryData<string> AlphaFiles => Corpus.Files("webp/alpha/");

    public static TheoryData<string> AnimatedFiles => Corpus.Files("webp/anim/");

    public static TheoryData<string> MetadataFiles => Corpus.Files("webp/meta/");

    [Theory]
    [MemberData(nameof(LossyFiles))]
    public void Lossy_IsBitExactWithDwebp(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file));
        Assert.Equal(Corpus.References[file].HasAlpha, image.HasAlpha);
        Assert.Equal(!image.HasAlpha || image.IsOpaque(), image.IsOpaque());
        LosslessDecoderTests.AssertMatchesReference(file, image);
    }

    [Theory]
    [MemberData(nameof(AlphaFiles))]
    public void LossyWithAlpha_IsBitExactWithDwebp(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file));
        Assert.True(image.HasAlpha);
        LosslessDecoderTests.AssertMatchesReference(file, image);
    }

    [Theory]
    [MemberData(nameof(MetadataFiles))]
    public void WithMetadata_IsBitExactWithDwebp(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file));
        LosslessDecoderTests.AssertMatchesReference(file, image);
        Assert.False(image.Metadata.IsEmpty);
    }

    [Fact]
    public void Metadata_IccAndXmp()
    {
        using Image image = Image.Load(Corpus.PathOf("webp/meta/alpha_icc_xmp.webp"));
        Assert.Equal(File.ReadAllBytes(Corpus.PathOf("webp/meta/icc.bin")), image.Metadata.IccProfile);
        Assert.Equal(File.ReadAllBytes(Corpus.PathOf("webp/meta/xmp.bin")), image.Metadata.Xmp);
        Assert.Null(image.Metadata.Exif);
    }

    [Theory]
    [MemberData(nameof(AnimatedFiles))]
    public void Animation_FramesMatchWebPAnimDecoder(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        using Image image = Image.Load(Corpus.PathOf(file));
        Assert.True(image.IsAnimated);
        Assert.Equal(r.Loop, image.LoopCount);
        LosslessDecoderTests.AssertMatchesReference(file, image);
        for (int i = 0; i < r.Frames.Count; i++)
        {
            Assert.Equal(r.Frames[i].Duration, image.Frames[i].Duration.TotalMilliseconds);
        }

        if (r.Background is { } bg)
        {
            Rgba32 c = image.AnimationBackgroundColor;
            Assert.Equal(bg, new[] { (int)c.R, c.G, c.B, c.A });
        }
    }

    [Theory]
    [InlineData("webp/anim/alpha_lossy.webp")]
    [InlineData("webp/anim/alpha_lossless.webp")]
    public void Animation_FirstFrameOnly(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file), new DecoderOptions { FirstFrameOnly = true });
        Assert.Equal(1, image.FrameCount);
        Assert.Equal(Corpus.References[file].Frames[0].Sha256,
            Corpus.Sha256(System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Frames[0].GetPixelSpan())));
    }

    [Fact]
    public void Options_NoFancyUpsampling_AndBypassFiltering_StillDecode()
    {
        string file = Corpus.PathOf("webp/lossy/photo_q75.webp");
        using Image fancy = Image.Load(file);
        using Image simple = Image.Load(file, new DecoderOptions { WebP = new() { FancyUpsampling = false }});
        using Image unfiltered = Image.Load(file, new DecoderOptions { WebP = new() { BypassFiltering = true }});
        Assert.NotEqual(fancy.ToPixelData(), simple.ToPixelData());
        Assert.NotEqual(fancy.ToPixelData(), unfiltered.ToPixelData());
        // Point-sampled and fancy upsampling only differ in chroma: luma-heavy differences stay small.
        byte[] a = fancy.ToPixelData(PixelFormat.Gray8);
        byte[] b = simple.ToPixelData(PixelFormat.Gray8);
        double mean = a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
        Assert.True(mean < 3, $"mean luma difference {mean}");
    }

    [Theory]
    [InlineData("webp/lossy/photo_q75.webp")]
    [InlineData("webp/alpha/photo_alpha_a_default.webp")]
    [InlineData("webp/anim/opaque_lossy.webp")]
    public void Truncated_ThrowsFormatException(string file)
    {
        byte[] data = Corpus.Read(file);
        for (int cut = 1; cut < data.Length - 30; cut += Math.Max(1, data.Length / 40))
        {
            byte[] truncated = data[..^cut];
            Assert.Throws<ImageFormatException>(() => Image.Load(truncated));
        }
    }
}
