using RasterSharp.Tests.Infrastructure;
using RasterSharp.Tests.WebP;

namespace RasterSharp.Tests.Png;

public class PngDecoderTests
{
    public static TheoryData<string> PngFiles => Corpus.Files("png/");

    [Theory]
    [MemberData(nameof(PngFiles))]
    public void Decode_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        using Image image = Image.FromFile(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Png, image.RawFormat);
        Assert.Equal(r.HasAlpha, image.HasAlpha);
        LosslessDecoderTests.AssertMatchesReference(file, image);
        if (r.Animated)
        {
            Assert.Equal(r.Loop, image.LoopCount);
            for (int i = 0; i < r.Frames.Count; i++)
            {
                Assert.Equal(r.Frames[i].Duration, image.Frames[i].Duration.TotalMilliseconds, 3);
            }
        }

        if (r.Dpi is { } dpi)
        {
            Assert.Equal(dpi[0], image.HorizontalResolution, 1);
            Assert.Equal(dpi[1], image.VerticalResolution, 1);
        }
    }

    [Theory]
    [MemberData(nameof(PngFiles))]
    public void Identify_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        ImageInfo info = Image.Identify(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Png, info.Format);
        Assert.Equal((r.Width, r.Height), (info.Width, info.Height));
        Assert.Equal(r.HasAlpha, info.HasAlpha);
        Assert.Equal(r.BitDepth, info.BitsPerChannel);
        Assert.Equal(r.Frames.Count, info.FrameCount);
        Assert.Equal(r.Animated, info.IsAnimated);
        Assert.Equal(r.Orientation, (int)info.Orientation);
    }

    [Fact]
    public void Decode_ExifOrientation()
    {
        using Image image = Image.FromFile(Corpus.PathOf("png/exif.png"));
        Assert.Equal(ExifOrientation.BottomRight, image.Metadata.Orientation);
    }

    [Fact]
    public void Decode_FirstFrameOnly_Apng()
    {
        using Image image = Image.FromFile(Corpus.PathOf("png/apng_alpha.png"), new DecoderOptions { FirstFrameOnly = true });
        Assert.Equal(1, image.FrameCount);
        Assert.Equal(Corpus.References["png/apng_alpha.png"].Frames[0].Sha256,
            Corpus.Sha256(System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.GetPixelSpan())));
    }

    [Fact]
    public void Decode_StrictChecksums_DetectsCorruption()
    {
        byte[] data = Corpus.Read("png/rgb8.png");
        data[^20] ^= 0x40;  // inside the last IDAT, before IEND
        Assert.Throws<ImageFormatException>(() => Image.FromBytes(data, new DecoderOptions { StrictChecksums = true }));
    }

    [Fact]
    public void BlendOver_FollowsPorterDuff()
    {
        var dst = new Rgba32(0, 0, 255, 255);
        Assert.Equal(new Rgba32(10, 20, 30, 255), Formats.Png.PngDecoder.BlendOver(new Rgba32(10, 20, 30, 255), dst));
        Assert.Equal(dst, Formats.Png.PngDecoder.BlendOver(new Rgba32(10, 20, 30, 0), dst));
        Rgba32 half = Formats.Png.PngDecoder.BlendOver(new Rgba32(255, 0, 0, 128), dst);
        Assert.Equal(255, half.A);
        Assert.InRange(half.R, 127, 129);
        Assert.InRange(half.B, 126, 128);
        Rgba32 overTransparent = Formats.Png.PngDecoder.BlendOver(new Rgba32(255, 0, 0, 128), default);
        Assert.Equal(new Rgba32(255, 0, 0, 128), overTransparent);
    }

    [Fact]
    public void Decode_RandomCorruption_OnlyThrowsFormatException()
    {
        foreach (string file in new[] { "png/rgba8.png", "png/palette4_trns.png", "png/rgb8_interlaced.png", "png/apng_alpha.png" })
        {
            byte[] original = Corpus.Read(file);
            var rng = new Random(file.Length);
            for (int iteration = 0; iteration < 200; iteration++)
            {
                byte[] data = (byte[])original.Clone();
                int flips = rng.Next(1, 6);
                for (int i = 0; i < flips; i++)
                {
                    data[rng.Next(8, data.Length)] ^= (byte)(1 << rng.Next(8));
                }

                if (rng.Next(4) == 0)
                {
                    data = data[..rng.Next(8, data.Length)];
                }

                try
                {
                    using Image img = Image.FromBytes(data);
                }
                catch (ImageFormatException)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
        }
    }
}
