using System.Runtime.InteropServices;
using RasterSharp.Tests.Infrastructure;

namespace RasterSharp.Tests.WebP;

public class LosslessDecoderTests
{
    public static TheoryData<string> LosslessFiles => Corpus.Files("webp/lossless/");

    internal static void AssertMatchesReference(string file, Image image)
    {
        ReferenceEntry r = Corpus.References[file];
        Assert.Equal(r.Width, image.Width);
        Assert.Equal(r.Height, image.Height);
        Assert.Equal(r.Frames.Count, image.FrameCount);
        for (int i = 0; i < r.Frames.Count; i++)
        {
            string actual = Corpus.Sha256(MemoryMarshal.AsBytes(image.Frames[i].GetPixelSpan()));
            Assert.True(r.Frames[i].Sha256 == actual, $"{file}: frame {i} differs from the {r.Decoder} reference");
        }
    }

    [Theory]
    [MemberData(nameof(LosslessFiles))]
    public void Decode_IsBitExactWithDwebp(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.WebP, image.SourceFormat);
        Assert.Equal(Corpus.References[file].HasAlpha, image.HasAlpha);
        AssertMatchesReference(file, image);
    }

    [Fact]
    public void Decode_ReadsMetadata()
    {
        using Image image = Image.Load(Corpus.PathOf("webp/meta/lossless_exif.webp"));
        Assert.NotNull(image.Metadata.Exif);
        Assert.Equal(ExifOrientation.RightTop, image.Metadata.Orientation);
        AssertMatchesReference("webp/meta/lossless_exif.webp", image);
    }

    [Fact]
    public void Decode_ApplyExifOrientation_RotatesImage()
    {
        using Image image = Image.Load(Corpus.PathOf("webp/meta/lossless_exif.webp"), new DecoderOptions { ApplyExifOrientation = true });
        ReferenceEntry r = Corpus.References["webp/meta/lossless_exif.webp"];
        Assert.Equal((r.Height, r.Width), (image.Width, image.Height));
        Assert.Equal(ExifOrientation.TopLeft, image.Metadata.Orientation);
    }

    [Fact]
    public void Decode_RespectsMaxPixels()
    {
        Assert.Throws<ImageFormatException>(() =>
            Image.Load(Corpus.PathOf("webp/lossless/photo_ll_z6.webp"), new DecoderOptions { MaxPixels = 1000 }));
    }

    public static TheoryData<string, int> Truncations => new()
    {
        { "webp/lossless/photo_ll_z6.webp", 20 },
        { "webp/lossless/photo_ll_z6.webp", 200 },
        { "webp/lossless/graphic_alpha_ll_z9.webp", 100 },
        { "webp/lossless/photo_alpha_ll_exact.webp", 2 },
    };

    [Theory]
    [MemberData(nameof(Truncations))]
    public void Decode_TruncatedOrPatchedFile_ThrowsFormatException(string file, int cut)
    {
        byte[] data = Corpus.Read(file);
        // Truncate, keeping the RIFF size consistent so the bitstream itself runs out.
        byte[] truncated = data[..^cut];
        BitConverter.GetBytes(truncated.Length - 8).CopyTo(truncated, 4);
        BitConverter.GetBytes(truncated.Length - 20).CopyTo(truncated, 16);
        Assert.Throws<ImageFormatException>(() => Image.Load(truncated));
    }

    [Fact]
    public void Decode_RandomCorruption_OnlyThrowsFormatException()
    {
        byte[] original = Corpus.Read("webp/lossless/graphic_ll_z6.webp");
        var rng = new Random(1234);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            byte[] data = (byte[])original.Clone();
            int flips = rng.Next(1, 8);
            for (int i = 0; i < flips; i++)
            {
                int pos = rng.Next(20, data.Length);
                data[pos] ^= (byte)(1 << rng.Next(8));
            }

            try
            {
                using Image img = Image.Load(data);
            }
            catch (ImageFormatException)
            {
            }
        }
    }
}
