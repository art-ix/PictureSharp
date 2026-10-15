using PictureSharp.IO;

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
        using Image image = ImageReader.Load(pcx, options);
        return image.GetPixelSpan().ToArray();
    }

    [Fact]
    public void Detect_RecognizesPcx()
    {
        byte[] pcx = new PcxBuilder { Rows = [[1]], BytesPerLine = 1, VgaPalette = PcxBuilder.Vga(Red) }.Build();
        Assert.Equal(ImageFormat.Pcx, ImageFormats.DetectFormat(pcx));
        Assert.Equal(ImageFormat.Pcx, ImageFormats.FromExtension("scan.PCX"));
        Assert.Equal(ImageFormat.Pcx, ImageFormats.FromMimeType("image/x-pcx"));
        Assert.True(ImageFormats.CanDecode(ImageFormat.Pcx));
        Assert.True(ImageFormats.CanEncode(ImageFormat.Pcx));
        Assert.Null(ImageFormats.DetectFormat([0x0A, 6, 1, 8]));
        Assert.Null(ImageFormats.DetectFormat([0x0A, 5, 2, 8]));
        Assert.Null(ImageFormats.DetectFormat([0x0A, 5, 1, 3]));
    }

    [Fact]
    public void Mono_EmptyPalette_IsBlackAndWhite()
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
        Assert.Equal(
            [White, Rgba32.Black, White, Rgba32.Black, Rgba32.Black, Rgba32.Black, Rgba32.Black, White, White],
            Pixels(pcx));
    }

    [Fact]
    public void Mono_HeaderPalette_KeepsColors()
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
        Assert.Equal([Blue, Red], Pixels(pcx));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Planar_UnpacksLeastSignificantPlaneFirst(int planes)
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
        Assert.Equal([palette[all], palette[1]], Pixels(pcx));
    }

    [Fact]
    public void Packed2_ReadsMostSignificantBitsFirst()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 5,
            Bits = 2,
            Planes = 1,
            EgaPalette = PcxBuilder.Rgb(Pal4),
            Rows = [[0x1B, 0x40]],
        }.Build();
        Assert.Equal([Pal4[0], Pal4[1], Pal4[2], Pal4[3], Pal4[1]], Pixels(pcx));
    }

    [Fact]
    public void Packed4_ReadsNibbles()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 3,
            Bits = 4,
            Planes = 1,
            EgaPalette = PcxBuilder.Rgb(Pal4),
            Rows = [[0x12, 0x30]],
        }.Build();
        Assert.Equal([Pal4[1], Pal4[2], Pal4[3]], Pixels(pcx));
    }

    [Fact]
    public void Indexed_UsesTrailingVgaPalette()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 3,
            Height = 1,
            BytesPerLine = 3,
            Rows = [[2, 0, 1]],
            VgaPalette = PcxBuilder.Vga(Red, Green, Blue),
        }.Build();
        Assert.Equal([Blue, Red, Green], Pixels(pcx));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Indexed_PaletteMarkerInsideRaster_IsNotATrailer(int encoding)
    {
        var row = new byte[770];
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = (byte)((x & 1) == 0 ? 10 : 12);
        }

        byte[] pcx = new PcxBuilder { Width = row.Length, Encoding = encoding, Rows = [row] }.Build();
        Assert.Equal(12, pcx[^769]);
        Rgba32[] expected = row.Select(value => new Rgba32(value, value, value)).ToArray();
        Assert.Equal(expected, Pixels(pcx));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Indexed_RasterMarkerDoesNotOverrideActualPalette(int encoding)
    {
        var row = new byte[770];
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = (byte)((x & 1) == 0 ? 10 : 12);
        }

        Rgba32[] colors = Enumerable.Range(0, 256).Select(i => new Rgba32((byte)i, (byte)(255 - i), 42)).ToArray();
        byte[] pcx = new PcxBuilder { Width = row.Length, Encoding = encoding, Rows = [row], VgaPalette = PcxBuilder.Vga(colors) }.Build();
        Assert.Equal(row.Select(index => colors[index]).ToArray(), Pixels(pcx));
    }

    [Fact]
    public void Indexed_MissingPalette_IsGrayscale()
    {
        byte[] pcx = new PcxBuilder { Width = 3, BytesPerLine = 3, Rows = [[0, 128, 255]] }.Build();
        Assert.Equal([new Rgba32(0, 0, 0), new Rgba32(128, 128, 128), new Rgba32(255, 255, 255)], Pixels(pcx));
    }

    [Fact]
    public void Rgb_ReadsPlanes()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Bits = 8,
            Planes = 3,
            Rows = [[10, 11, 20, 21, 30, 31]],
        }.Build();
        Assert.Equal([new Rgba32(10, 20, 30), new Rgba32(11, 21, 31)], Pixels(pcx));
    }

    [Fact]
    public void Rgba_ReadsStraightAlpha()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Bits = 8,
            Planes = 4,
            Rows = [[1, 2, 3, 4, 5, 6, 7, 8]],
        }.Build();
        using Image image = ImageReader.Load(pcx);
        Assert.True(image.HasAlpha);
        Assert.Equal([new Rgba32(1, 3, 5, 7), new Rgba32(2, 4, 6, 8)], image.GetPixelSpan().ToArray());
        ImageInfo info = ImageReader.Identify(pcx);
        Assert.True(info.HasAlpha);
        Assert.Equal(ImageFormat.Pcx, info.Format);
    }

    [Fact]
    public void Origin_DoesNotChangeTheSize()
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
        using Image image = ImageReader.Load(pcx);
        Assert.Equal((2, 2), (image.Width, image.Height));
        Assert.Equal([Green, Blue, White, Red], image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Uncompressed_ReadsRawScanlines()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Encoding = 0,
            Rows = [[9, 8]],
            VgaPalette = PcxBuilder.Vga(Red, Green, Blue, White, new(1, 1, 1), new(2, 2, 2), new(3, 3, 3), new(4, 4, 4), new(5, 5, 5), new(9, 9, 9)),
        }.Build();
        Assert.Equal([new Rgba32(9, 9, 9), new Rgba32(5, 5, 5)], Pixels(pcx));
    }

    [Fact]
    public void Run_MayCrossPlanes()
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
        Assert.Equal([White], Pixels(pcx));
    }

    [Fact]
    public void Run_OfZero_WritesNothing()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 1,
            BytesPerLine = 1,
            Rows = null,
            Data = [0xC0, 0x11, 4],
            VgaPalette = PcxBuilder.Vga(Red, Green, Blue, White, new(4, 4, 4)),
        }.Build();
        Assert.Equal([new Rgba32(4, 4, 4)], Pixels(pcx));
    }

    [Fact]
    public void LiteralAboveMarker_IsARunOfOne()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 1,
            BytesPerLine = 1,
            Data = [0xC1, 0xC5],
        }.Build();
        Assert.Equal([new Rgba32(0xC5, 0xC5, 0xC5)], Pixels(pcx));
    }

    [Fact]
    public void Dpi_RequiresBothAxes()
    {
        byte[] both = new PcxBuilder { Width = 1, BytesPerLine = 1, Rows = [[0]], HorizontalDpi = 72, VerticalDpi = 144 }.Build();
        byte[] one = new PcxBuilder { Width = 1, BytesPerLine = 1, Rows = [[0]], HorizontalDpi = 72 }.Build();
        using Image image = ImageReader.Load(both);
        Assert.Equal((72f, 144f), (image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution));
        using Image missing = ImageReader.Load(one);
        Assert.Equal((96f, 96f), (missing.Metadata.HorizontalResolution, missing.Metadata.VerticalResolution));
    }

    [Fact]
    public void Identify_DoesNotNeedPixelData()
    {
        byte[] pcx = new PcxBuilder { Width = 6, Height = 4, Bits = 8, Planes = 3, HorizontalDpi = 300, VerticalDpi = 300 }.Build();
        ImageInfo info = ImageReader.Identify(pcx);
        Assert.Equal((6, 4, false), (info.Width, info.Height, info.HasAlpha));
        Assert.Equal((300f, 300f), (info.HorizontalResolution, info.VerticalResolution));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(pcx));
    }

    [Fact]
    public void Stride_RejectsValuesBelowTheMinimumAndHonorsPadding()
    {
        byte[] tooSmall = new PcxBuilder { Width = 3, Bits = 8, Planes = 1, BytesPerLine = 2, Rows = [[1, 2]] }.Build();
        byte[] paddedRgb = new PcxBuilder { Width = 1, Bits = 8, Planes = 3, BytesPerLine = 8, Rows = [[7, 0, 0, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 9, 0, 0, 0, 0, 0, 0, 0]] }.Build();
        byte[] padded = new PcxBuilder { Width = 1, Bits = 8, Planes = 1, BytesPerLine = 4, Rows = [[7, 0, 0, 0]] }.Build();
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(tooSmall));
        Assert.Equal([new Rgba32(7, 8, 9)], Pixels(paddedRgb));
        Assert.Equal([new Rgba32(7, 7, 7)], Pixels(padded));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(126)]
    [InlineData(127)]
    public void MaximallyCompressedRows_AreNotRejectedAsTruncated(int width)
    {
        var row = new byte[width];
        row.AsSpan().Fill(42);
        byte[] pcx = new PcxBuilder { Width = width, Height = 3, BytesPerLine = width, Rows = [row, row, row] }.Build();
        Assert.All(Pixels(pcx), pixel => Assert.Equal(new Rgba32(42, 42, 42), pixel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TruncatedLargeImage_IsRejectedBeforePixelAllocation(int encoding)
    {
        byte[] pcx = new PcxBuilder { Width = 4096, Height = 4096, Encoding = encoding }.Build();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ImageFormatException error = Assert.Throws<ImageFormatException>(() => ImageReader.Load(pcx));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("truncated", error.Message);
        Assert.True(allocated < 65536, $"Allocated {allocated} bytes for a header without image data.");
    }

    [Fact]
    public void Indexed_PaletteMarkerInsideHeader_IsNotATrailer()
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 658,
            Encoding = 0,
            EgaPalette = PcxBuilder.Rgb(new Rgba32(0, 12, 0)),
            Rows = [new byte[658]],
        }.Build();
        Assert.Equal(12, pcx[^769]);
        Assert.All(Pixels(pcx), pixel => Assert.Equal(Rgba32.Black, pixel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Mono_VersionWithoutPalette_IgnoresHeaderColors(int version)
    {
        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Bits = 1,
            Version = version,
            EgaPalette = PcxBuilder.Rgb(Red, Blue),
            Rows = [[0x80, 0]],
        }.Build();
        Assert.Equal([White, Rgba32.Black], Pixels(pcx));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Color_VersionWithoutPalette_IsNotSupported(int version)
    {
        byte[] pcx = new PcxBuilder { Width = 2, Bits = 1, Planes = 4, Version = version, Rows = [new byte[8]] }.Build();
        Assert.Throws<NotSupportedException>(() => ImageReader.Load(pcx));
        Assert.Throws<NotSupportedException>(() => ImageReader.Identify(pcx));
    }

    [Fact]
    public void VersionOne_IsInvalid()
    {
        byte[] pcx = new PcxBuilder { Version = 1, Rows = [[0, 0]] }.Build();
        Assert.Null(ImageFormats.DetectFormat(pcx));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(pcx));
        Assert.Throws<ImageFormatException>(() => ImageReader.Identify(pcx));
    }

    [Fact]
    public void RunPastScanline_AndTruncation_AreCorrupt()
    {
        byte[] overrun = new PcxBuilder { Width = 1, Bits = 8, Planes = 1, BytesPerLine = 1, Data = [0xC2, 1] }.Build();
        byte[] truncated = new PcxBuilder { Width = 2, Bits = 8, Planes = 1, Data = [1] }.Build();
        byte[] shortPalette = new PcxBuilder { Width = 1, BytesPerLine = 1, Rows = [[0]], Trailing = [0x0C, 1, 2, 3] }.Build();
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(overrun));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(truncated));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(shortPalette));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(overrun[..10]));
    }

    [Fact]
    public void MaxPixels_IsCheckedBeforeDecoding()
    {
        byte[] pcx = new PcxBuilder { Width = 100, Height = 100, Bits = 8, Planes = 3 }.Build();
        ImageFormatException ex = Assert.Throws<ImageFormatException>(() => ImageReader.Load(pcx, new DecoderOptions { MaxPixels = 10 }));
        Assert.Contains("MaxPixels", ex.Message);
    }

    [Fact]
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
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(header, new DecoderOptions { MaxPixels = long.MaxValue }));
    }

    [Fact]
    public void UnsupportedPlaneCount_IsNotSupported()
    {
        byte[] pcx = new PcxBuilder { Width = 1, Bits = 8, Planes = 2, Rows = [[1, 2]] }.Build();
        Assert.Throws<NotSupportedException>(() => ImageReader.Load(pcx));
        Assert.Throws<NotSupportedException>(() => ImageReader.Identify(pcx));
    }

    [Fact]
    public void InvertedWindow_IsCorrupt()
    {
        byte[] pcx = new PcxBuilder { Width = 2, Height = 1 }.Build();
        pcx[8] = 0;
        pcx[9] = 0;
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(pcx));
    }
}
