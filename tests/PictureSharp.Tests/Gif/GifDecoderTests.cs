using System.Runtime.InteropServices;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;
using PictureSharp.Tests.WebP;

namespace PictureSharp.Tests.Gif;

public class GifDecoderTests
{
    public static TestCases<string> GifFiles => Corpus.Files("gif/");

    private static readonly Rgba32 Red = new(255, 0, 0);
    private static readonly Rgba32 Green = new(0, 255, 0);
    private static readonly Rgba32 Blue = new(0, 0, 255);

    [Test]
    [MethodDataSource(nameof(GifFiles))]
    public async Task Decode_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        using Image image = Image.Load(Corpus.PathOf(file));
        await Assert.That(image.SourceFormat).IsEqualTo(ImageFormat.Gif);
        await Assert.That(image.HasAlpha).IsEqualTo(r.HasAlpha);
        await LosslessDecoderTests.AssertMatchesReference(file, image);
        await Assert.That(image.LoopCount).IsEqualTo(r.Animated ? r.Loop : 0);
        for (int i = 0; i < r.Frames.Count; i++)
        {
            await Assert.That(Math.Round((double)(image.Frames[i].Duration.TotalMilliseconds), 3)).IsEqualTo(Math.Round((double)(r.Frames[i].Duration), 3));
        }

