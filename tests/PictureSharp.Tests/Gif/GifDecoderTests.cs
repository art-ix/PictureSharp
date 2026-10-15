using System.Runtime.InteropServices;
using PictureSharp.IO;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;
using PictureSharp.Tests.WebP;

namespace PictureSharp.Tests.Gif;

public class GifDecoderTests
{
    public static TheoryData<string> GifFiles => Corpus.Files("gif/");

    private static readonly Rgba32 Red = new(255, 0, 0);
    private static readonly Rgba32 Green = new(0, 255, 0);
    private static readonly Rgba32 Blue = new(0, 0, 255);

    [Theory]
    [MemberData(nameof(GifFiles))]
    public void Decode_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        using Image image = ImageReader.Load(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Gif, image.SourceFormat);
        Assert.Equal(r.HasAlpha, image.HasAlpha);
        LosslessDecoderTests.AssertMatchesReference(file, image);
        Assert.Equal(r.Animated ? r.Loop : 0, image.LoopCount);
        for (int i = 0; i < r.Frames.Count; i++)
        {
            Assert.Equal(r.Frames[i].Duration, image.Frames[i].Duration.TotalMilliseconds, 3);
        }

        Rgba32 background = image.AnimationBackgroundColor;
        Assert.Equal(r.Background, new[] { background.R, background.G, background.B, background.A }.Select(v => (int)v).ToArray());
        Assert.Equal(r.XmpSha256, image.Metadata.Xmp is { } xmp ? Corpus.Sha256(xmp) : null);
        Assert.Equal(r.IccSha256, image.Metadata.IccProfile is { } icc ? Corpus.Sha256(icc) : null);
    }

    [Theory]
    [MemberData(nameof(GifFiles))]
    public void Identify_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        ImageInfo info = ImageReader.Identify(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Gif, info.Format);
        Assert.Equal((r.Width, r.Height), (info.Width, info.Height));
        Assert.Equal(r.HasAlpha, info.HasAlpha);
        Assert.Equal(r.Frames.Count, info.FrameCount);
        Assert.Equal(r.Animated, info.IsAnimated);
    }

    [Fact]
    public void Decode_FirstFrameOnly_And_IgnoreMetadata()
    {
        const string file = "gif/hand_extensions.gif";
        using Image image = ImageReader.Load(Corpus.PathOf(file), new DecoderOptions { FirstFrameOnly = true, IgnoreMetadata = true });
        Assert.Equal(1, image.FrameCount);
        Assert.Equal(0, image.LoopCount);
        Assert.Null(image.Metadata.Xmp);
        Assert.Null(image.Metadata.IccProfile);
        Assert.Equal(Corpus.References[file].Frames[0].Sha256, Corpus.Sha256(MemoryMarshal.AsBytes(image.GetPixelSpan())));
    }

    [Fact]
    public void DetectFormat_And_Extension()
    {
        Assert.Equal(ImageFormat.Gif, ImageFormats.DetectFormat(Corpus.Read("gif/im_87a.gif")));
        Assert.Equal(ImageFormat.Gif, ImageFormats.FromExtension("anim.GIF"));
        Assert.Equal(ImageFormat.Gif, ImageFormats.FromMimeType("image/gif"));
        Assert.True(ImageFormats.CanDecode(ImageFormat.Gif));
        Assert.True(ImageFormats.CanEncode(ImageFormat.Gif));
        Assert.Null(ImageFormats.DetectFormat("GIF88a\0\0\0\0\0\0\0"u8));
    }

    [Fact]
    public void Decode_FramePastCanvas_IsClipped()
    {
        byte[] gif = new GifBuilder(4, 4, GifBuilder.Palette4)
            .Image(0, 0, 4, 4, new byte[16])
            .Image(2, 3, 4, 2, [1, 1, 1, 1, 2, 2, 2, 2])
            .ToArray();
        using Image image = ImageReader.Load(gif);
        ImageFrame last = image.Frames[1];
        Assert.Equal(Red, last[1, 3]);
        Assert.Equal(Green, last[2, 3]);
        Assert.Equal(Green, last[3, 3]);
        Assert.Equal(Red, last[3, 2]);
    }

    [Fact]
    public void Decode_FrameEntirelyOutsideCanvas_DrawsNothing()
    {
        byte[] gif = new GifBuilder(4, 4, GifBuilder.Palette4)
            .Image(0, 0, 4, 4, new byte[16])
            .Image(10, 10, 2, 2, [1, 1, 1, 1])
            .ToArray();
        using Image image = ImageReader.Load(gif);
        Assert.True(image.Frames[1].GetPixelSpan().ToArray().All(p => p == Red));
    }

    [Fact]
    public void Decode_TruncatedImageData_KeepsDecodedPixels()
    {
        byte[] full = new GifBuilder(64, 64, GifBuilder.Palette4).Image(0, 0, 64, 64, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 % 4)).ToArray()).ToArray();
        using Image complete = ImageReader.Load(full);
        for (int cut = full.Length - 2; cut > 800; cut -= 97)
        {
            using Image partial = ImageReader.Load(full.AsMemory(0, cut));
            ReadOnlySpan<Rgba32> a = complete.GetPixelSpan();
            ReadOnlySpan<Rgba32> b = partial.GetPixelSpan();
            int decoded = a.CommonPrefixLength(b);
            Assert.True(decoded > 0);
            Assert.True(b[decoded..].ToArray().All(p => p == default), $"cut {cut}: pixels after the data must stay transparent");
        }
    }

    [Fact]
    public void Decode_InvalidCode_StopsTheFrame()
    {
        // 3-bit codes: clear (4), literal 1, then 7 although only code 6 can be defined next.
        int codes = 4 | (1 << 3) | (7 << 6);
        byte[] lzw = [2, (byte)codes, (byte)(codes >> 8), 0];
        byte[] gif = new GifBuilder(4, 1, GifBuilder.Palette4).Image(0, 0, 4, 1, [], lzw: lzw, minCodeSize: 2).ToArray();
        using Image image = ImageReader.Load(gif);
        Assert.Equal(Green, image[0, 0]);
        Assert.Equal(default, image[1, 0]);
    }

    [Fact]
    public void Decode_KwKwK_And_Restarts()
    {
        // A long run exercises the code-equals-next case; the encoder resets the table at 4096 codes.
        byte[] indices = Enumerable.Repeat((byte)3, 30000).Concat(Enumerable.Range(0, 20000).Select(i => (byte)(i * 31 % 4))).ToArray();
        byte[] gif = new GifBuilder(500, 100, GifBuilder.Palette4).Image(0, 0, 500, 100, indices).ToArray();
        using Image image = ImageReader.Load(gif);
        for (int i = 0; i < indices.Length; i++)
        {
            Assert.Equal(GifBuilder.Palette4[indices[i]], image.GetPixelSpan()[i]);
        }
    }

    [Fact]
    public void Decode_DisposalAndTransparency()
    {
        byte[] gif = new GifBuilder(3, 1, GifBuilder.Palette4).Loop(0)
            .Control(5, disposal: 0).Image(0, 0, 3, 1, [0, 0, 0])
            .Control(5, transparent: 0, disposal: 3).Image(0, 0, 3, 1, [0, 1, 2])          // restored afterwards
            .Control(5, disposal: 2).Image(1, 0, 1, 1, [3])                               // cleared afterwards
            .Control(5, transparent: 1).Image(0, 0, 3, 1, [1, 1, 2])
            .ToArray();
        using Image image = ImageReader.Load(gif);
        Assert.Equal(4, image.FrameCount);
        Assert.Equal(new[] { Red, Green, Blue }, image.Frames[1].GetPixelSpan().ToArray());
        Assert.Equal(new[] { Red, new Rgba32(255, 255, 0), Red }, image.Frames[2].GetPixelSpan().ToArray());
        Assert.Equal(new[] { Red, default, Blue }, image.Frames[3].GetPixelSpan().ToArray());
        Assert.Equal(TimeSpan.FromMilliseconds(50), image.Frames[3].Duration);
        Assert.Equal(0, image.LoopCount);
        Assert.True(image.HasAlpha);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(65534, 65535)]
    [InlineData(65535, 65535)]
    public void Decode_LoopCount_FollowsGif2WebP(int? repetitions, int plays)
    {
        var builder = new GifBuilder(1, 1, GifBuilder.Palette4);
        if (repetitions is int r)
        {
            builder.Loop(r);
        }

        using Image image = ImageReader.Load(builder.Image(0, 0, 1, 1, [0]).Image(0, 0, 1, 1, [1]).ToArray());
        Assert.Equal(plays, image.LoopCount);
    }

    [Fact]
    public void Decode_MaxPixels_CoversAllFrames()
    {
        var builder = new GifBuilder(100, 100, GifBuilder.Palette4);
        for (int i = 0; i < 10; i++)
        {
            builder.Image(0, 0, 1, 1, [1]);
        }

        byte[] gif = builder.ToArray();
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(gif, new DecoderOptions { MaxPixels = 99_999 }));
        using Image first = ImageReader.Load(gif, new DecoderOptions { MaxPixels = 10_000, FirstFrameOnly = true });
        Assert.Equal(1, first.FrameCount);
        using Image all = ImageReader.Load(gif, new DecoderOptions { MaxPixels = 100_000 });
        Assert.Equal(10, all.FrameCount);
    }

    public static TheoryData<byte[]> CorruptFiles() => new()
    {
        "GIF89a"u8.ToArray(),
        "GIF89a\x04\0\x04\0\x81\0\0\xFF"u8.ToArray(),                          // truncated global color table
        new GifBuilder(4, 4, GifBuilder.Palette4).ToArray(),                  // no image
        new GifBuilder(4, 4, GifBuilder.Palette4).Raw(0x2C, 0, 0, 0, 0, 4, 0).ToArray(false),   // truncated descriptor
        new GifBuilder(1, 1, GifBuilder.Palette4).Image(0, 0, 1, 1, [], lzw: [1, 0, 0], minCodeSize: 12).ToArray(),
        new GifBuilder(1, 1, GifBuilder.Palette4).Image(0, 0, 1, 1, [], lzw: [1, 0, 0], minCodeSize: 0).ToArray(),
    };

    [Theory]
    [MemberData(nameof(CorruptFiles))]
    public void Decode_CorruptData_ThrowsImageFormatException(byte[] data)
    {
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(data));
    }

    [Fact]
    public void Decode_RandomMutations_OnlyThrowFormatExceptions()
    {
        var random = new Random(9);
        foreach (string file in new[] { "gif/pil_anim_dispose2.gif", "gif/hand_extensions.gif", "gif/im_interlace.gif" })
        {
            byte[] original = Corpus.Read(file);
            for (int round = 0; round < 300; round++)
            {
                byte[] data = (byte[])original.Clone();
                int mutations = random.Next(1, 8);
                for (int m = 0; m < mutations; m++)
                {
                    data[random.Next(6, data.Length)] = (byte)random.Next(256);
                }

                int length = random.Next(4) == 0 ? random.Next(6, data.Length) : data.Length;
                try
                {
                    using Image image = ImageReader.Load(data.AsMemory(0, length));
                }
                catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
                {
                }
            }
        }
    }
}
