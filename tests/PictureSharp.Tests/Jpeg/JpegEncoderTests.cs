using System.Drawing;
using System.Text.Json;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Jpeg;

public class JpegEncoderTests
{
    public sealed record Reference(string Source, int Quality, int Subsampling, bool Optimize, bool Progressive,
        string Sha256, int Size, string RgbaSha256);

    public static TestCases<string, int, int, bool, bool, string, int, string> References
    {
        get
        {
            var data = new TestCases<string, int, int, bool, bool, string, int, string>();
            var entries = JsonSerializer.Deserialize<Reference[]>(File.ReadAllText(Path.Combine(Corpus.Assets, "jpeg_encoder_reference.json")))!;
            foreach (Reference e in entries)
                data.Add(e.Source, e.Quality, e.Subsampling, e.Optimize, e.Progressive, e.Sha256, e.Size, e.RgbaSha256);
            return data;
        }
    }

    [Test]
    [MethodDataSource(nameof(References))]
    public async Task Encode_IsByteExactWithPillow(string source, int quality, int sampling, bool optimize, bool progressive,
        string expected, int size, string pixels)
    {
        using Image image = Image.Load(Corpus.PathOf(source));
        image.Metadata.HorizontalResolution = 96;
        image.Metadata.VerticalResolution = 96;
        byte[] encoded = image.ToJpeg(new JpegEncoderOptions
        {
            Quality = quality,
            Subsampling = (JpegSubsampling)sampling,
            OptimizeCoding = optimize,
            Progressive = progressive,
            Metadata = MetadataKinds.None,
        });
        await Assert.That(encoded.Length).IsEqualTo(size);
        await Assert.That(Corpus.Sha256(encoded)).IsEqualTo(expected);
        using Image decoded = Image.Load(encoded.AsMemory());
        await Assert.That(Corpus.Sha256(decoded.ToPixelData(PixelFormat.Rgba32))).IsEqualTo(pixels);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Metadata_Resolution_AndFirstFrame_RoundTrip(bool progressive)
    {
        using var image = new Image(19, 13, Color.Red);
        using var frame = new Image(19, 13, Color.Blue);
        image.Frames.Add(frame, TimeSpan.FromMilliseconds(100));
        image.Metadata.HorizontalResolution = 300;
        image.Metadata.VerticalResolution = 150;
        image.Metadata.IccProfile = Enumerable.Range(0, 140000).Select(i => (byte)i).ToArray();
        image.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        image.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        using Image decoded = Image.Load(image.ToJpeg(new JpegEncoderOptions { Progressive = progressive }));
        await Assert.That(decoded.FrameCount).IsEqualTo(1);
        await Assert.That(decoded[0, 0].R > 250 && decoded[0, 0].B < 5).IsTrue();
        await Assert.That(decoded.Metadata.HorizontalResolution).IsEqualTo(300);
        await Assert.That(decoded.Metadata.VerticalResolution).IsEqualTo(150);
        await Assert.That(decoded.Metadata.IccProfile).IsEquivalentTo(image.Metadata.IccProfile, CollectionOrdering.Matching);
        await Assert.That(decoded.Metadata.Exif).IsEquivalentTo(image.Metadata.Exif, CollectionOrdering.Matching);
        await Assert.That(decoded.Metadata.Xmp).IsEquivalentTo(image.Metadata.Xmp, CollectionOrdering.Matching);
        await Assert.That(decoded.Metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        foreach (MetadataKinds kind in new[] { MetadataKinds.None, MetadataKinds.Icc, MetadataKinds.Exif, MetadataKinds.Xmp })
        {
            using Image selected = Image.Load(image.ToJpeg(new JpegEncoderOptions { Metadata = kind }));
            await Sequence.AssertEqual(kind == MetadataKinds.Icc ? image.Metadata.IccProfile : null, selected.Metadata.IccProfile);
            await Sequence.AssertEqual(kind == MetadataKinds.Exif ? image.Metadata.Exif : null, selected.Metadata.Exif);
            await Sequence.AssertEqual(kind == MetadataKinds.Xmp ? image.Metadata.Xmp : null, selected.Metadata.Xmp);
        }
    }

    [Test]
    public async Task Alpha_IsCompositedOnTheSelectedBackground_WithoutChangingSource()
    {
        using var image = new Image(17, 9);
        var random = new Random(55);
        foreach (ref Rgba32 p in image.GetPixelSpan())
            p = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        byte[] original = image.ToPixelData(PixelFormat.Rgba32);
        Color background = Color.FromArgb(17, 93, 201);
        using var flat = new Image(image.Width, image.Height);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 p = image[x, y];
                flat[x, y] = new Rgba32((byte)((p.R * p.A + background.R * (255 - p.A) + 127) / 255),
                    (byte)((p.G * p.A + background.G * (255 - p.A) + 127) / 255),
                    (byte)((p.B * p.A + background.B * (255 - p.A) + 127) / 255), 255);
            }
        await Assert.That(image.ToJpeg(new JpegEncoderOptions { BackgroundColor = background })).IsEquivalentTo(flat.ToJpeg(), CollectionOrdering.Matching);
        await Assert.That(image.ToPixelData(PixelFormat.Rgba32)).IsEquivalentTo(original, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SaveApis_UseJpeg_AndLeaveStreamsOpen()
    {
        using var image = new Image(3, 5, Color.Green);
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-jpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string extension in new[] { ".jpg", ".jpeg", ".JPG", ".jpe", ".jfif" })
            {
                string path = Path.Combine(directory, "image" + extension);
                image.Save(path);
                await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(image.ToJpeg(), CollectionOrdering.Matching);
            }
            string explicitPath = Path.Combine(directory, "explicit.jpg");
            image.SaveAsJpeg(explicitPath, new() { Quality = 90 });
            await Assert.That(File.ReadAllBytes(explicitPath)).IsEquivalentTo(image.ToJpeg(new JpegEncoderOptions { Quality = 90 }), CollectionOrdering.Matching);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Jpeg);
            await Assert.That(stream.CanWrite).IsTrue();
            await Assert.That(stream.ToArray()).IsEquivalentTo(image.ToJpeg(), CollectionOrdering.Matching);
            stream.SetLength(0);
            await image.SaveAsync(stream, new JpegEncoderOptions { Progressive = true });
            await Assert.That(stream.ToArray()).IsEquivalentTo(image.ToJpeg(new JpegEncoderOptions { Progressive = true }), CollectionOrdering.Matching);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task InvalidOptions_ThrowBeforeWriting()
    {
        using var image = new Image(1, 1);
        var random = new Random(5500);
        var options = new List<Func<JpegEncoderOptions>>
        {
            () => new() { Quality = 0 }, () => new() { Quality = 101 },
            () => new() { RestartInterval = -1 }, () => new() { RestartInterval = 65536 },
            () => new() { Subsampling = (JpegSubsampling)(-1) }, () => new() { Subsampling = (JpegSubsampling)4 },
            () => new() { ColorMode = (JpegColorMode)(-1) }, () => new() { ColorMode = (JpegColorMode)3 },
            () => new() { BackgroundColor = Color.Transparent }, () => new() { Metadata = (MetadataKinds)8 },
        };
        for (int i = 0; i < 100; i++)
        {
            int value = random.Next(101, int.MaxValue);
            options.Add(() => new JpegEncoderOptions { Quality = value });
            options.Add(() => new JpegEncoderOptions { Subsampling = (JpegSubsampling)value });
            options.Add(() => new JpegEncoderOptions { RestartInterval = -value });
        }
        foreach (Func<JpegEncoderOptions> create in options)
        {
            using var stream = new MemoryStream();
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Save(stream, create()));
            await Assert.That(stream.Length).IsEqualTo(0);
        }
    }

    [Test]
    public async Task OversizedMetadata_IsRejectedBeforeWriting_AndCanBeExcluded()
    {
        using var image = new Image(1, 1, Color.White);
        foreach (MetadataKinds kind in new[] { MetadataKinds.Exif, MetadataKinds.Xmp, MetadataKinds.Icc })
        {
            image.Metadata.Clear();
            if (kind == MetadataKinds.Exif) image.Metadata.Exif = new byte[65528];
            if (kind == MetadataKinds.Xmp) image.Metadata.Xmp = new byte[65505];
            if (kind == MetadataKinds.Icc) image.Metadata.IccProfile = new byte[65519 * 255 + 1];
            using var stream = new MemoryStream();
            Assert.ThrowsExactly<ArgumentException>(() => image.Save(stream, new JpegEncoderOptions()));
            await Assert.That(stream.Length).IsEqualTo(0);
            await Assert.That(image.ToJpeg(new JpegEncoderOptions { Metadata = MetadataKinds.None })).IsNotEmpty();
        }
    }

    [Test]
    public async Task JfifDensity_IsRoundedAndClamped()
    {
        using var image = new Image(1, 1);
        image.Metadata.HorizontalResolution = float.MaxValue;
        image.Metadata.VerticalResolution = 0.25f;
        using Image decoded = Image.Load(image.ToJpeg());
        await Assert.That(decoded.Metadata.HorizontalResolution).IsEqualTo(65535);
        await Assert.That(decoded.Metadata.VerticalResolution).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ColorMode_Color_KeepsThreeComponentsForGrey_AndDecodesLikeOne(bool progressive)
    {
        using Image gray = Gradient(37, 21, gray: true);
        byte[] auto = gray.ToJpeg(new JpegEncoderOptions { Progressive = progressive });
        byte[] color = gray.ToJpeg(new JpegEncoderOptions { Progressive = progressive, ColorMode = JpegColorMode.Color });
        await Assert.That((Components(auto), Components(color))).IsEqualTo((1, 3));
        // Grey pixels have Cb = Cr = 128, whose coefficients quantize to zero, so the pixels are the same.
        using Image fromAuto = Image.Load(auto.AsMemory());
        using Image fromColor = Image.Load(color.AsMemory());
        await Assert.That(Corpus.Sha256(fromColor.ToPixelData(PixelFormat.Rgba32))).IsEqualTo(Corpus.Sha256(fromAuto.ToPixelData(PixelFormat.Rgba32)));
    }

    [Test]
    public async Task ColorMode_Grayscale_WritesTheLumaOfColourPixels()
    {
        using Image source = Gradient(37, 21, gray: false);
        using Image luma = source.Clone();
        Span<Rgba32> pixels = luma.GetPixelSpan();
        foreach (ref Rgba32 p in pixels)
        {
            byte y = (byte)((19595 * p.R + 38470 * p.G + 7471 * p.B + 32768) >> 16);
            p = new Rgba32(y, y, y, 255);
        }

        byte[] grayscale = source.ToJpeg(new JpegEncoderOptions { ColorMode = JpegColorMode.Grayscale });
        await Assert.That(Components(grayscale)).IsEqualTo(1);
        await Assert.That(grayscale.SequenceEqual(luma.ToJpeg())).IsTrue();
    }

    [Test]
    [Arguments(true, JpegColorMode.Grayscale)]
    [Arguments(false, JpegColorMode.Color)]
    public async Task ColorMode_MatchingAuto_GivesTheSameBytes(bool gray, JpegColorMode mode)
    {
        using Image image = Gradient(19, 9, gray);
        await Assert.That(image.ToJpeg(new JpegEncoderOptions { ColorMode = mode }).SequenceEqual(image.ToJpeg())).IsTrue();
    }

    [Test]
    [Arguments(JpegGainMapMode.Preserve)]
    [Arguments(JpegGainMapMode.Require)]
    public async Task ColorMode_GrayscaleWithAGainMap_Throws(JpegGainMapMode mode)
    {
        using var image = new Image(4, 4);
        using var stream = new MemoryStream();
        Assert.ThrowsExactly<ArgumentException>(() => image.Save(stream, new JpegEncoderOptions { ColorMode = JpegColorMode.Grayscale, GainMap = mode }));
        await Assert.That(stream.Length).IsEqualTo(0);
    }

    private static Image Gradient(int width, int height, bool gray)
    {
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)(x * 7 + y * 3), g = (byte)(x * 2 + y * 11), b = (byte)(x * x + y);
                pixels[y * width + x] = gray ? new Rgba32(r, r, r, 255) : new Rgba32(r, g, b, 255);
            }
        return image;
    }

    /// <summary>The component count of the first SOF segment.</summary>
    private static int Components(byte[] jpeg)
    {
        for (int i = 2; i + 9 < jpeg.Length; i += 2 + (jpeg[i + 2] << 8 | jpeg[i + 3]))
            if (jpeg[i + 1] is 0xC0 or 0xC1 or 0xC2) return jpeg[i + 9];
        throw new InvalidOperationException("No SOF segment.");
    }
}
