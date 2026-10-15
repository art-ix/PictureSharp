using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using PictureSharp.Formats.Bmp;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Bmp;

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

    public static TestCases<int, int> ColorCounts => new() { { 2, 1 }, { 16, 4 }, { 256, 8 }, { 257, 24 } };

    [Test]
    [MethodDataSource(nameof(ColorCounts))]
    public async Task Auto_PicksSmallestDepth_AndRoundTrips(int colors, int expectedBits)
    {
        using Image image = WithColors(colors);
        byte[] bmp = image.ToBmp();
        await Assert.That(Header(bmp)).IsEqualTo((40, expectedBits));
        using Image back = Image.Load(bmp);
        await Assert.That(back.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        await Assert.That(back.HasAlpha).IsFalse();
    }

    [Test]
    public async Task Auto_WithAlpha_Writes32BitV5()
    {
        using Image image = Image.Load(Corpus.PathOf("png/odd_rgba.png"));
        byte[] bmp = image.ToBmp();
        await Assert.That(Header(bmp)).IsEqualTo((124, 32));
        using Image back = Image.Load(bmp);
        await Assert.That(back.HasAlpha).IsTrue();
        await Assert.That(back.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(24)]
    [Arguments(32)]
    public async Task ForcedDepth_RoundTrips(int bits)
    {
        using Image image = bits <= 8 ? WithColors(1 << bits) : Opaque(Image.Load(Corpus.PathOf("png/odd_rgba.png")));
        byte[] bmp = image.ToBmp(new BmpEncoderOptions { BitsPerPixel = bits });
        await Assert.That(Header(bmp).Bits).IsEqualTo(bits);
        using Image back = Image.Load(bmp);
        await Assert.That(back.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task ForcedOpaqueDepth_DropsAlpha()
    {
        using Image image = new(2, 1, System.Drawing.Color.FromArgb(10, 1, 2, 3));
        using Image back = Image.Load(image.ToBmp(new BmpEncoderOptions { BitsPerPixel = 24 }));
        await Assert.That(back[1, 0]).IsEqualTo(new Rgba32(1, 2, 3, 255));
        using Image pal = Image.Load(image.ToBmp(new BmpEncoderOptions { BitsPerPixel = 1 }));
        await Assert.That(pal[0, 0]).IsEqualTo(new Rgba32(1, 2, 3, 255));
    }

    [Test]
    public void ForcedPalette_TooManyColors_Throws()
    {
        using Image image = WithColors(17);
        Assert.ThrowsExactly<InvalidOperationException>(() => image.ToBmp(new BmpEncoderOptions { BitsPerPixel = 4 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.ToBmp(new BmpEncoderOptions { BitsPerPixel = 16 }));
    }

    [Test]
    public async Task Metadata_IccAndResolution_RoundTrip()
    {
        using Image image = WithColors(300);
        image.Metadata.HorizontalResolution = 300;
        image.Metadata.VerticalResolution = 72;
        image.Metadata.IccProfile = [9, 8, 7, 6, 5];
        byte[] bmp = image.ToBmp();
        await Assert.That(Header(bmp)).IsEqualTo((124, 24));
        using Image back = Image.Load(bmp);
        await Assert.That(back.Metadata.IccProfile).IsEquivalentTo(image.Metadata.IccProfile, CollectionOrdering.Matching);
        await Assert.That(back.Metadata.HorizontalResolution).IsEqualTo(300f).Within(0.05f);
        await Assert.That(back.Metadata.VerticalResolution).IsEqualTo(72f).Within(0.05f);
        await Assert.That(back.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);

        byte[] stripped = image.ToBmp(new BmpEncoderOptions { Metadata = MetadataKinds.None });
        await Assert.That(Header(stripped).HeaderSize).IsEqualTo(40);
    }

    [Test]
    [MethodDataSource(typeof(BmpDecoderTests), nameof(BmpDecoderTests.BmpFiles))]
    public async Task Corpus_RoundTrip_IsLossless(string file)
    {
        using Image image = Image.Load(Corpus.PathOf(file));
        using Image back = Image.Load(image.ToBmp());
        await Assert.That(back.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Property("Category", "Oracle")]
    [Test]
    public async Task PillowAndImageMagick_DecodeOurBmpsIdentically()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"picturesharp-bmp-{Guid.NewGuid():N}");
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

            using (Image photo = Image.Load(Corpus.PathOf("png/rgb8.png")))
            {
                Add("rgb24.bmp", photo, new BmpEncoderOptions());
            }

            using (Image alpha = Image.Load(Corpus.PathOf("png/odd_rgba.png")))
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
                Skip.Unless(ReferenceTools.HasPillow, "Pillow is not installed.");
                Assert.Fail(json);
            }

            var actual = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json[..(json.LastIndexOf('}') + 1)])!;
            bool imageMagick = ReferenceTools.Run("convert", "-version", out _) == 0;
            foreach ((string name, string hash) in expected)
            {
                await Assert.That(hash == actual[name]["pillow"]).IsTrue().Because($"{name}: Pillow decodes differently");
                await Assert.That(!imageMagick || hash == actual[name]["imagemagick"]).IsTrue().Because($"{name}: ImageMagick decodes differently");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
