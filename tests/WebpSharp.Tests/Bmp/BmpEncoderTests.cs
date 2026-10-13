using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using WebpSharp.Tests.Infrastructure;

namespace WebpSharp.Tests.Bmp;

public class BmpEncoderTests
{
    private static (int HeaderSize, int Bits) Header(byte[] bmp) =>
        (BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(14)), BinaryPrimitives.ReadUInt16LittleEndian(bmp.AsSpan(28)));

    private static Image Opaque(Image image)
    {
        foreach (ref Rgba32 p in image.GetPixelSpan())
        {
            p.A = 255;
        }

        return image;
    }

    /// <summary>An opaque image with exactly <paramref name="colors"/> distinct colors and an odd width.</summary>
    private static Image WithColors(int colors)
    {
        var image = new Image(37, 11);
        Span<Rgba32> px = image.GetPixelSpan();
        for (int i = 0; i < px.Length; i++)
        {
            int c = i % colors;
            px[i] = new Rgba32((byte)((c >> 8) * 100), (byte)(c * 13), (byte)c, 255);
        }

        return image;
    }

    public static TheoryData<int, int> ColorCounts => new() { { 2, 1 }, { 16, 4 }, { 256, 8 }, { 257, 24 } };

    [Theory]
    [MemberData(nameof(ColorCounts))]
    public void Auto_PicksSmallestDepth_AndRoundTrips(int colors, int expectedBits)
    {
        using Image image = WithColors(colors);
        byte[] bmp = image.ToBmp();
        Assert.Equal((40, expectedBits), Header(bmp));
        using Image back = Image.FromBytes(bmp);
        Assert.Equal(image.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
        Assert.False(back.HasAlpha);
    }

    [Fact]
    public void Auto_WithAlpha_Writes32BitV5()
    {
        using Image image = Image.FromFile(Corpus.PathOf("png/odd_rgba.png"));
        byte[] bmp = image.ToBmp();
        Assert.Equal((124, 32), Header(bmp));
        using Image back = Image.FromBytes(bmp);
        Assert.True(back.HasAlpha);
        Assert.Equal(image.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(32)]
    public void ForcedDepth_RoundTrips(int bits)
    {
        using Image image = bits <= 8 ? WithColors(1 << bits) : Opaque(Image.FromFile(Corpus.PathOf("png/odd_rgba.png")));
        byte[] bmp = image.ToBmp(new BmpEncoderOptions { BitsPerPixel = bits });
        Assert.Equal(bits, Header(bmp).Bits);
        using Image back = Image.FromBytes(bmp);
        Assert.Equal(image.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Fact]
    public void ForcedOpaqueDepth_DropsAlpha()
    {
        using Image image = new(2, 1, System.Drawing.Color.FromArgb(10, 1, 2, 3));
        using Image back = Image.FromBytes(image.ToBmp(new BmpEncoderOptions { BitsPerPixel = 24 }));
        Assert.Equal(new Rgba32(1, 2, 3, 255), back[1, 0]);
        using Image pal = Image.FromBytes(image.ToBmp(new BmpEncoderOptions { BitsPerPixel = 1 }));
        Assert.Equal(new Rgba32(1, 2, 3, 255), pal[0, 0]);
    }

    [Fact]
    public void ForcedPalette_TooManyColors_Throws()
    {
        using Image image = WithColors(17);
        Assert.Throws<InvalidOperationException>(() => image.ToBmp(new BmpEncoderOptions { BitsPerPixel = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ToBmp(new BmpEncoderOptions { BitsPerPixel = 16 }));
    }

    [Fact]
    public void Metadata_IccAndResolution_RoundTrip()
    {
        using Image image = WithColors(300);
        image.SetResolution(300, 72);
        image.Metadata.IccProfile = [9, 8, 7, 6, 5];
        byte[] bmp = image.ToBmp();
        Assert.Equal((124, 24), Header(bmp));
        using Image back = Image.FromBytes(bmp);
        Assert.Equal(image.Metadata.IccProfile, back.Metadata.IccProfile);
        Assert.Equal(300f, back.HorizontalResolution, 0.05f);
        Assert.Equal(72f, back.VerticalResolution, 0.05f);
        Assert.Equal(image.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());

        byte[] stripped = image.ToBmp(new BmpEncoderOptions { Metadata = MetadataKinds.None });
        Assert.Equal(40, Header(stripped).HeaderSize);
    }

    [Fact]
    public void Save_ChoosesBmpFromExtension()
    {
        string path = Path.Combine(Path.GetTempPath(), $"webpsharp-{Guid.NewGuid():N}.bmp");
        try
        {
            using Image image = WithColors(5);
            image.Save(path);
            Assert.Equal(ImageFormat.Bmp, Image.Identify(path).Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(BmpDecoderTests.BmpFiles), MemberType = typeof(BmpDecoderTests))]
    public void Corpus_RoundTrip_IsLossless(string file)
    {
        using Image image = Image.FromFile(Corpus.PathOf(file));
        using Image back = Image.FromBytes(image.ToBmp());
        Assert.Equal(image.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Trait("Category", "Oracle")]
    [Fact]
    public void PillowAndImageMagick_DecodeOurBmpsIdentically()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"webpsharp-bmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var expected = new Dictionary<string, string>();
            void Add(string name, Image image, BmpEncoderOptions options)
            {
                image.Save(Path.Combine(dir, name), options);
                expected[name] = Corpus.Sha256(MemoryMarshal.AsBytes(image.GetPixelSpan()));
            }

            foreach (int bits in new[] { 1, 4, 8 })
            {
                using Image image = WithColors(1 << bits);
                Add($"pal{bits}.bmp", image, new BmpEncoderOptions { BitsPerPixel = bits });
            }

            using (Image photo = Image.FromFile(Corpus.PathOf("png/rgb8.png")))
            {
                Add("rgb24.bmp", photo, new BmpEncoderOptions());
            }

            using (Image alpha = Image.FromFile(Corpus.PathOf("png/odd_rgba.png")))
            {
                alpha.Metadata.IccProfile = null;
                Add("rgba32.bmp", alpha, new BmpEncoderOptions());
            }

            using (Image icc = WithColors(1000))
            {
                icc.Metadata.IccProfile = new byte[128];
                Add("rgb24_icc.bmp", icc, new BmpEncoderOptions());
            }

            string script = Path.Combine(Corpus.Assets, "bmp_reference.py");
            if (ReferenceTools.Run("python3", $"-I \"{script}\" \"{dir}\"", out string json) != 0)
            {
                return;  // Pillow not available
            }

            var actual = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json[..(json.LastIndexOf('}') + 1)])!;
            foreach ((string name, string hash) in expected)
            {
                Assert.True(hash == actual[name]["pillow"], $"{name}: Pillow decodes differently");
                Assert.True(hash == actual[name]["imagemagick"], $"{name}: ImageMagick decodes differently");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
