using System.Drawing;
using System.Text.Json;
using WebpSharp.Tests.Infrastructure;

namespace WebpSharp.Tests.Jpeg;

public class JpegEncoderTests
{
    public sealed record Reference(string Source, int Quality, int Subsampling, bool Optimize, bool Progressive,
        string Sha256, int Size, string RgbaSha256);

    public static TheoryData<string, int, int, bool, bool, string, int, string> References
    {
        get
        {
            var data = new TheoryData<string, int, int, bool, bool, string, int, string>();
            var entries = JsonSerializer.Deserialize<Reference[]>(File.ReadAllText(Path.Combine(Corpus.Assets, "jpeg_encoder_reference.json")))!;
            foreach (Reference e in entries)
                data.Add(e.Source, e.Quality, e.Subsampling, e.Optimize, e.Progressive, e.Sha256, e.Size, e.RgbaSha256);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(References))]
    public void Encode_IsByteExactWithPillow(string source, int quality, int sampling, bool optimize, bool progressive,
        string expected, int size, string pixels)
    {
        using Image image = Image.FromFile(Corpus.PathOf(source));
        image.SetResolution(96, 96);
        byte[] encoded = image.ToJpeg(new JpegEncoderOptions
        {
            Quality = quality, Subsampling = (JpegSubsampling)sampling, OptimizeCoding = optimize,
            Progressive = progressive, Metadata = MetadataKinds.None,
        });
        Assert.Equal(size, encoded.Length);
        Assert.Equal(expected, Corpus.Sha256(encoded));
        using Image decoded = Image.FromBytes(encoded.AsMemory());
        Assert.Equal(pixels, Corpus.Sha256(decoded.ToPixelArray(PixelFormat.Rgba32)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Metadata_Resolution_AndActiveFrame_RoundTrip(bool progressive)
    {
        using var image = new Image(19, 13, Color.Red);
        using var frame = new Image(19, 13, Color.Blue);
        image.Frames.Add(frame, TimeSpan.FromMilliseconds(100));
        image.SelectActiveFrame(1);
        image.SetResolution(300, 150);
        image.Metadata.IccProfile = Enumerable.Range(0, 140000).Select(i => (byte)i).ToArray();
        image.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        image.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        using Image decoded = Image.FromBytes(image.ToJpeg(new JpegEncoderOptions { Progressive = progressive }));
        Assert.Equal(1, decoded.FrameCount);
        Assert.True(decoded[0, 0].B > 250);
        Assert.Equal(300, decoded.HorizontalResolution);
        Assert.Equal(150, decoded.VerticalResolution);
        Assert.Equal(image.Metadata.IccProfile, decoded.Metadata.IccProfile);
        Assert.Equal(image.Metadata.Exif, decoded.Metadata.Exif);
        Assert.Equal(image.Metadata.Xmp, decoded.Metadata.Xmp);
        Assert.Equal(ExifOrientation.RightTop, decoded.Metadata.Orientation);
        foreach (MetadataKinds kind in new[] { MetadataKinds.None, MetadataKinds.Icc, MetadataKinds.Exif, MetadataKinds.Xmp })
        {
            using Image selected = Image.FromBytes(image.ToJpeg(new JpegEncoderOptions { Metadata = kind }));
            Assert.Equal(kind == MetadataKinds.Icc ? image.Metadata.IccProfile : null, selected.Metadata.IccProfile);
            Assert.Equal(kind == MetadataKinds.Exif ? image.Metadata.Exif : null, selected.Metadata.Exif);
            Assert.Equal(kind == MetadataKinds.Xmp ? image.Metadata.Xmp : null, selected.Metadata.Xmp);
        }
    }

    [Fact]
    public void Alpha_IsCompositedOnTheSelectedBackground_WithoutChangingSource()
    {
        using var image = new Image(17, 9);
        var random = new Random(55);
        foreach (ref Rgba32 p in image.GetPixelSpan())
            p = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        byte[] original = image.ToPixelArray(PixelFormat.Rgba32);
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
        Assert.Equal(flat.ToJpeg(), image.ToJpeg(new JpegEncoderOptions { BackgroundColor = background }));
        Assert.Equal(original, image.ToPixelArray(PixelFormat.Rgba32));
    }

    [Fact]
    public async Task SaveApis_UseJpeg_AndLeaveStreamsOpen()
    {
        using var image = new Image(3, 5, Color.Green);
        string directory = Path.Combine(Path.GetTempPath(), $"webpsharp-jpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string extension in new[] { ".jpg", ".jpeg", ".JPG", ".jpe", ".jfif" })
            {
                string path = Path.Combine(directory, "image" + extension);
                image.Save(path);
                Assert.Equal(image.ToJpeg(), File.ReadAllBytes(path));
            }
            string explicitPath = Path.Combine(directory, "explicit.jpg");
            image.SaveAsJpeg(explicitPath, 90);
            Assert.Equal(image.ToJpeg(new JpegEncoderOptions { Quality = 90 }), File.ReadAllBytes(explicitPath));
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Jpeg);
            Assert.True(stream.CanWrite);
            Assert.Equal(image.ToJpeg(), stream.ToArray());
            stream.SetLength(0);
            await image.SaveAsync(stream, new JpegEncoderOptions { Progressive = true });
            Assert.Equal(image.ToJpeg(new JpegEncoderOptions { Progressive = true }), stream.ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void InvalidOptions_ThrowBeforeWriting()
    {
        using var image = new Image(1, 1);
        var random = new Random(5500);
        var options = new List<JpegEncoderOptions>
        {
            new() { Quality = 0 }, new() { Quality = 101 },
            new() { RestartInterval = -1 }, new() { RestartInterval = 65536 },
            new() { Subsampling = (JpegSubsampling)(-1) }, new() { Subsampling = (JpegSubsampling)4 },
            new() { BackgroundColor = Color.Transparent }, new() { Metadata = (MetadataKinds)8 },
        };
        for (int i = 0; i < 100; i++)
        {
            int value = random.Next(101, int.MaxValue);
            options.Add(new JpegEncoderOptions { Quality = value });
            options.Add(new JpegEncoderOptions { Subsampling = (JpegSubsampling)value });
            options.Add(new JpegEncoderOptions { RestartInterval = -value });
        }
        foreach (JpegEncoderOptions option in options)
        {
            using var stream = new MemoryStream();
            Assert.Throws<ArgumentOutOfRangeException>(() => image.Save(stream, option));
            Assert.Equal(0, stream.Length);
        }
    }

    [Fact]
    public void OversizedMetadata_IsRejectedBeforeWriting_AndCanBeExcluded()
    {
        using var image = new Image(1, 1, Color.White);
        foreach (MetadataKinds kind in new[] { MetadataKinds.Exif, MetadataKinds.Xmp, MetadataKinds.Icc })
        {
            image.Metadata.Clear();
            if (kind == MetadataKinds.Exif) image.Metadata.Exif = new byte[65528];
            if (kind == MetadataKinds.Xmp) image.Metadata.Xmp = new byte[65505];
            if (kind == MetadataKinds.Icc) image.Metadata.IccProfile = new byte[65519 * 255 + 1];
            using var stream = new MemoryStream();
            Assert.Throws<ArgumentException>(() => image.Save(stream, new JpegEncoderOptions()));
            Assert.Equal(0, stream.Length);
            Assert.NotEmpty(image.ToJpeg(new JpegEncoderOptions { Metadata = MetadataKinds.None }));
        }
    }

    [Fact]
    public void JfifDensity_IsRoundedAndClamped()
    {
        using var image = new Image(1, 1);
        image.SetResolution(float.MaxValue, 0.25f);
        using Image decoded = Image.FromBytes(image.ToJpeg());
        Assert.Equal(65535, decoded.HorizontalResolution);
        Assert.Equal(1, decoded.VerticalResolution);
    }
}
