
namespace PictureSharp.Tests.Pcx;

public class PcxDecoderTests
{
    private static readonly Rgba32 Red = new(255, 0, 0);
    private static readonly Rgba32 Green = new(0, 255, 0);
    private static readonly Rgba32 Blue = new(0, 0, 255);
    private static readonly Rgba32 White = new(255, 255, 255);
    private static readonly Rgba32[] Pal4 = [Red, Green, Blue, White];

    private static Rgba32[] Pixels(byte[] pcx, DecoderOptions? options = null)
    {
        using Image image = Image.Load(pcx, options);
        return image.GetPixelSpan().ToArray();
    }

    [Test]
    public async Task Detect_RecognizesPcx()
    {
        byte[] pcx = new PcxBuilder { Rows = [[1]], BytesPerLine = 1, VgaPalette = PcxBuilder.Vga(Red) }.Build();
        await Assert.That(Image.DetectFormat(pcx)).IsEqualTo(ImageFormat.Pcx);
        await Assert.That(ImageFormat.FromExtension("scan.PCX")).IsEqualTo(ImageFormat.Pcx);
        await Assert.That(ImageFormat.FromMimeType("image/x-pcx")).IsEqualTo(ImageFormat.Pcx);
        await Assert.That(ImageFormat.Pcx.CanDecode).IsTrue();
        await Assert.That(ImageFormat.Pcx.CanEncode).IsTrue();
        Assert.Null(Image.DetectFormat([0x0A, 6, 1, 8]));
        Assert.Null(Image.DetectFormat([0x0A, 5, 2, 8]));
        Assert.Null(Image.DetectFormat([0x0A, 5, 1, 3]));
    }

