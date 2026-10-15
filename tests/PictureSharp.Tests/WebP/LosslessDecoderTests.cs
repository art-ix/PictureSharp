using System.Runtime.InteropServices;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LosslessDecoderTests
{
    public static TestCases<string> LosslessFiles => Corpus.Files("webp/lossless/");

    internal static async Task AssertMatchesReference(string file, Image image)
    {
        ReferenceEntry r = Corpus.References[file];
        await Assert.That(image.Width).IsEqualTo(r.Width);
        await Assert.That(image.Height).IsEqualTo(r.Height);
        await Assert.That(image.FrameCount).IsEqualTo(r.Frames.Count);
        for (int i = 0; i < r.Frames.Count; i++)
        {
            string actual = Corpus.Sha256(MemoryMarshal.AsBytes(image.Frames[i].GetPixelSpan()));
            await Assert.That(r.Frames[i].Sha256 == actual).IsTrue().Because($"{file}: frame {i} differs from the {r.Decoder} reference");
        }
    }

    [Test]
    [MethodDataSource(nameof(LosslessFiles))]
    public async Task Decode_IsBitExactWithDwebp(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file));
        await Assert.That(image.SourceFormat).IsEqualTo(ImageFormat.WebP);
        await Assert.That(image.HasAlpha).IsEqualTo(Corpus.References[file].HasAlpha);
        await AssertMatchesReference(file, image);
    }

    [Test]
    public async Task Decode_ReadsMetadata()
    {
        using Image image = Image.Load(Corpus.PathOf("webp/meta/lossless_exif.webp"));
        Assert.NotNull(image.Metadata.Exif);
        await Assert.That(image.Metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        await AssertMatchesReference("webp/meta/lossless_exif.webp", image);
    }

    [Test]
    public async Task Decode_ApplyExifOrientation_RotatesImage()
    {
        using Image image = Image.Load(Corpus.PathOf("webp/meta/lossless_exif.webp"), new DecoderOptions { ApplyExifOrientation = true });
        ReferenceEntry r = Corpus.References["webp/meta/lossless_exif.webp"];
        await Assert.That((image.Width, image.Height)).IsEqualTo((r.Height, r.Width));
        await Assert.That(image.Metadata.Orientation).IsEqualTo(ExifOrientation.TopLeft);
    }

    public static TestCases<string, int> Truncations => new()
    {
        { "webp/lossless/photo_ll_z6.webp", 20 },
        { "webp/lossless/photo_ll_z6.webp", 200 },
        { "webp/lossless/graphic_alpha_ll_z9.webp", 100 },
        { "webp/lossless/photo_alpha_ll_exact.webp", 2 },
    };

    [Test]
    [MethodDataSource(nameof(Truncations))]
    public void Decode_TruncatedOrPatchedFile_ThrowsFormatException(string file, int cut)
    {
        byte[] data = Corpus.Read(file);
        // Truncate, keeping the RIFF size consistent so the bitstream itself runs out.
        byte[] truncated = data[..^cut];
        BitConverter.GetBytes(truncated.Length - 8).CopyTo(truncated, 4);
        BitConverter.GetBytes(truncated.Length - 20).CopyTo(truncated, 16);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(truncated));
    }
}
