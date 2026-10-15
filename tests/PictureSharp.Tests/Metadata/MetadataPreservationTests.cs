using System.Buffers.Binary;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Metadata;

[NotInParallel("Allocations")]
public class MetadataPreservationTests
{
    [Test]
    [Arguments("jpeg/progressive_420.jpg")]
    [Arguments("png/rgba8.png")]
    [Arguments("webp/anim/alpha_mixed.webp")]
    public async Task UnknownSegmentsAndChunks_ArePreservedVerbatim(string path)
    {
        byte[] source = Corpus.Read(path);
        ImageFormat format = Image.DetectFormat(source)!;
        byte[] opaque;
        byte[] data;
        if (format == ImageFormat.Jpeg)
        {
            opaque = [0xff, 0xef, 0, 8, 1, 2, 3, 4, 5, 6];
            data = [.. source.AsSpan(0, 2), .. opaque, .. source.AsSpan(2)];
        }
        else if (format == ImageFormat.Png)
        {
            opaque = new byte[17];
            BinaryPrimitives.WriteUInt32BigEndian(opaque, 5);
            "vpAg"u8.CopyTo(opaque.AsSpan(4));
            "hello"u8.CopyTo(opaque.AsSpan(8));
            BinaryPrimitives.WriteUInt32BigEndian(opaque.AsSpan(13), Crc32.Compute(opaque.AsSpan(4, 9)));
            data = [.. source.AsSpan(0, 33), .. opaque, .. source.AsSpan(33)];
        }
        else
        {
            opaque = new byte[14];
            "UNKN"u8.CopyTo(opaque);
            BinaryPrimitives.WriteUInt32LittleEndian(opaque.AsSpan(4), 5);
            "hello"u8.CopyTo(opaque.AsSpan(8));
            opaque[13] = 0xa5;
            data = [.. source, .. opaque];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        }
        byte[] result = Image.EditMetadata(data, m => m.ExifProfile.SetValue(ExifTag.Model, "Unknowns"));
        await Assert.That(result.AsSpan().IndexOf(opaque) >= 0).IsTrue();
        await Assert.That(MetadataContainerTests.Payloads(data, format)).IsEquivalentTo(MetadataContainerTests.Payloads(result, format), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Jpeg_MetadataBetweenProgressiveScans_IsReadAndReplaced()
    {
        byte[] source = Corpus.Read("jpeg/progressive_420.jpg");
        int first = source.AsSpan().IndexOf(new byte[] { 0xff, 0xda });
        int second = first + 2 + source.AsSpan(first + 2).IndexOf(new byte[] { 0xff, 0xda });
        await Assert.That(second > first + 2).IsTrue();
        byte[] profile = Corpus.Read("metadata/big.exif");
        byte[] segment = [0xff, 0xe1, 0, 0, .. "Exif\0\0"u8, .. profile];
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(segment.Length - 2));
        byte[] data = [.. source.AsSpan(0, second), .. segment, .. source.AsSpan(second)];
        await Assert.That(Image.ReadMetadata(data).ExifProfile.TryGetValue(ExifTag.Model, out _)).IsTrue();
        byte[] result = Image.EditMetadata(data, m => m.ExifProfile.SetValue(ExifTag.Model, "Between scans"));
        Image.ReadMetadata(result).ExifProfile.TryGetValue(ExifTag.Model, out var model);
        await Assert.That(model).IsEqualTo("Between scans");
        await Assert.That(MetadataContainerTests.Payloads(data, ImageFormat.Jpeg)).IsEquivalentTo(MetadataContainerTests.Payloads(result, ImageFormat.Jpeg), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Jpeg_MpfOffsetsAreRejectedRatherThanCopiedWithIncorrectPositions()
    {
        byte[] source = Corpus.Read("jpeg/q90_420.jpg");
        byte[] data = [.. source.AsSpan(0, 2), 0xff, 0xe2, 0, 6, .. "MPF\0"u8, .. source.AsSpan(2)];
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Task.Run(() => Image.EditMetadata(data, m => m.Clear())));
    }

    [Test]
    public async Task JpegResolution_PreservesEmbeddedJfifThumbnail()
    {
        byte[] source = Corpus.Read("jpeg/q90_420.jpg");
        await Assert.That(source[3]).IsEqualTo((byte)0xe0);
        int originalLength = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(4));
        byte[] body = [.. source.AsSpan(6, originalLength - 2), 17, 23, 41];
        body[12] = 1; body[13] = 1;
        byte[] segment = [0xff, 0xe0, 0, 0, .. body];
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(body.Length + 2));
        byte[] data = [.. source.AsSpan(0, 2), .. segment, .. source.AsSpan(4 + originalLength)];
        byte[] result = Image.EditMetadata(data, m => m.HorizontalResolution = 300);
        await Assert.That(result[18]).IsEqualTo((byte)1);
        await Assert.That(result[19]).IsEqualTo((byte)1);
        await Sequence.AssertEqual(new byte[] { 17, 23, 41 }, result.AsSpan(20, 3).ToArray());
        await Assert.That(MetadataContainerTests.Payloads(data, ImageFormat.Jpeg)).IsEquivalentTo(MetadataContainerTests.Payloads(result, ImageFormat.Jpeg), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CompressedXmpAndMetadataBombs_RespectDecompressedBudget()
    {
        byte[] source = Corpus.Read("png/rgba8.png");
        byte[] text = new byte[100000];
        Array.Fill(text, (byte)'x');
        byte[] compressed = Deflater.CompressZlib(text, 9, 1);
        byte[] body = [.. "XML:com.adobe.xmp\0"u8, 1, 0, 0, 0, .. compressed];
        byte[] chunk = new byte[body.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)body.Length);
        "iTXt"u8.CopyTo(chunk.AsSpan(4));
        body.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), Crc32.Compute(chunk.AsSpan(4, body.Length + 4)));
        byte[] data = [.. source.AsSpan(0, 33), .. chunk, .. source.AsSpan(33)];
        await Sequence.AssertEqual(text, Image.ReadMetadata(data).Xmp);
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.ReadMetadata(data, new DecoderOptions { MaxMetadataBytes = 99999 })));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.EditMetadata(data, m => m.Clear(), new DecoderOptions { MaxMetadataBytes = 99999 })));
        byte[] after = Image.EditMetadata(data, m => m.ExifProfile.SetValue(ExifTag.Model, "XMP preserved"));
        await Sequence.AssertEqual(text, Image.ReadMetadata(after).Xmp);
    }
}
