using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PictureSharp.Domain.Metadata;
using PictureSharp.Infrastructure.Codecs.Jpeg;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;
using PictureSharp.Tests.WebP;

namespace PictureSharp.Tests.Jpeg;

public class JpegDecoderTests
{
    private static readonly Rgba32 Gray = new(128, 128, 128, 255);

    public static TheoryData<string> JpegFiles => Corpus.Files("jpeg/");

    [Theory]
    [MemberData(nameof(JpegFiles))]
    public void Decode_IsBitExactWithPillow(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        using Image image = Image.Load(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Jpeg, image.SourceFormat);
        Assert.False(image.HasAlpha);
        LosslessDecoderTests.AssertMatchesReference(file, image);
        if (r.Dpi is { } dpi)
        {
            Assert.Equal(dpi[0], image.Metadata.HorizontalResolution, 1);
            Assert.Equal(dpi[1], image.Metadata.VerticalResolution, 1);
        }

        Assert.Equal(r.IccSha256, image.Metadata.IccProfile is { } icc ? Corpus.Sha256(icc) : null);
        Assert.Equal(r.XmpSha256, image.Metadata.Xmp is { } xmp ? Corpus.Sha256(xmp) : null);
        Assert.Equal(r.Orientation, (int)image.Metadata.Orientation);
    }

