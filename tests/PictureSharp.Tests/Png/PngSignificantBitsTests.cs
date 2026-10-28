using System.Buffers.Binary;
using PictureSharp.Formats.Png;

namespace PictureSharp.Tests.Png;

public class PngSignificantBitsTests
{
    [Test]
    [Arguments(PngColorType.Grayscale, 1)]
    [Arguments(PngColorType.GrayscaleAlpha, 2)]
    [Arguments(PngColorType.Rgb, 3)]
    [Arguments(PngColorType.Rgba, 4)]
    public async Task PrecisionIsMetadata_AtEveryDecodePath(PngColorType color, int channels)
    {
        using var source = new Image(7, 3, PixelType.Rgba64);
        for (int i = 0; i < source.GetPixelSpan<Rgba64>().Length; i++)
            source.GetPixelSpan<Rgba64>()[i] = new Rgba64((ushort)(1001 + i), 32003, 60007, 41009);
        var options = new PngEncoderOptions { ColorType = color, BitDepth = PngBitDepth.Sixteen };
        byte[] plain = source.ToPng(options);
        byte[] values = Enumerable.Repeat((byte)10, channels).ToArray();
        byte[] tagged = Insert(plain, values);
        using Image expected = Image.Load(plain, new() { PreserveBitDepth = true });
        using Image decoded = Image.Load(tagged, new() { PreserveBitDepth = true });
        await Assert.That(decoded.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(decoded.GetPixelSpan<Rgba64>().SequenceEqual(expected.GetPixelSpan<Rgba64>())).IsTrue();
        await Check(decoded.Metadata, values);
        await Check(Image.ReadMetadata(tagged), values);
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(new MemoryStream(tagged), new DecoderOptions { PreserveBitDepth = true });
        await foreach (ImageFrame frame in reader.ReadFramesAsync())
            await Assert.That(frame.GetPixelSpan<Rgba64>().SequenceEqual(expected.GetPixelSpan<Rgba64>())).IsTrue();
        await Check(reader.Metadata, values);
        using Image narrow = Image.Load(tagged);
        await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);
        await Check(narrow.Metadata, values);
        using Image ignored = Image.Load(tagged, new() { IgnoreMetadata = true, PreserveBitDepth = true });
        await Assert.That(ignored.Metadata.GetFormatMetadata<PngSignificantBits>()).IsNull();
        using Image roundtrip = Image.Load(decoded.ToPng(options));
        await Check(roundtrip.Metadata, values);
        using Image reduced = Image.Load(decoded.ToPng(options with { BitDepth = PngBitDepth.UpTo8 }));
        await Assert.That(reduced.Metadata.GetFormatMetadata<PngSignificantBits>()).IsNull();
        using Image changedColor = Image.Load(decoded.ToPng(options with { ColorType = color == PngColorType.Rgba ? PngColorType.Rgb : PngColorType.Rgba }));
        await Assert.That(changedColor.Metadata.GetFormatMetadata<PngSignificantBits>()).IsNull();
        using Image stripped = Image.Load(decoded.ToPng(options with { Metadata = MetadataKinds.None }));
        await Assert.That(stripped.Metadata.GetFormatMetadata<PngSignificantBits>()).IsNull();
        using var streamed = new MemoryStream();
        await using (ImageFrameWriter writer = Image.CreateFrameWriter(streamed, decoded.Size, options,
            new ImageFrameWriterOptions { FrameCount = 1, Metadata = decoded.Metadata }))
        {
            await writer.WriteAsync(decoded.Frames[0]);
            await writer.CompleteAsync();
        }
        await Check(Image.ReadMetadata(streamed.ToArray()), values);
        byte[] edited = Image.EditMetadata(tagged, m => m.ExifProfile.SetValue(ExifTag.Model, "sBIT"));
        await Check(Image.ReadMetadata(edited), values);
        byte[] cleared = Image.EditMetadata(tagged, m => m.RemoveFormatMetadata<PngSignificantBits>());
        await Assert.That(Image.ReadMetadata(cleared).GetFormatMetadata<PngSignificantBits>()).IsNull();
    }

    [Test]
    [Arguments(0)]
    [Arguments(17)]
    [Arguments(256)]
    public async Task InvalidPrecision_IsIgnored(int value)
    {
        using var source = new Image(1, 1, PixelType.Rgba64);
        byte[] data = Insert(source.ToPng(new() { ColorType = PngColorType.Rgba, BitDepth = PngBitDepth.Sixteen }),
            value == 256 ? [8, 8, 8] : [(byte)value, 8, 8, 8]);
        using Image decoded = Image.Load(data, new() { PreserveBitDepth = true });
        await Assert.That(decoded.Metadata.GetFormatMetadata<PngSignificantBits>()).IsNull();
        await Assert.That(Image.ReadMetadata(data).GetFormatMetadata<PngSignificantBits>()).IsNull();
    }

    [Test]
    public async Task ValuesAreSnapshots_AndPalettePrecisionIsEightBit()
    {
        byte[] values = [8, 7, 6];
        var bits = new PngSignificantBits(1, PngColorType.Palette, values);
        values[0] = 1;
        await Assert.That(bits.Values[0]).IsEqualTo((byte)8);
        using var image = new Image(3, 2, new Rgba32(10, 20, 30));
        image.Metadata.SetFormatMetadata(bits);
        using Image decoded = Image.Load(image.ToPng(new() { ColorType = PngColorType.Palette }));
        await Check(decoded.Metadata, [8, 7, 6]);
        Assert.ThrowsExactly<ArgumentException>(() => new PngSignificantBits(16, PngColorType.Palette, [8, 8, 8]));
        Assert.ThrowsExactly<ArgumentException>(() => new PngSignificantBits(16, PngColorType.Auto, [8, 8, 8]));
    }

    private static async Task Check(ImageMetadata metadata, byte[] values)
    {
        PngSignificantBits? bits = metadata.GetFormatMetadata<PngSignificantBits>();
        await Assert.That(bits).IsNotNull();
        await Assert.That(bits!.Values).IsEquivalentTo(values, CollectionOrdering.Matching);
    }

    private static byte[] Insert(byte[] png, byte[] values)
    {
        byte[] chunk = new byte[values.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, values.Length);
        "sBIT"u8.CopyTo(chunk.AsSpan(4));
        values.CopyTo(chunk, 8);
        uint crc = uint.MaxValue;
        foreach (byte b in chunk.AsSpan(4, values.Length + 4))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + values.Length), ~crc);
        return [.. png.AsSpan(0, 33), .. chunk, .. png.AsSpan(33)];
    }
}
