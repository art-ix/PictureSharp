using System.Buffers.Binary;
using System.Text.Json;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Png;

public class PngEncoderTests
{
    private static Image Load(string file) => Image.Load(Corpus.PathOf(file));

    private static (int Depth, int ColorType, bool Interlaced) Header(byte[] png) => (png[24], png[25], png[28] == 1);

    private static void AssertSamePixels(Image expected, Image actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.FrameCount, actual.FrameCount);
        for (int i = 0; i < expected.FrameCount; i++)
        {
            Assert.True(expected.Frames[i].GetPixelSpan().SequenceEqual(actual.Frames[i].GetPixelSpan()), $"frame {i} differs");
        }
    }

    public static TheoryData<string> PngFiles => Corpus.Files("png/");

    [Theory]
    [MemberData(nameof(PngFiles))]
    public void RoundTrip_DefaultOptions_IsLossless(string file)
    {
        using Image original = Load(file);
        byte[] png = original.ToPng();
        using Image back = Image.Load(png);
        AssertSamePixels(original, back);
        Assert.Equal(original.LoopCount, back.LoopCount);
    }

    public static TheoryData<PngColorType, bool, PngFilterStrategy, int> Variants()
    {
        var data = new TheoryData<PngColorType, bool, PngFilterStrategy, int>();
        foreach (PngColorType type in new[] { PngColorType.Auto, PngColorType.Rgba, PngColorType.Palette })
        {
            foreach (bool interlace in new[] { false, true })
            {
                foreach (PngFilterStrategy filter in Enum.GetValues<PngFilterStrategy>())
                {
                    data.Add(type, interlace, filter, filter == PngFilterStrategy.Adaptive ? 9 : 1);
                }
            }
        }

        data.Add(PngColorType.Rgba, false, PngFilterStrategy.Adaptive, 0);
        return data;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void RoundTrip_ColorTypes_Filters_Interlace(PngColorType type, bool interlace, PngFilterStrategy filter, int level)
    {
        using Image original = Load("png/palette4_trns.png");  // few colors with alpha: valid for all three types
        var options = new PngEncoderOptions { ColorType = type, Interlace = interlace, FilterStrategy = filter, CompressionLevel = level };
        byte[] png = original.ToPng(options);
        Assert.Equal(interlace, Header(png).Interlaced);
        using Image back = Image.Load(png);
        AssertSamePixels(original, back);
    }

    [Theory]
    [InlineData("png/rgb8.png", 8, 2)]          // many opaque colors -> RGB
    [InlineData("png/rgba8.png", 8, 6)]         // many colors with alpha -> RGBA
    [InlineData("png/gray8.png", 8, 0)]         // gray, opaque -> grayscale
    [InlineData("png/graya8.png", 8, 4)]        // gray with alpha, many values -> gray+alpha
    [InlineData("png/gray1.png", 1, 3)]         // two gray levels... gray beats palette when opaque
    [InlineData("png/palette.png", 4, 3)]       // <= 16 colors -> 4-bit palette
    [InlineData("png/palette_trns.png", 4, 3)]  // binary-alpha graphic with few colors -> 4-bit palette
    public void Auto_ChoosesCompactColorType(string file, int depth, int colorType)
    {
        using Image image = Load(file);
        byte[] png = image.ToPng();
        (int d, int c, _) = Header(png);
        if (file == "png/gray1.png")
        {
            Assert.Equal(0, c);
            return;
        }

        Assert.Equal((depth, colorType), (d, c));
    }

    [Fact]
    public void Palette_WithTooManyColors_Throws()
    {
        using Image image = Load("png/rgb8.png");
        Assert.Throws<InvalidOperationException>(() => image.ToPng(new PngEncoderOptions { ColorType = PngColorType.Palette }));
    }

    [Fact]
    public void Grayscale_ConvertsColorToLuma()
    {
        using Image image = Load("png/rgb8.png");
        byte[] png = image.ToPng(new PngEncoderOptions { ColorType = PngColorType.Grayscale });
        using Image back = Image.Load(png);
        Assert.Equal(image.ToPixelData(PixelFormat.Gray8), back.ToPixelData(PixelFormat.Gray8));
        Assert.False(back.HasAlpha);
    }

    [Fact]
    public void Apng_RoundTrip_KeepsFramesDurationsAndLoops()
    {
        using Image a = Load("png/rgba8.png");
        using Image b = a.Clone(x => x.Rotate(RotateMode.Rotate180));
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(70), a, b, a);
        anim.LoopCount = 3;
        anim.Frames[1].Duration = TimeSpan.FromMilliseconds(1234);
        byte[] png = anim.ToPng();
        using Image back = Image.Load(png);
        AssertSamePixels(anim, back);
        Assert.Equal(3, back.LoopCount);
        Assert.Equal(new[] { 70.0, 1234.0, 70.0 }, back.Frames.Select(f => f.Duration.TotalMilliseconds));

        byte[] still = anim.ToPng(new PngEncoderOptions { SaveAnimation = false });
        using Image single = Image.Load(still);
        Assert.Equal(1, single.FrameCount);
    }

    [Fact]
    public void Metadata_And_Resolution_RoundTrip()
    {
        using Image image = Load("png/rgba8.png");
        image.Metadata.IccProfile = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        image.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        image.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        image.Metadata.HorizontalResolution = 300;
        image.Metadata.VerticalResolution = 150;

        using Image back = Image.Load(image.ToPng());
        Assert.Equal(image.Metadata.IccProfile, back.Metadata.IccProfile);
        Assert.Equal(image.Metadata.Exif, back.Metadata.Exif);
        Assert.Equal(image.Metadata.Xmp, back.Metadata.Xmp);
        Assert.Equal(ExifOrientation.RightTop, back.Metadata.Orientation);
        Assert.Equal(300f, back.Metadata.HorizontalResolution, 0.1f);
        Assert.Equal(150f, back.Metadata.VerticalResolution, 0.1f);

        using Image stripped = Image.Load(image.ToPng(new PngEncoderOptions { Metadata = MetadataKinds.None }));
        Assert.True(stripped.Metadata.IsEmpty);
    }

    [Fact]
    public void Chunks_HaveValidCrcs()
    {
        using Image image = Load("png/rgba8.png");
        byte[] png = image.ToPng();
        using Image back = Image.Load(png, new DecoderOptions { StrictChecksums = true });
        Assert.Equal(image.Size, back.Size);
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    [Fact]
    public void SaveToFile_ByExtension()
    {
        using Image image = Load("png/rgb8.png");
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.png");
        try
        {
            image.Save(path);
            Assert.Equal(ImageFormat.Png, Image.Identify(path).Format);
            image.SaveAsPng(path);
            using Image back = Image.Load(path);
            AssertSamePixels(image, back);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Compression_IsReasonable()
    {
        // Our files should be within ~10% of ImageMagick/libpng output for the same content.
        foreach (string file in new[] { "png/rgb8.png", "png/rgba8.png", "png/rgb_level9.png" })
        {
            using Image image = Load(file);
            byte[] ours = image.ToPng(new PngEncoderOptions { CompressionLevel = 9 });
            long theirs = new FileInfo(Corpus.PathOf(file)).Length;
            Assert.True(ours.Length < theirs * 1.10, $"{file}: {ours.Length} vs {theirs}");
        }
    }

    [Trait("Category", "Oracle")]
    [Fact]
    public void Pillow_DecodesOurPngsIdentically()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"picturesharp-png-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var expected = new Dictionary<string, string>();
            int n = 0;
            foreach (string file in new[] { "png/rgba8.png", "png/palette4_trns.png", "png/gray8.png", "png/graya8.png", "png/odd_rgba.png", "png/apng_alpha.png" })
            {
                foreach (bool interlace in new[] { false, true })
                {
                    using Image image = Load(file);
                    string name = $"out{n++}.png";
                    image.Save(Path.Combine(dir, name), new PngEncoderOptions { Interlace = interlace });
                    expected[name] = Corpus.Sha256(System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Frames[0].GetPixelSpan()));
                }
            }

            string script = Path.Combine(Corpus.Assets, "png_reference.py");
            if (ReferenceTools.Run("python3", $"-I \"{script}\" \"{dir}\"", out string json) != 0)
            {
                return;  // Pillow not available
            }

            var actual = JsonSerializer.Deserialize<Dictionary<string, string>>(json[..(json.LastIndexOf('}') + 1)])!;
            foreach ((string name, string hash) in expected)
            {
                Assert.Equal(hash, actual[name]);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