    [Theory]
    [MemberData(nameof(JpegFiles))]
    public void Identify_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        ImageInfo info = Image.Identify(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Jpeg, info.Format);
        Assert.Equal((r.Width, r.Height), (info.Width, info.Height));
        Assert.False(info.HasAlpha);
        Assert.Equal(1, info.FrameCount);
        Assert.Equal(r.Orientation, (int)info.Orientation);
        if (r.Dpi is { } dpi)
        {
            Assert.Equal(dpi[0], info.HorizontalResolution, 1);
        }
    }

    [Fact]
    public void Format_DecodesAndEncodes()
    {
        Assert.Equal(ImageFormat.Jpeg, Image.DetectFormat(Corpus.Read("jpeg/q90_420.jpg")));
        Assert.True(ImageFormat.Jpeg.CanDecode);
        Assert.True(ImageFormat.Jpeg.CanEncode);
    }

    [Fact]
    public void Resolution_InCentimeters_IsConvertedToDpi()
    {
        // Pillow 10.2 ignores JFIF density unit 2, so this is not covered by the reference.
        using Image image = Image.Load(Corpus.PathOf("jpeg/dpcm.jpg"));
        Assert.Equal(28 * 2.54f, image.Metadata.HorizontalResolution, 2);
        Assert.Equal(28 * 2.54f, Image.Identify(Corpus.PathOf("jpeg/dpcm.jpg")).VerticalResolution, 2);
    }

    [Fact]
    public void Resolution_FromExif_KeepsBothAxesAndUnits()
    {
        static byte[] Exif(uint x, uint y, ushort unit)
        {
            var tiff = new byte[8 + 2 + (3 * 12) + 4 + 16];
            "II*\0"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 3);
            (ushort Tag, ushort Type, uint Value)[] entries = [(0x011A, 5, 50), (0x011B, 5, 58), (0x0128, 3, unit)];
            for (int i = 0; i < entries.Length; i++)
            {
                Span<byte> e = tiff.AsSpan(10 + (12 * i), 12);
                BinaryPrimitives.WriteUInt16LittleEndian(e, entries[i].Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(e[2..], entries[i].Type);
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], 1);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], entries[i].Value);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(50), x);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(54), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(58), y);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(62), 2);
            return tiff;
        }

        Assert.Equal((300f, 75f), ExifReader.GetResolution(Exif(300, 150, 2)));
        Assert.Equal((254f, 127f), ExifReader.GetResolution(Exif(100, 100, 3)));
        Assert.Null(ExifReader.GetResolution(Exif(300, 150, 1)));
        Assert.Null(ExifReader.GetResolution(Exif(0, 150, 2)));
    }

    [Fact]
    public void Metadata_CanBeIgnored_AndOrientationApplied()
    {
        using Image plain = Image.Load(Corpus.PathOf("jpeg/icc_multi.jpg"), new DecoderOptions { IgnoreMetadata = true });
        Assert.True(plain.Metadata.IsEmpty);

        using Image rotated = Image.Load(Corpus.PathOf("jpeg/orientation_6.jpg"), new DecoderOptions { ApplyExifOrientation = true });
        Assert.Equal((192, 256), (rotated.Width, rotated.Height));
        Assert.Equal(ExifOrientation.TopLeft, rotated.Metadata.Orientation);
    }

    // ------------------------------------------------------------------ damaged and unusual streams

    /// <summary>Marker segments before the first scan: (marker, offset of 0xFF, total length).</summary>
    private static List<(int Marker, int Offset, int Length)> Segments(byte[] jpeg)
    {
        var list = new List<(int, int, int)>();
        int pos = 2;
        while (true)
        {
            int marker = jpeg[pos + 1];
            int length = 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            list.Add((marker, pos, length));
            if (marker == 0xDA)
            {
                return list;
            }

            pos += length;
        }
    }

    private static (int Offset, int Length) Find(byte[] jpeg, int marker)
    {
        (_, int offset, int length) = Segments(jpeg).First(s => s.Marker == marker);
        return (offset, length);
    }

    private static byte[] Segment(int marker, params byte[] payload) =>
        [0xFF, (byte)marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    /// <summary>SOI, the given segments and EOI.</summary>
    private static byte[] Build(params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(s => s), 0xFF, 0xD9];

    private static byte[] Sof(int marker, int precision, int height, int width, params (int Id, int H, int V)[] comps) =>
        Segment(marker, [(byte)precision, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)comps.Length,
            .. comps.SelectMany(c => new[] { (byte)c.Id, (byte)((c.H << 4) | c.V), (byte)0 })]);

    private static Rgba32[] Pixels(byte[] jpeg)
    {
        using Image image = Image.Load(jpeg);
        return image.GetPixelSpan().ToArray();
    }

    [Fact]
    public void Truncated_Baseline_FillsTheRestWithGray()
    {
        byte[] full = Corpus.Read("jpeg/q90_420.jpg");
        Rgba32[] expected = Pixels(full);
        Rgba32[] actual = Pixels(full[..(full.Length / 2)]);
        Assert.Equal(expected[0], actual[0]);
        Assert.Equal(Gray, actual[^1]);
    }

    [Fact]
    public void Truncated_Progressive_StillDecodes()
    {
        byte[] full = Corpus.Read("jpeg/progressive_420.jpg");
        using Image image = Image.Load(full[..(full.Length * 2 / 3)]);
        Assert.Equal((256, 192), (image.Width, image.Height));
    }

    [Fact]
    public void Truncated_BeforeTheFirstScan_Throws()
    {
        byte[] full = Corpus.Read("jpeg/q90_420.jpg");
        (int sos, _) = Find(full, 0xDA);
        Assert.Throws<ImageFormatException>(() => Image.Load(full[..sos]));
        Assert.Throws<ImageFormatException>(() => Image.Load(full[..(sos - 3)]));
        Assert.Equal(256, Image.Identify(full[..(sos + 2)]).Width);
    }

    [Fact]
    public void GarbageBetweenSegments_IsSkipped()
    {
        byte[] full = Corpus.Read("jpeg/q90_444.jpg");
        (int sof, _) = Find(full, 0xC0);
        byte[] damaged = [.. full[..sof], 0x12, 0x34, 0xFF, 0x00, 0xFF, .. full[sof..]];
        Assert.Equal(Pixels(full), Pixels(damaged));
    }

    public static TheoryData<string, byte[]> UnsupportedFiles() => new()
    {
        { "lossless", Build(Sof(0xC3, 8, 8, 8, (1, 1, 1))) },
        { "arithmetic", Build(Sof(0xC9, 8, 8, 8, (1, 1, 1))) },
        { "progressive arithmetic", Build(Sof(0xCA, 8, 8, 8, (1, 1, 1))) },
        { "hierarchical", Build(Sof(0xC5, 8, 8, 8, (1, 1, 1))) },
        { "12-bit", Build(Sof(0xC1, 12, 8, 8, (1, 1, 1))) },
        { "DNL height", Build(Sof(0xC0, 8, 0, 8, (1, 1, 1))) },
        { "two components", Build(Sof(0xC0, 8, 8, 8, (1, 1, 1), (2, 1, 1))) },
        { "fractional sampling", Build(Sof(0xC0, 8, 8, 8, (1, 3, 1), (2, 2, 1), (3, 1, 1))) },
    };

    [Theory]
    [MemberData(nameof(UnsupportedFiles))]
    public void Decode_UnsupportedVariant_ThrowsNotSupported(string name, byte[] jpeg)
    {
        Assert.Throws<NotSupportedException>(() => Image.Load(jpeg));
        Assert.NotEmpty(name);
    }

    public static TheoryData<string, byte[]> InvalidFiles()
    {
        byte[] good = Corpus.Read("jpeg/q90_420.jpg");
        (int sos, int sosLength) = Find(good, 0xDA);
        (int dht, _) = Find(good, 0xC4);
        (int dqt, _) = Find(good, 0xDB);

        byte[] Patch(int offset, params byte[] bytes)
        {
            byte[] copy = (byte[])good.Clone();
            bytes.CopyTo(copy, offset);
            return copy;
        }

        return new TheoryData<string, byte[]>
        {
            { "no frame header", Build(Segment(0xDA, 1, 1, 0, 0, 63, 0)) },
            { "empty", Build() },
            { "two SOI", Build([0xFF, 0xD8]) },
            { "zero width", Build(Sof(0xC0, 8, 8, 0, (1, 1, 1))) },
            { "bad precision", Build(Sof(0xC0, 7, 8, 8, (1, 1, 1))) },
            { "bad sampling", Build(Sof(0xC0, 8, 8, 8, (1, 5, 1))) },
            { "duplicate component", Build(Sof(0xC0, 8, 8, 8, (1, 1, 1), (1, 1, 1), (2, 1, 1))) },
            { "two frames", Build(Sof(0xC0, 8, 8, 8, (1, 1, 1)), Sof(0xC0, 8, 8, 8, (1, 1, 1))) },
            { "bad segment length", Build([0xFF, 0xFE, 0x00, 0x01]) },
            { "unknown scan component", Patch(sos + 5, 9) },
            { "too many blocks in MCU", Build(Sof(0xC0, 8, 8, 8, (1, 4, 4), (2, 1, 1), (3, 1, 1)), Segment(0xDA, 2, 1, 0, 2, 0, 0, 63, 0)) },
            { "undefined quantization table", Patch(dqt + 4, 0x03) },
            { "huffman table overfull", Patch(dht + 5, 0xFF) },
            { "huffman all-ones code", Build(Segment(0xC4, [0x00, 2, .. new byte[15], 0, 1])) },
            { "bad progression", Build(Sof(0xC2, 8, 8, 8, (1, 1, 1)), Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]),
                Segment(0xDA, 1, 1, 0, 0, 5, 0)) },
            { "scan length", good[..sos].Concat(Segment(0xDA, good.AsSpan(sos + 4, sosLength - 5).ToArray())).Concat(good[(sos + sosLength)..]).ToArray() },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Decode_InvalidFile_ThrowsFormatException(string name, byte[] jpeg)
    {
        var ex = Assert.ThrowsAny<Exception>(() => Image.Load(jpeg));
        Assert.True(ex is ImageFormatException, $"{name}: {ex}");
    }

    [Fact]
    public void Decode_RespectsMaxPixels()
    {
        byte[] jpeg = Build(Sof(0xC0, 8, 60000, 60000, (1, 1, 1)));
        Assert.Throws<ImageFormatException>(() => Image.Load(jpeg, new DecoderOptions { MaxPixels = 1 << 20 }));
        Assert.Equal(60000, Image.Identify(jpeg).Width);
    }

    [Fact]
    public void Decode_RandomCorruption_OnlyThrowsFormatException()
    {
        foreach (string file in new[] { "jpeg/q90_420.jpg", "jpeg/progressive_420.jpg", "jpeg/restart_rows_progressive.jpg",
                                        "jpeg/crafted_ycck.jpg", "jpeg/crafted_noninterleaved.jpg", "jpeg/tiny_5x2_420.jpg" })
        {
            byte[] original = Corpus.Read(file);
            (int sos, _) = Find(original, 0xDA);
            var rng = new Random(file.Length);
            for (int iteration = 0; iteration < 200; iteration++)
            {
                byte[] data = (byte[])original.Clone();
                int flips = rng.Next(1, 4);
                for (int i = 0; i < flips; i++)
                {
                    // Bias towards the marker segments, where the interesting fields are.
                    int pos = rng.Next(2) == 0 ? rng.Next(3, data.Length) : rng.Next(3, Math.Min(data.Length, sos + 20));
                    data[pos] ^= (byte)(1 << rng.Next(8));
                }

                if (rng.Next(4) == 0)
                {
                    data = data[..rng.Next(3, data.Length)];
                }

                try
                {
                    using Image img = Image.Load(data);
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

    // ------------------------------------------------------------------ damaged streams against libjpeg

    /// <summary>
    /// Damaged files are decoded like libjpeg-turbo does: a truncated baseline scan ends in gray, and restart
    /// markers that are missing or out of order are resynchronized with jpeg_resync_to_restart.
    /// </summary>
    [Trait("Category", "Oracle")]
    [Fact]
    public void DamagedFiles_MatchPillow()
    {
        byte[] restart = Corpus.Read("jpeg/restart_blocks.jpg");
        (int sos, int sosLength) = Find(restart, 0xDA);
        List<int> rst = [];
        for (int i = sos + sosLength; i < restart.Length - 1; i++)
        {
            if (restart[i] == 0xFF && restart[i + 1] is >= 0xD0 and <= 0xD7)
            {
                rst.Add(i);
            }
        }

        byte[] With(byte[] data, Action<byte[]> change)
        {
            byte[] copy = (byte[])data.Clone();
            change(copy);
            return copy;
        }

        byte[] baseline = Corpus.Read("jpeg/q90_420.jpg");
        var files = new Dictionary<string, byte[]>
        {
            ["truncated_baseline.jpg"] = baseline[..(baseline.Length * 3 / 5)],
            ["truncated_noninterleaved.jpg"] = Corpus.Read("jpeg/crafted_noninterleaved.jpg")[..900],
            ["rst_skipped_ahead.jpg"] = With(restart, d => d[rst[3] + 1] = 0xD5),
            ["rst_previous.jpg"] = With(restart, d => d[rst[6] + 1] = (byte)(0xD0 + ((6 - 1) & 7))),
            ["rst_far_away.jpg"] = With(restart, d => d[rst[2] + 1] = (byte)(0xD0 + ((2 + 4) & 7))),
            ["rst_missing.jpg"] = restart.Where((_, i) => i != rst[4] && i != rst[4] + 1).ToArray(),
            ["rst_garbage.jpg"] = [.. restart[..rst[5]], 0x11, 0x22, .. restart[rst[5]..]],
            ["scan_marker_inside.jpg"] = With(baseline, d => { d[sos + sosLength + 2000] = 0xFF; d[sos + sosLength + 2001] = 0xFE; }),
        };

        string dir = Path.Combine(Path.GetTempPath(), $"picturesharp-jpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach ((string name, byte[] data) in files)
            {
                File.WriteAllBytes(Path.Combine(dir, name), data);
            }

            foreach (int scale in (int[])[1, 2, 4, 8])
            {
                if (JpegSamplesTests.PillowReference(dir, null, scale) is not { } reference)
                {
                    return;   // Pillow not available
                }

                foreach ((string name, byte[] data) in files)
                {
                    using Image image = PictureSharp.Infrastructure.Codecs.Jpeg.JpegDecoder.DecodeScaled(data, DecoderOptions.Default, scale);
                    Assert.True(reference[name] == Corpus.Sha256(MemoryMarshal.AsBytes(image.GetPixelSpan())), $"{name} at 1/{scale} differs from Pillow");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
