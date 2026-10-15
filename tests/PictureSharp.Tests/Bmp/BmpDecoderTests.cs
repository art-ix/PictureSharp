using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;
using PictureSharp.Tests.WebP;

namespace PictureSharp.Tests.Bmp;

public class BmpDecoderTests
{
    private static readonly Rgba32 Black = new(0, 0, 0, 255);
    private static readonly Rgba32[] Pal4 = [new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255), new(255, 255, 255, 255)];

    public static TheoryData<string> BmpFiles => Corpus.Files("bmp/");

    private static Rgba32[] Pixels(byte[] bmp, DecoderOptions? options = null)
    {
        using Image image = Image.Load(bmp, options);
        return image.GetPixelSpan().ToArray();
    }

    [Theory]
    [MemberData(nameof(BmpFiles))]
    public void Decode_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        using Image image = Image.Load(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Bmp, image.SourceFormat);
        Assert.Equal(r.HasAlpha, image.HasAlpha);
        LosslessDecoderTests.AssertMatchesReference(file, image);
        if (r.Dpi is { } dpi)
        {
            Assert.Equal(dpi[0], image.Metadata.HorizontalResolution, 1);
            Assert.Equal(dpi[1], image.Metadata.VerticalResolution, 1);
        }
    }

    [Theory]
    [MemberData(nameof(BmpFiles))]
    public void Identify_MatchesReference(string file)
    {
        ReferenceEntry r = Corpus.References[file];
        ImageInfo info = Image.Identify(Corpus.PathOf(file));
        Assert.Equal(ImageFormat.Bmp, info.Format);
        Assert.Equal((r.Width, r.Height), (info.Width, info.Height));
        Assert.Equal(r.HasAlpha, info.HasAlpha);
        Assert.Equal(1, info.FrameCount);
    }

    [Fact]
    public void Detect_RecognizesBmp()
    {
        Assert.Equal(ImageFormat.Bmp, Image.DetectFormat(Corpus.Read("bmp/im_rgb24.bmp")));
        Assert.Equal(ImageFormat.Bmp, ImageFormat.FromExtension("x.DIB"));
        Assert.True(ImageFormat.Bmp.CanDecode);
        Assert.True(ImageFormat.Bmp.CanEncode);
    }

    [Fact]
    public void Pal2_UnpacksTwoBitIndices()
    {
        // 5 pixels: 0 1 2 3 | 1 -> 0b00011011, 0b01000000
        byte[] bmp = new BmpBuilder { Width = 5, Height = 1, Bits = 2, Palette = Pal4, Rows = [[0x1B, 0x40]] }.Build();
        Assert.Equal([Pal4[0], Pal4[1], Pal4[2], Pal4[3], Pal4[1]], Pixels(bmp));
    }

    [Fact]
    public void Palette_IndexBeyondPalette_IsBlack()
    {
        byte[] bmp = new BmpBuilder { Width = 3, Height = 1, Bits = 8, Palette = Pal4[..2], Rows = [[0, 1, 200]] }.Build();
        Assert.Equal([Pal4[0], Pal4[1], Black], Pixels(bmp));
    }

    [Fact]
    public void TopDown_KeepsRowOrder()
    {
        byte[][] rows = [[0, 0], [1, 0], [2, 0]];
        Rgba32[] bottomUp = Pixels(new BmpBuilder { Width = 1, Height = 3, Bits = 8, Palette = Pal4, Rows = rows }.Build());
        Rgba32[] topDown = Pixels(new BmpBuilder { Width = 1, Height = 3, Bits = 8, Palette = Pal4, Rows = rows, TopDown = true }.Build());
        Assert.Equal([Pal4[0], Pal4[1], Pal4[2]], bottomUp);
        Assert.Equal(bottomUp, topDown);
    }

    [Fact]
    public void Rle8_HandlesDeltaEndOfLineAndAbsoluteRuns()
    {
        byte[] stream =
        [
            2, 1,               // row 0: two pixels of index 1
            0, 2, 1, 1,         // delta: right 1, up 1 -> (3, 1)
            1, 3,               // (3, 1) = 3
            0, 0,               // end of line -> row 2
            0, 3, 0, 1, 2, 0,   // absolute run of 3, padded to a word
            0, 1,               // end of bitmap
        ];
        byte[] bmp = new BmpBuilder { Width = 4, Height = 3, Bits = 8, Compression = 1, Palette = Pal4, Data = stream }.Build();
        Rgba32 z = Pal4[0];
        Assert.Equal(
            [
                Pal4[0], Pal4[1], Pal4[2], z,   // file row 2 (top)
                z, z, z, Pal4[3],               // file row 1
                Pal4[1], Pal4[1], z, z,         // file row 0 (bottom)
            ],
            Pixels(bmp));
    }

    [Fact]
    public void Rle4_OddAbsoluteRunAndAlternatingNibbles()
    {
        byte[] stream =
        [
            0, 3, 0x12, 0x30,   // absolute: 1 2 3 (two bytes, already word aligned)
            3, 0x01,            // encoded: 0 1 0
            0, 1,
        ];
        byte[] bmp = new BmpBuilder { Width = 6, Height = 1, Bits = 4, Compression = 2, Palette = Pal4, Data = stream }.Build();
        Assert.Equal([Pal4[1], Pal4[2], Pal4[3], Pal4[0], Pal4[1], Pal4[0]], Pixels(bmp));
    }

    [Fact]
    public void Rle_ClipsLongRunsAndToleratesTruncation()
    {
        byte[] stream = [255, 2, 0, 0, 3];  // a run wider than the image, then a cut-off command
        byte[] bmp = new BmpBuilder { Width = 2, Height = 2, Bits = 8, Compression = 1, Palette = Pal4, Data = stream }.Build();
        Assert.Equal([Pal4[0], Pal4[0], Pal4[2], Pal4[2]], Pixels(bmp));
    }

    [Fact]
    public void Rle_HostileDeltas_DoNotOverflow()
    {
        var stream = new List<byte>();
        for (int i = 0; i < 100_000; i++)
        {
            stream.AddRange([0, 2, 255, 0]);
        }

        byte[] bmp = new BmpBuilder { Width = 2, Height = 2, Bits = 8, Compression = 1, Palette = Pal4, Data = [.. stream] }.Build();
        Assert.All(Pixels(bmp), p => Assert.Equal(Pal4[0], p));
    }

    [Fact]
    public void BitFields_V3_ReadsAlphaMask()
    {
        // RGBA byte order: R = 0x000000FF, G = 0x0000FF00, B = 0x00FF0000, A = 0xFF000000.
        byte[] bmp = new BmpBuilder
        {
            Width = 2,
            Height = 1,
            Bits = 32,
            Compression = 3,
            HeaderSize = 56,
            Masks = [0xFF, 0xFF00, 0xFF0000, 0xFF000000],
            Rows = [[10, 20, 30, 40, 50, 60, 70, 255]],
        }.Build();
        using Image image = Image.Load(bmp);
        Assert.True(image.HasAlpha);
        Assert.Equal([new Rgba32(10, 20, 30, 40), new Rgba32(50, 60, 70, 255)], image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void BitFields_V2Header_HasNoAlpha()
    {
        byte[] bmp = new BmpBuilder
        {
            Width = 1,
            Height = 1,
            Bits = 16,
            Compression = 3,
            HeaderSize = 52,
            Masks = [0xF800, 0x7E0, 0x1F],
            Rows = [[0xFF, 0xFF]],
        }.Build();
        using Image image = Image.Load(bmp);
        Assert.False(image.HasAlpha);
        Assert.Equal(new Rgba32(255, 255, 255, 255), image[0, 0]);
    }

    [Fact]
    public void AlphaBitFields_InfoHeader_ReadsFourMasks()
    {
        byte[] bmp = new BmpBuilder
        {
            Width = 1,
            Height = 1,
            Bits = 16,
            Compression = 6,
            Masks = [0x7C00, 0x3E0, 0x1F, 0x8000],
            Rows = [[0x1F, 0x00]],  // blue only, alpha bit clear
        }.Build();
        using Image image = Image.Load(bmp);
        Assert.True(image.HasAlpha);
        Assert.Equal(new Rgba32(0, 0, 255, 0), image[0, 0]);
    }

    [Fact]
    public void BitFields_ScaleArbitraryWidthsLikePillow()
    {
        // 32-bit 10-10-10-2 and 16-bit 1-5-5-5: v * 255 / max, truncated.
        uint p32 = (1023u << 20) | (512u << 10) | 1u | (1u << 30);
        byte[] bmp = new BmpBuilder
        {
            Width = 1,
            Height = 1,
            Bits = 32,
            Compression = 3,
            HeaderSize = 124,
            Masks = [0x3FF00000, 0xFFC00, 0x3FF, 0xC0000000],
            Rows = [BitConverter.GetBytes(p32)],
        }.Build();
        Assert.Equal(new Rgba32(255, 512 * 255 / 1023, 0, 85), Pixels(bmp)[0]);

        ushort p16 = (ushort)(0x8000 | (16 << 10) | (1 << 5) | 31);
        bmp = new BmpBuilder
        {
            Width = 1,
            Height = 1,
            Bits = 16,
            Compression = 3,
            HeaderSize = 124,
            Masks = [0x7C00, 0x3E0, 0x1F, 0x8000],
            Rows = [BitConverter.GetBytes(p16)],
        }.Build();
        Assert.Equal(new Rgba32(16 * 255 / 31, 8, 255, 255), Pixels(bmp)[0]);
    }

    [Fact]
    public void Rgb32_IgnoresReservedByte()
    {
        byte[] bmp = new BmpBuilder { Width = 1, Height = 1, Bits = 32, Rows = [[1, 2, 3, 77]] }.Build();
        using Image image = Image.Load(bmp);
        Assert.False(image.HasAlpha);
        Assert.Equal(new Rgba32(3, 2, 1, 255), image[0, 0]);
    }

    [Fact]
    public void Os2V2_TruncatedHeader_Decodes()
    {
        byte[] bmp = new BmpBuilder { Width = 2, Height = 1, Bits = 8, HeaderSize = 16, Palette = Pal4, ColorsUsed = 0, Rows = [[3, 2]] }.Build();
        Assert.Equal([Pal4[3], Pal4[2]], Pixels(bmp));
    }

    [Fact]
    public void Os2V1_PaletteSizeFollowsDataOffset()
    {
        // Only four 3-byte entries before the pixels, although 8 bits allow 256.
        byte[] bmp = new BmpBuilder { Width = 2, Height = 1, Bits = 8, HeaderSize = 12, Palette = Pal4, Rows = [[1, 5]] }.Build();
        Assert.Equal([Pal4[1], Black], Pixels(bmp));
    }

    [Fact]
    public void ZeroDataOffset_MeansAfterPalette()
    {
        byte[] bmp = new BmpBuilder { Width = 1, Height = 1, Bits = 8, Palette = Pal4, OffBits = 0, Rows = [[2]] }.Build();
        Assert.Equal([Pal4[2]], Pixels(bmp));
    }

    [Fact]
    public void MissingPaddingOfLastRow_IsAccepted()
    {
        byte[] bmp = new BmpBuilder { Width = 1, Height = 2, Bits = 24, Rows = [[1, 2, 3], [4, 5, 6]] }.Build();
        Assert.Equal([new Rgba32(3, 2, 1, 255), new Rgba32(6, 5, 4, 255)], Pixels(bmp[..^1]));
        Assert.Throws<ImageFormatException>(() => Pixels(bmp[..^5]));
    }

    [Fact]
    public void Resolution_ComesFromPixelsPerMeter()
    {
        using Image image = Image.Load(new BmpBuilder { Width = 1, Height = 1, PixelsPerMeter = 11811, Rows = [[0, 0, 0]] }.Build());
        Assert.Equal(300f, image.Metadata.HorizontalResolution, 0.01f);
        using Image unspecified = Image.Load(new BmpBuilder { Width = 1, Height = 1, Rows = [[0, 0, 0]] }.Build());
        Assert.Equal(96f, unspecified.Metadata.HorizontalResolution);
    }

    [Fact]
    public void V5_EmbeddedProfile_IsRead()
    {
        byte[] icc = [1, 2, 3, 4, 5, 6, 7];
        byte[] bmp = new BmpBuilder
        {
            Width = 1,
            Height = 1,
            HeaderSize = 124,
            Rows = [[0, 0, 0]],
            PatchHeader = h =>
            {
                BitConverter.GetBytes(0x4D424544u).CopyTo(h, 56);
                BitConverter.GetBytes(124u + 4).CopyTo(h, 112);  // header + one padded pixel row
                BitConverter.GetBytes((uint)icc.Length).CopyTo(h, 116);
            },
            Trailer = icc,
        }.Build();
        using Image image = Image.Load(bmp);
        Assert.Equal(icc, image.Metadata.IccProfile);
        using Image noMeta = Image.Load(bmp, new DecoderOptions { IgnoreMetadata = true });
        Assert.Null(noMeta.Metadata.IccProfile);
    }

    [Fact]
    public void EmbeddedPng_IsDecoded()
    {
        using var source = new Image(3, 2, System.Drawing.Color.FromArgb(128, 10, 20, 30));
        source[1, 1] = new Rgba32(200, 100, 50, 255);
        byte[] png = source.ToPng();
        byte[] bmp = new BmpBuilder { Width = 3, Height = 2, Bits = 0, Compression = 5, Data = png, ImageSize = (uint)png.Length }.Build();
        using Image image = Image.Load(bmp);
        Assert.Equal(ImageFormat.Bmp, image.SourceFormat);
        Assert.True(image.HasAlpha);
        Assert.Equal(source.GetPixelSpan().ToArray(), image.GetPixelSpan().ToArray());
        Assert.True(Image.Identify(bmp).HasAlpha);
    }

    [Fact]
    public void EmbeddedJpeg_IsDecoded()
    {
        byte[] jpeg = Corpus.Read("jpeg/odd_420.jpg");
        byte[] bmp = new BmpBuilder { Width = 171, Height = 93, Bits = 0, Compression = 4, Data = jpeg }.Build();
        using Image image = Image.Load(bmp);
        using Image direct = Image.Load(jpeg);
        Assert.Equal(ImageFormat.Bmp, image.SourceFormat);
        Assert.Equal(direct.GetPixelSpan().ToArray(), image.GetPixelSpan().ToArray());

        byte[] broken = new BmpBuilder { Width = 1, Height = 1, Bits = 0, Compression = 4, Data = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0] }.Build();
        Assert.Throws<ImageFormatException>(() => Image.Load(broken));
    }

    public static TheoryData<string, byte[]> InvalidFiles() => new()
    {
        { "header size", new BmpBuilder { Width = 1, Height = 1, HeaderSize = 100, Rows = [[0, 0, 0]] }.Build() },
        { "planes", new BmpBuilder { Width = 1, Height = 1, Planes = 2, Rows = [[0, 0, 0]] }.Build() },
        { "bit count", new BmpBuilder { Width = 1, Height = 1, Bits = 7, Rows = [[0]] }.Build() },
        { "zero width", new BmpBuilder { Width = 0, Height = 1, Rows = [[0, 0, 0]] }.Build() },
        { "zero height", new BmpBuilder { Width = 1, Height = 0, Rows = [] }.Build() },
        { "rle8 with 4 bits", new BmpBuilder { Width = 1, Height = 1, Bits = 4, Compression = 1, Palette = Pal4, Data = [0, 1] }.Build() },
        { "split mask", new BmpBuilder { Width = 1, Height = 1, Bits = 16, Compression = 3, Masks = [0x7C00, 0x3E0, 0x15], Rows = [[0, 0]] }.Build() },
        { "mask too wide", new BmpBuilder { Width = 1, Height = 1, Bits = 16, Compression = 3, Masks = [0x7C000, 0x3E0, 0x1F], Rows = [[0, 0]] }.Build() },
        { "unknown compression", new BmpBuilder { Width = 1, Height = 1, Compression = 9, Rows = [[0, 0, 0]] }.Build() },
        { "palette size", new BmpBuilder { Width = 1, Height = 1, Bits = 8, ColorsUsed = 100_000, Palette = Pal4, Rows = [[0]] }.Build() },
        { "truncated palette", new BmpBuilder { Width = 1, Height = 1, Bits = 8, Palette = Pal4, Rows = [[0]] }.Build()[..60] },
        { "offset beyond file", new BmpBuilder { Width = 1, Height = 1, OffBits = 5000, Rows = [[0, 0, 0]] }.Build()[..60] },
        { "embedded mismatch", new BmpBuilder { Width = 1, Height = 1, Bits = 0, Compression = 5, Data = [1, 2, 3, 4] }.Build() },
        { "truncated header", new BmpBuilder { Width = 1, Height = 1, HeaderSize = 124, Rows = [[0, 0, 0]] }.Build()[..40] },
    };

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Decode_InvalidFile_ThrowsFormatException(string reason, byte[] bmp)
    {
        Assert.NotNull(reason);
        Assert.Throws<ImageFormatException>(() => Image.Load(bmp));
    }

    [Theory]
    [InlineData(64, 0u)]
    [InlineData(32, 11u)]
    [InlineData(1, 3u)]   // OS/2 Huffman 1D
    public void Decode_UnsupportedVariant_ThrowsNotSupported(int bits, uint compression)
    {
        byte[] bmp = new BmpBuilder { Width = 1, Height = 1, Bits = bits, Compression = compression, HeaderSize = 64, Rows = [new byte[8]] }.Build();
        Assert.Throws<NotSupportedException>(() => Image.Load(bmp));
    }

    [Fact]
    public void Decode_RespectsMaxPixels()
    {
        byte[] bmp = new BmpBuilder { Width = 100_000, Height = 100_000, Bits = 1, Palette = Pal4[..2], Rows = [] }.Build();
        Assert.Throws<ImageFormatException>(() => Image.Load(bmp, new DecoderOptions { MaxPixels = 1 << 20 }));
    }
}
