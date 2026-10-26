using PictureSharp.Infrastructure.Codecs.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;
using PictureSharp.Tests.WebP;

namespace PictureSharp.Tests.Png;

public class PngDecoderTests
{
    public static TestCases<string> PngFiles => Corpus.Files("png/");

    [Test]
    [MethodDataSource(nameof(PngFiles))]
    public async Task Decode_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        using Image image = Image.Load(Corpus.PathOf(file));
        await Assert.That(image.SourceFormat).IsEqualTo(ImageFormat.Png);
        await Assert.That(image.HasAlpha).IsEqualTo(r.HasAlpha);
        await LosslessDecoderTests.AssertMatchesReference(file, image);
        if (r.Animated)
        {
            await Assert.That(image.LoopCount).IsEqualTo(r.Loop);
            for (int i = 0; i < r.Frames.Count; i++)
            {
                await Assert.That(Math.Round((double)(image.Frames[i].Duration.TotalMilliseconds), 3)).IsEqualTo(Math.Round((double)(r.Frames[i].Duration), 3));
            }
        }

        if (r.Dpi is { } dpi)
        {
            await Assert.That(Math.Round((double)image.Metadata.HorizontalResolution, 1)).IsEqualTo(Math.Round((double)(dpi[0]), 1));
            await Assert.That(Math.Round((double)image.Metadata.VerticalResolution, 1)).IsEqualTo(Math.Round((double)(dpi[1]), 1));
        }
    }

    [Test]
    [MethodDataSource(nameof(PngFiles))]
    public async Task Identify_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        ImageInfo info = Image.Identify(Corpus.PathOf(file));
        await Assert.That(info.Format).IsEqualTo(ImageFormat.Png);
        await Assert.That((info.Width, info.Height)).IsEqualTo((r.Width, r.Height));
        await Assert.That(info.Color.HasAlpha).IsEqualTo(r.HasAlpha);
        await Assert.That(info.Color.BitsPerSample).IsEqualTo(r.BitDepth);
        await Assert.That(info.FrameCount).IsEqualTo(r.Frames.Count);
        await Assert.That(info.IsAnimated).IsEqualTo(r.Animated);
        await Assert.That((int)info.Orientation).IsEqualTo(r.Orientation);
    }

    [Test]
    public async Task Decode_ExifOrientation()
    {
        using Image image = Image.Load(Corpus.PathOf("png/exif.png"));
        await Assert.That(image.Metadata.Orientation).IsEqualTo(ExifOrientation.BottomRight);
    }

    [Test]
    public async Task Decode_FirstFrameOnly_Apng()
    {
        using Image image = Image.Load(Corpus.PathOf("png/apng_alpha.png"), new DecoderOptions { FirstFrameOnly = true });
        await Assert.That(image.FrameCount).IsEqualTo(1);
        await Assert.That(Corpus.Sha256(System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.GetPixelSpan()))).IsEqualTo(Corpus.References["png/apng_alpha.png"].Frames[0].Sha256);
    }

    [Test]
    public void Decode_StrictChecksums_DetectsCorruption()
    {
        byte[] data = Corpus.Read("png/rgb8.png");
        data[^20] ^= 0x40;  // inside the last IDAT, before IEND
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, new DecoderOptions { Png = new() { StrictChecksums = true }}));
    }

    [Test]
    public async Task BlendOver_FollowsPorterDuff()
    {
        var dst = new Rgba32(0, 0, 255, 255);
        await Assert.That(PictureSharp.Infrastructure.Codecs.Png.PngDecoder.BlendOver(new Rgba32(10, 20, 30, 255), dst)).IsEqualTo(new Rgba32(10, 20, 30, 255));
        await Assert.That(PictureSharp.Infrastructure.Codecs.Png.PngDecoder.BlendOver(new Rgba32(10, 20, 30, 0), dst)).IsEqualTo(dst);
        Rgba32 half = PictureSharp.Infrastructure.Codecs.Png.PngDecoder.BlendOver(new Rgba32(255, 0, 0, 128), dst);
        await Assert.That((int)half.A).IsEqualTo(255);
        await Assert.That((int)half.R).IsBetween(127, 129);
        await Assert.That((int)half.B).IsBetween(126, 128);
        Rgba32 overTransparent = PictureSharp.Infrastructure.Codecs.Png.PngDecoder.BlendOver(new Rgba32(255, 0, 0, 128), default);
        await Assert.That(overTransparent).IsEqualTo(new Rgba32(255, 0, 0, 128));
    }
}