        Rgba32 background = image.AnimationBackgroundColor;
        await Sequence.AssertEqual(r.Background, new[] { background.R, background.G, background.B, background.A }.Select(v => (int)v).ToArray());
        await Assert.That(image.Metadata.Xmp is { } xmp ? Corpus.Sha256(xmp) : null).IsEqualTo(r.XmpSha256);
        await Assert.That(image.Metadata.IccProfile is { } icc ? Corpus.Sha256(icc) : null).IsEqualTo(r.IccSha256);
    }

    [Test]
    [MethodDataSource(nameof(GifFiles))]
    public async Task Identify_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        ImageInfo info = Image.Identify(Corpus.PathOf(file));
        await Assert.That(info.Format).IsEqualTo(ImageFormat.Gif);
        await Assert.That((info.Width, info.Height)).IsEqualTo((r.Width, r.Height));
        await Assert.That(info.HasAlpha).IsEqualTo(r.HasAlpha);
        await Assert.That(info.FrameCount).IsEqualTo(r.Frames.Count);
        await Assert.That(info.IsAnimated).IsEqualTo(r.Animated);
    }

    [Test]
    public async Task Decode_FirstFrameOnly_And_IgnoreMetadata()
    {
        const string file = "gif/hand_extensions.gif";
        using Image image = Image.Load(Corpus.PathOf(file), new DecoderOptions { FirstFrameOnly = true, IgnoreMetadata = true });
        await Assert.That(image.FrameCount).IsEqualTo(1);
        await Assert.That(image.LoopCount).IsEqualTo(0);
        Assert.Null(image.Metadata.Xmp);
        Assert.Null(image.Metadata.IccProfile);
        await Assert.That(Corpus.Sha256(MemoryMarshal.AsBytes(image.GetPixelSpan()))).IsEqualTo(Corpus.References[file].Frames[0].Sha256);
    }

    [Test]
    public async Task DetectFormat_And_Extension()
    {
        await Assert.That(Image.DetectFormat(Corpus.Read("gif/im_87a.gif"))).IsEqualTo(ImageFormat.Gif);
        await Assert.That(ImageFormat.FromExtension("anim.GIF")).IsEqualTo(ImageFormat.Gif);
        await Assert.That(ImageFormat.FromMimeType("image/gif")).IsEqualTo(ImageFormat.Gif);
        await Assert.That(ImageFormat.Gif.CanDecode).IsTrue();
        await Assert.That(ImageFormat.Gif.CanEncode).IsTrue();
        Assert.Null(Image.DetectFormat("GIF88a\0\0\0\0\0\0\0"u8));
    }

    [Test]
    public async Task Decode_FramePastCanvas_IsClipped()
    {
        byte[] gif = new GifBuilder(4, 4, GifBuilder.Palette4)
            .Image(0, 0, 4, 4, new byte[16])
            .Image(2, 3, 4, 2, [1, 1, 1, 1, 2, 2, 2, 2])
            .ToArray();
        using Image image = Image.Load(gif);
        ImageFrame last = image.Frames[1];
        await Assert.That(last[1, 3]).IsEqualTo(Red);
        await Assert.That(last[2, 3]).IsEqualTo(Green);
        await Assert.That(last[3, 3]).IsEqualTo(Green);
        await Assert.That(last[3, 2]).IsEqualTo(Red);
    }

    [Test]
    public async Task Decode_FrameEntirelyOutsideCanvas_DrawsNothing()
    {
        byte[] gif = new GifBuilder(4, 4, GifBuilder.Palette4)
            .Image(0, 0, 4, 4, new byte[16])
            .Image(10, 10, 2, 2, [1, 1, 1, 1])
            .ToArray();
        using Image image = Image.Load(gif);
        await Assert.That(image.Frames[1].GetPixelSpan().ToArray().All(p => p == Red)).IsTrue();
    }

    [Test]
    public async Task Decode_TruncatedImageData_KeepsDecodedPixels()
    {
        byte[] full = new GifBuilder(64, 64, GifBuilder.Palette4).Image(0, 0, 64, 64, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 % 4)).ToArray()).ToArray();
        using Image complete = Image.Load(full);
        for (int cut = full.Length - 2; cut > 800; cut -= 97)
        {
            using Image partial = Image.Load(full.AsMemory(0, cut));
            ReadOnlySpan<Rgba32> a = complete.GetPixelSpan();
            ReadOnlySpan<Rgba32> b = partial.GetPixelSpan();
            int decoded = a.CommonPrefixLength(b);
            bool transparentTail = b[decoded..].ToArray().All(p => p == default);
            await Assert.That(decoded > 0).IsTrue();
            await Assert.That(transparentTail).IsTrue().Because($"cut {cut}: pixels after the data must stay transparent");
        }
    }

    [Test]
    public async Task Decode_InvalidCode_StopsTheFrame()
    {
        // 3-bit codes: clear (4), literal 1, then 7 although only code 6 can be defined next.
        int codes = 4 | (1 << 3) | (7 << 6);
        byte[] lzw = [2, (byte)codes, (byte)(codes >> 8), 0];
        byte[] gif = new GifBuilder(4, 1, GifBuilder.Palette4).Image(0, 0, 4, 1, [], lzw: lzw, minCodeSize: 2).ToArray();
        using Image image = Image.Load(gif);
        await Assert.That(image[0, 0]).IsEqualTo(Green);
        await Assert.That(image[1, 0]).IsEqualTo(default);
    }

    [Test]
    public async Task Decode_KwKwK_And_Restarts()
    {
        // A long run exercises the code-equals-next case; the encoder resets the table at 4096 codes.
        byte[] indices = Enumerable.Repeat((byte)3, 30000).Concat(Enumerable.Range(0, 20000).Select(i => (byte)(i * 31 % 4))).ToArray();
        byte[] gif = new GifBuilder(500, 100, GifBuilder.Palette4).Image(0, 0, 500, 100, indices).ToArray();
        using Image image = Image.Load(gif);
        for (int i = 0; i < indices.Length; i++)
        {
            await Assert.That(image.GetPixelSpan()[i]).IsEqualTo(GifBuilder.Palette4[indices[i]]);
        }
    }

    [Test]
    public async Task Decode_DisposalAndTransparency()
    {
        byte[] gif = new GifBuilder(3, 1, GifBuilder.Palette4).Loop(0)
            .Control(5, disposal: 0).Image(0, 0, 3, 1, [0, 0, 0])
            .Control(5, transparent: 0, disposal: 3).Image(0, 0, 3, 1, [0, 1, 2])          // restored afterwards
            .Control(5, disposal: 2).Image(1, 0, 1, 1, [3])                               // cleared afterwards
            .Control(5, transparent: 1).Image(0, 0, 3, 1, [1, 1, 2])
            .ToArray();
        using Image image = Image.Load(gif);
        await Assert.That(image.FrameCount).IsEqualTo(4);
        await Assert.That(image.Frames[1].GetPixelSpan().ToArray()).IsEquivalentTo(new[] { Red, Green, Blue }, CollectionOrdering.Matching);
        await Assert.That(image.Frames[2].GetPixelSpan().ToArray()).IsEquivalentTo(new[] { Red, new Rgba32(255, 255, 0), Red }, CollectionOrdering.Matching);
        await Assert.That(image.Frames[3].GetPixelSpan().ToArray()).IsEquivalentTo(new[] { Red, default, Blue }, CollectionOrdering.Matching);
        await Assert.That(image.Frames[3].Duration).IsEqualTo(TimeSpan.FromMilliseconds(50));
        await Assert.That(image.LoopCount).IsEqualTo(0);
        await Assert.That(image.HasAlpha).IsTrue();
    }

    [Test]
    [Arguments(null, 1)]
    [Arguments(0, 0)]
    [Arguments(1, 2)]
    [Arguments(65534, 65535)]
    [Arguments(65535, 65535)]
    public async Task Decode_LoopCount_FollowsGif2WebP(int? repetitions, int plays)
    {
        var builder = new GifBuilder(1, 1, GifBuilder.Palette4);
        if (repetitions is int r)
        {
            builder.Loop(r);
        }

        using Image image = Image.Load(builder.Image(0, 0, 1, 1, [0]).Image(0, 0, 1, 1, [1]).ToArray());
        await Assert.That(image.LoopCount).IsEqualTo(plays);
    }

    [Test]
    public async Task Decode_MaxPixels_CoversAllFrames()
    {
        var builder = new GifBuilder(100, 100, GifBuilder.Palette4);
        for (int i = 0; i < 10; i++)
        {
            builder.Image(0, 0, 1, 1, [1]);
        }

        byte[] gif = builder.ToArray();
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(gif, new DecoderOptions { MaxPixels = 99_999 }));
        using Image first = Image.Load(gif, new DecoderOptions { MaxPixels = 10_000, FirstFrameOnly = true });
        await Assert.That(first.FrameCount).IsEqualTo(1);
        using Image all = Image.Load(gif, new DecoderOptions { MaxPixels = 100_000 });
        await Assert.That(all.FrameCount).IsEqualTo(10);
    }

    public static TestCases<byte[]> CorruptFiles() => new()
    {
        "GIF89a"u8.ToArray(),
        "GIF89a\x04\0\x04\0\x81\0\0\xFF"u8.ToArray(),                          // truncated global color table
        new GifBuilder(4, 4, GifBuilder.Palette4).ToArray(),                  // no image
        new GifBuilder(4, 4, GifBuilder.Palette4).Raw(0x2C, 0, 0, 0, 0, 4, 0).ToArray(false),   // truncated descriptor
        new GifBuilder(1, 1, GifBuilder.Palette4).Image(0, 0, 1, 1, [], lzw: [1, 0, 0], minCodeSize: 12).ToArray(),
        new GifBuilder(1, 1, GifBuilder.Palette4).Image(0, 0, 1, 1, [], lzw: [1, 0, 0], minCodeSize: 0).ToArray(),
    };

    [Test]
    [MethodDataSource(nameof(CorruptFiles))]
    public void Decode_CorruptData_ThrowsImageFormatException(byte[] data)
    {
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data));
    }
}