    [Test]
    public async Task Mono_EmptyPalette_IsBlackAndWhite()
    {
        // Width 9 so the second byte holds the last pixel. Bits: 1010 0001 1000 0000.
        byte[] pcx = new PcxBuilder
        {
            Width = 9,
            Bits = 1,
            Planes = 1,
            Version = 2,
            Rows = [[0xA1, 0x80]],
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([White, Rgba32.Black, White, Rgba32.Black, Rgba32.Black, Rgba32.Black, Rgba32.Black, White, White], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Mono_HeaderPalette_KeepsColors()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Bits = 1,
            Planes = 1,
            BytesPerLine = 1,
            EgaPalette = PcxBuilder.Rgb(Red, Blue),
            Rows = [[0x80]],
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([Blue, Red], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task Planar_UnpacksLeastSignificantPlaneFirst(int planes)
    {
        var palette = new Rgba32[16];
        for (int i = 0; i < 16; i++)
        {
            palette[i] = new Rgba32((byte)(i * 15), (byte)(255 - (i * 10)), (byte)(i * 3));
        }

        // Pixel 0 sets every plane (index = 2^planes - 1). Pixel 1 sets plane 0 only (index 1).
        byte[][] row = new byte[planes][];
        for (int plane = 0; plane < planes; plane++)
        {
            row[plane] = [(byte)(plane == 0 ? 0xC0 : 0x80)];
        }

        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Bits = 1,
            Planes = planes,
            BytesPerLine = 1,
            EgaPalette = PcxBuilder.Rgb(palette),
            Rows = [[.. row.SelectMany(plane => plane)]],
        }.Build();
        int all = (1 << planes) - 1;
        await Assert.That(Pixels(pcx)).IsEquivalentTo([palette[all], palette[1]], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Packed2_ReadsMostSignificantBitsFirst()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 5,
            Bits = 2,
            Planes = 1,
            EgaPalette = PcxBuilder.Rgb(Pal4),
            Rows = [[0x1B, 0x40]],
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([Pal4[0], Pal4[1], Pal4[2], Pal4[3], Pal4[1]], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Packed4_ReadsNibbles()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 3,
            Bits = 4,
            Planes = 1,
            EgaPalette = PcxBuilder.Rgb(Pal4),
            Rows = [[0x12, 0x30]],
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([Pal4[1], Pal4[2], Pal4[3]], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Indexed_UsesTrailingVgaPalette()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 3,
            Height = 1,
            BytesPerLine = 3,
            Rows = [[2, 0, 1]],
            VgaPalette = PcxBuilder.Vga(Red, Green, Blue),
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([Blue, Red, Green], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Indexed_PaletteMarkerInsideRaster_IsNotATrailer(int encoding)
    {
        var row = new byte[770];
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = (byte)((x & 1) == 0 ? 10 : 12);
        }

        byte[] pcx = new PcxBuilder { Width = row.Length, Encoding = encoding, Rows = [row] }.Build();
        await Assert.That((int)(pcx[^769])).IsEqualTo(12);
        Rgba32[] expected = row.Select(value => new Rgba32(value, value, value)).ToArray();
        await Assert.That(Pixels(pcx)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Indexed_RasterMarkerDoesNotOverrideActualPalette(int encoding)
    {
        var row = new byte[770];
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = (byte)((x & 1) == 0 ? 10 : 12);
        }

        Rgba32[] colors = Enumerable.Range(0, 256).Select(i => new Rgba32((byte)i, (byte)(255 - i), 42)).ToArray();
        byte[] pcx = new PcxBuilder { Width = row.Length, Encoding = encoding, Rows = [row], VgaPalette = PcxBuilder.Vga(colors) }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo(row.Select(index => colors[index]).ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Indexed_MissingPalette_IsGrayscale()
    {
        byte[] pcx = new PcxBuilder { Width = 3, BytesPerLine = 3, Rows = [[0, 128, 255]] }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([new Rgba32(0, 0, 0), new Rgba32(128, 128, 128), new Rgba32(255, 255, 255)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Rgb_ReadsPlanes()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Bits = 8,
            Planes = 3,
            Rows = [[10, 11, 20, 21, 30, 31]],
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([new Rgba32(10, 20, 30), new Rgba32(11, 21, 31)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Rgba_ReadsStraightAlpha()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Bits = 8,
            Planes = 4,
            Rows = [[1, 2, 3, 4, 5, 6, 7, 8]],
        }.Build();
        using Image image = Image.Load(pcx);
        await Assert.That(image.HasAlpha).IsTrue();
        await Assert.That(image.GetPixelSpan().ToArray()).IsEquivalentTo([new Rgba32(1, 3, 5, 7), new Rgba32(2, 4, 6, 8)], CollectionOrdering.Matching);
        ImageInfo info = Image.Identify(pcx);
        await Assert.That(info.HasAlpha).IsTrue();
        await Assert.That(info.Format).IsEqualTo(ImageFormat.Pcx);
    }

    [Test]
    public async Task Origin_DoesNotChangeTheSize()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Height = 2,
            XMin = 4,
            YMin = 7,
            Rows = [[1, 2], [3, 0]],
            VgaPalette = PcxBuilder.Vga(Red, Green, Blue, White),
        }.Build();
        using Image image = Image.Load(pcx);
        await Assert.That((image.Width, image.Height)).IsEqualTo((2, 2));
        await Assert.That(image.GetPixelSpan().ToArray()).IsEquivalentTo([Green, Blue, White, Red], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Uncompressed_ReadsRawScanlines()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Encoding = 0,
            Rows = [[9, 8]],
            VgaPalette = PcxBuilder.Vga(Red, Green, Blue, White, new(1, 1, 1), new(2, 2, 2), new(3, 3, 3), new(4, 4, 4), new(5, 5, 5), new(9, 9, 9)),
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([new Rgba32(9, 9, 9), new Rgba32(5, 5, 5)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Run_MayCrossPlanes()
    {
        // One run of two 0x80 bytes covers both 1-byte planes. Both pixels' high bits are set.
        byte[] pcx = new PcxBuilder
        {
            Width = 1,
            Height = 1,
            Bits = 1,
            Planes = 2,
            BytesPerLine = 1,
            EgaPalette = PcxBuilder.Rgb(Red, Green, Blue, White),
            Data = [0xC2, 0x80],
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([White], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Run_OfZero_WritesNothing()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 1,
            BytesPerLine = 1,
            Rows = null,
            Data = [0xC0, 0x11, 4],
            VgaPalette = PcxBuilder.Vga(Red, Green, Blue, White, new(4, 4, 4)),
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([new Rgba32(4, 4, 4)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task LiteralAboveMarker_IsARunOfOne()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 1,
            BytesPerLine = 1,
            Data = [0xC1, 0xC5],
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([new Rgba32(0xC5, 0xC5, 0xC5)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Dpi_RequiresBothAxes()
    {
        byte[] both = new PcxBuilder { Width = 1, BytesPerLine = 1, Rows = [[0]], HorizontalDpi = 72, VerticalDpi = 144 }.Build();
        byte[] one = new PcxBuilder { Width = 1, BytesPerLine = 1, Rows = [[0]], HorizontalDpi = 72 }.Build();
        using Image image = Image.Load(both);
        await Assert.That((image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution)).IsEqualTo((72f, 144f));
        using Image missing = Image.Load(one);
        await Assert.That((missing.Metadata.HorizontalResolution, missing.Metadata.VerticalResolution)).IsEqualTo((96f, 96f));
    }

    [Test]
    public async Task Identify_DoesNotNeedPixelData()
    {
        byte[] pcx = new PcxBuilder { Width = 6, Height = 4, Bits = 8, Planes = 3, HorizontalDpi = 300, VerticalDpi = 300 }.Build();
        ImageInfo info = Image.Identify(pcx);
        await Assert.That((info.Width, info.Height, info.HasAlpha)).IsEqualTo((6, 4, false));
        await Assert.That((info.HorizontalResolution, info.VerticalResolution)).IsEqualTo((300f, 300f));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(pcx));
    }

    [Test]
    public async Task Stride_RejectsValuesBelowTheMinimumAndHonorsPadding()
    {
        byte[] tooSmall = new PcxBuilder { Width = 3, Bits = 8, Planes = 1, BytesPerLine = 2, Rows = [[1, 2]] }.Build();
        byte[] paddedRgb = new PcxBuilder { Width = 1, Bits = 8, Planes = 3, BytesPerLine = 8, Rows = [[7, 0, 0, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 9, 0, 0, 0, 0, 0, 0, 0]] }.Build();
        byte[] padded = new PcxBuilder { Width = 1, Bits = 8, Planes = 1, BytesPerLine = 4, Rows = [[7, 0, 0, 0]] }.Build();
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(tooSmall));
        await Assert.That(Pixels(paddedRgb)).IsEquivalentTo([new Rgba32(7, 8, 9)], CollectionOrdering.Matching);
        await Assert.That(Pixels(padded)).IsEquivalentTo([new Rgba32(7, 7, 7)], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(1)]
    [Arguments(62)]
    [Arguments(63)]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(126)]
    [Arguments(127)]
    public async Task MaximallyCompressedRows_AreNotRejectedAsTruncated(int width)
    {
        var row = new byte[width];
        row.AsSpan().Fill(42);
        byte[] pcx = new PcxBuilder { Width = width, Height = 3, BytesPerLine = width, Rows = [row, row, row] }.Build();
        foreach (var pixel in Pixels(pcx))
        {
            await Assert.That(pixel).IsEqualTo(new Rgba32(42, 42, 42));
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task TruncatedLargeImage_IsRejectedBeforePixelAllocation(int encoding)
    {
        byte[] pcx = new PcxBuilder { Width = 4096, Height = 4096, Encoding = encoding }.Build();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(pcx));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(error.Message).Contains("truncated");
        await Assert.That(allocated < 65536).IsTrue().Because($"Allocated {allocated} bytes for a header without image data.");
    }

    [Test]
    public async Task Indexed_PaletteMarkerInsideHeader_IsNotATrailer()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 658,
            Encoding = 0,
            EgaPalette = PcxBuilder.Rgb(new Rgba32(0, 12, 0)),
            Rows = [new byte[658]],
        }.Build();
        await Assert.That((int)(pcx[^769])).IsEqualTo(12);
        foreach (var pixel in Pixels(pcx))
        {
            await Assert.That(pixel).IsEqualTo(Rgba32.Black);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    public async Task Mono_VersionWithoutPalette_IgnoresHeaderColors(int version)
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Bits = 1,
            Version = version,
            EgaPalette = PcxBuilder.Rgb(Red, Blue),
            Rows = [[0x80, 0]],
        }.Build();
        await Assert.That(Pixels(pcx)).IsEquivalentTo([White, Rgba32.Black], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    public void Color_VersionWithoutPalette_IsNotSupported(int version)
    {
        byte[] pcx = new PcxBuilder { Width = 2, Bits = 1, Planes = 4, Version = version, Rows = [new byte[8]] }.Build();
        Assert.ThrowsExactly<NotSupportedException>(() => Image.Load(pcx));
        Assert.ThrowsExactly<NotSupportedException>(() => Image.Identify(pcx));
    }

    [Test]
    public void VersionOne_IsInvalid()
    {
        byte[] pcx = new PcxBuilder { Version = 1, Rows = [[0, 0]] }.Build();
        Assert.Null(Image.DetectFormat(pcx));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(pcx));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(pcx));
    }

    [Test]
    public void RunPastScanline_AndTruncation_AreCorrupt()
    {
        byte[] overrun = new PcxBuilder { Width = 1, Bits = 8, Planes = 1, BytesPerLine = 1, Data = [0xC2, 1] }.Build();
        byte[] truncated = new PcxBuilder { Width = 2, Bits = 8, Planes = 1, Data = [1] }.Build();
        byte[] shortPalette = new PcxBuilder { Width = 1, BytesPerLine = 1, Rows = [[0]], Trailing = [0x0C, 1, 2, 3] }.Build();
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(overrun));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(truncated));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(shortPalette));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(overrun[..10]));
    }

    [Test]
    public async Task MaxPixels_IsCheckedBeforeDecoding()
    {
        byte[] pcx = new PcxBuilder { Width = 100, Height = 100, Bits = 8, Planes = 3 }.Build();
        ImageFormatException ex = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(pcx, new DecoderOptions { MaxPixels = 10 }));
        await Assert.That(ex.Message).Contains("MaxPixels");
    }

    [Test]
    public void HugeImage_DoesNotOverflowThePixelBuffer()
    {
        var header = new byte[128];
        header[0] = 0x0A;
        header[1] = 5;
        header[3] = 1;
        header[2] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 65535);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 65535);
        header[65] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(66), 8192);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(header, new DecoderOptions { MaxPixels = long.MaxValue }));
    }

    [Test]
    public void UnsupportedPlaneCount_IsNotSupported()
    {
        byte[] pcx = new PcxBuilder { Width = 1, Bits = 8, Planes = 2, Rows = [[1, 2]] }.Build();
        Assert.ThrowsExactly<NotSupportedException>(() => Image.Load(pcx));
        Assert.ThrowsExactly<NotSupportedException>(() => Image.Identify(pcx));
    }

    [Test]
    public void InvertedWindow_IsCorrupt()
    {
        byte[] pcx = new PcxBuilder { Width = 2, Height = 1 }.Build();
        pcx[8] = 0;
        pcx[9] = 0;
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(pcx));
    }
}
