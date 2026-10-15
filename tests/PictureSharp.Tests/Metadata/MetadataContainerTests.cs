using System.Buffers.Binary;
using System.Text;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Metadata;

[NotInParallel("Allocations")]
public class MetadataContainerTests
{
    public static TestCases<string> Files =>
    [
        "jpeg/progressive_420.jpg", "jpeg/q90_420.jpg", "jpeg/exif_resolution.jpg",
        "png/apng_alpha.png", "png/rgba8.png", "png/rgb8_interlaced.png",
        "webp/anim/alpha_mixed.webp", "webp/lossless/graphic_ll_z6.webp", "webp/lossy/photo_q75.webp",
        "webp/meta/lossless_exif.webp", "webp/alpha/photo_alpha_a_default.webp",
    ];

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task EditingMetadata_PreservesEveryCompressedPayloadAndFrame(string path)
    {
        byte[] original = Corpus.Read(path);
        await Sequence.AssertEqual(original, Image.EditMetadata(original, _ => { }));
        byte[] edited = Image.EditMetadata(original, m =>
        {
            m.Exif = Corpus.Read("metadata/big.exif");
            m.ExifProfile.SetValue(ExifTag.Model, "Edited Camera");
            m.ExifProfile.RemoveGps();
            m.Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
            m.IccProfile = Enumerable.Range(0, 80000).Select(i => (byte)i).ToArray();
        });
        ImageFormat format = Image.DetectFormat(original)!;
        await Assert.That(Payloads(original, format)).IsEquivalentTo(Payloads(edited, format), CollectionOrdering.Matching);
        var read = Image.ReadMetadata(edited);
        await Assert.That(read.ExifProfile.TryGetValue(ExifTag.Model, out var model)).IsTrue();
        await Assert.That(model).IsEqualTo("Edited Camera");
        await Assert.That(read.ExifProfile.TryGetValue(ExifTag.GpsLatitude, out _)).IsFalse();
        await Assert.That(read.GetBlockLength(MetadataKinds.Icc)).IsEqualTo(80000);
        using Image source = Image.Load(original);
        using Image result = Image.Load(edited, new DecoderOptions { Png = new() { StrictChecksums = true } });
        await Assert.That(result.FrameCount).IsEqualTo(source.FrameCount);
        await Assert.That(result.LoopCount).IsEqualTo(source.LoopCount);
        await Assert.That(result.AnimationBackgroundColor).IsEqualTo(source.AnimationBackgroundColor);
        for (int i = 0; i < result.FrameCount; i++)
        {
            await Assert.That(result.Frames[i].Duration).IsEqualTo(source.Frames[i].Duration);
            await Sequence.AssertEqual(source.Frames[i].GetPixelSpan().ToArray(), result.Frames[i].GetPixelSpan().ToArray());
        }
        byte[] cleared = Image.EditMetadata(edited, m => m.Clear());
        await Assert.That(Image.ReadMetadata(cleared).IsEmpty).IsTrue();
        await Assert.That(Payloads(original, format)).IsEquivalentTo(Payloads(cleared, format), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("jpeg/q90_420.jpg")]
    [Arguments("png/rgba8.png")]
    public async Task ContainerResolution_CanBeEditedWithoutChangingPayloads(string path)
    {
        byte[] before = Corpus.Read(path);
        byte[] after = Image.EditMetadata(before, m => { m.HorizontalResolution = 300; m.VerticalResolution = 240; });
        ImageMetadata metadata = Image.ReadMetadata(after);
        await Assert.That(metadata.HorizontalResolution).IsEqualTo(300f).Within(0.02f);
        await Assert.That(metadata.VerticalResolution).IsEqualTo(240f).Within(0.02f);
        await Assert.That(Payloads(before, Image.DetectFormat(before)!)).IsEquivalentTo(Payloads(after, Image.DetectFormat(after)!), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Limits_ConfigurationAndOptions_AreEnforcedBeforeCallback()
    {
        byte[] data = Image.EditMetadata(Corpus.Read("png/rgba8.png"), m => m.Exif = Corpus.Read("metadata/little.exif"));
        bool called = false;
        foreach (DecoderOptions options in new[]
        {
            new DecoderOptions { MaxInputBytes = data.Length - 1 }, new DecoderOptions { MaxMetadataBytes = 1 }, new DecoderOptions { MaxPixels = 1 },
        })
        {
            await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.EditMetadata(data, _ => called = true, options)));
        }
        await Assert.That(called).IsFalse();
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.ReadMetadata(Corpus.Read("png/apng_alpha.png"), new DecoderOptions { MaxFrames = 1 })));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.ReadMetadata(data, new DecoderOptions
        {
            Configuration = ImageConfiguration.Default.ToBuilder().RemoveFormat(ImageFormat.Png).Build(),
        })));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Task.Run(() => Image.ReadMetadata(data, new DecoderOptions { IgnoreMetadata = true })));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Task.Run(() => Image.ReadMetadata(Corpus.Read("gif/im_interlace.gif"))));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.EditMetadata(data, m => m.Xmp = new byte[20], new DecoderOptions { MaxMetadataBytes = Image.ReadMetadata(data).GetBlockLength(MetadataKinds.Exif) })));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() => Image.ReadMetadata(data, null, cts.Token)));
    }

    [Test]
    [Arguments("jpeg/progressive_420.jpg")]
    [Arguments("png/apng_alpha.png")]
    [Arguments("webp/anim/alpha_mixed.webp")]
    public async Task TruncationsAndHostileLengths_ThrowFormatExceptions(string path)
    {
        byte[] data = Corpus.Read(path);
        foreach (int length in new[] { 0, 1, 4, 12, data.Length / 2, data.Length - 1 })
            await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.ReadMetadata(data.AsMemory(0, length))));
        if (path.StartsWith("png", StringComparison.Ordinal))
        {
            byte[] damaged = (byte[])data.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(damaged.AsSpan(8), uint.MaxValue);
            await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.ReadMetadata(damaged)));
        }
        if (path.StartsWith("webp", StringComparison.Ordinal))
        {
            byte[] damaged = (byte[])data.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(16), uint.MaxValue);
            await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.ReadMetadata(damaged)));
        }
    }

    [Test]
    [Arguments("jpeg/progressive_420.jpg")]
    [Arguments("png/apng_alpha.png")]
    [Arguments("webp/anim/alpha_mixed.webp")]
    public async Task Mutations_NeverExposeUncheckedExceptions(string path)
    {
        byte[] valid = Corpus.Read(path);
        var random = new Random(6102);
        for (int i = 0; i < 64; i++)
        {
            byte[] bytes = (byte[])valid.Clone();
            bytes[random.Next(bytes.Length)] ^= (byte)random.Next(1, 256);
            try { _ = Image.EditMetadata(bytes, m => m.ExifProfile.SetValue(ExifTag.Model, "Fuzz")); }
            catch (Exception ex)
            {
                await Assert.That(ex is ImageFormatException or NotSupportedException).IsTrue().Because(ex.ToString());
            }
        }
    }

    [Test]
    public async Task Jpeg_RejectsOversizedReplacements()
    {
        byte[] data = Corpus.Read("jpeg/q90_420.jpg");
        // Same limits and exception type as Save.
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Task.Run(() => Image.EditMetadata(data, m => m.Xmp = new byte[65534])));
        byte[] exif = [.. Corpus.Read("metadata/little.exif"), .. new byte[65534]];
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Task.Run(() => Image.EditMetadata(data, m => m.Exif = exif)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Task.Run(() => Image.EditMetadata(data, m => m.IccProfile = new byte[(65519 * 255) + 1])));
        using Image image = Image.Load(data);
        image.Metadata.Xmp = new byte[65534];
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Task.Run(() => image.Encode(ImageFormat.Jpeg)));
    }

    // Compare entire coding/control chunks, not just decoded pixels or a re-encoding's hash.
    internal static string[] Payloads(byte[] data, ImageFormat format)
    {
        var parts = new List<string>();
        if (format == ImageFormat.Png)
        {
            for (int p = 8; p + 12 <= data.Length;)
            {
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
                string type = Encoding.ASCII.GetString(data, p + 4, 4);
                if (type is not ("eXIf" or "iCCP" or "iTXt" or "pHYs" or "sRGB")) parts.Add(type + ":" + Corpus.Sha256(data.AsSpan(p, size + 12)));
                p += size + 12;
            }
        }
        else if (format == ImageFormat.WebP)
        {
            int end = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) + 8;
            for (int p = 12; p + 8 <= end;)
            {
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4));
                string type = Encoding.ASCII.GetString(data, p, 4);
                int length = size + (size & 1) + 8;
                if (type is not ("VP8X" or "ICCP" or "EXIF" or "XMP ")) parts.Add(type + ":" + Corpus.Sha256(data.AsSpan(p, length)));
                p += length;
            }
        }
        else
        {
            int p = 2;
            while (p < data.Length)
            {
                int start = p++;
                while (data[p] == 0xff) p++;
                byte marker = data[p++];
                if (marker == 0xd9) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
                p += length;
                if (marker < 0xe0 || marker > 0xef) parts.Add(marker + ":" + Corpus.Sha256(data.AsSpan(start, p - start)));
                if (marker != 0xda) continue;
                start = p;
                while (p < data.Length - 1)
                {
                    if (data[p] == 0xff && data[p + 1] is not (0 or >= 0xd0 and <= 0xd7)) break;
                    p++;
                }
                parts.Add("scan:" + Corpus.Sha256(data.AsSpan(start, p - start)));
            }
        }
        return parts.ToArray();
    }
}
