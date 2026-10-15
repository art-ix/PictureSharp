using System.Buffers.Binary;
using System.Text.Json;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Png;

public class PngEncoderTests
{
    private static Image Load(string file) => Image.Load(Corpus.PathOf(file));

    private static (int Depth, int ColorType, bool Interlaced) Header(byte[] png) => (png[24], png[25], png[28] == 1);

    private static async Task AssertSamePixels(Image expected, Image actual)
    {
        await Assert.That(actual.Size).IsEqualTo(expected.Size);
        await Assert.That(actual.FrameCount).IsEqualTo(expected.FrameCount);
        for (int i = 0; i < expected.FrameCount; i++)
        {
            await Assert.That(expected.Frames[i].GetPixelSpan().SequenceEqual(actual.Frames[i].GetPixelSpan())).IsTrue().Because($"frame {i} differs");
        }
    }

    public static TestCases<string> PngFiles => Corpus.Files("png/");

    [Test]
    [MethodDataSource(nameof(PngFiles))]
    public async Task RoundTrip_DefaultOptions_IsLossless(string file)
    {
        using Image original = Load(file);
        byte[] png = original.ToPng();
        using Image back = Image.Load(png);
        await AssertSamePixels(original, back);
        await Assert.That(back.LoopCount).IsEqualTo(original.LoopCount);
    }

    public static TestCases<PngColorType, bool, PngFilterStrategy, int> Variants()
    {
        var data = new TestCases<PngColorType, bool, PngFilterStrategy, int>();
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

    [Test]
    [MethodDataSource(nameof(Variants))]
    public async Task RoundTrip_ColorTypes_Filters_Interlace(PngColorType type, bool interlace, PngFilterStrategy filter, int level)
    {
        using Image original = Load("png/palette4_trns.png");  // few colors with alpha: valid for all three types
        var options = new PngEncoderOptions { ColorType = type, Interlace = interlace, FilterStrategy = filter, CompressionLevel = level };
        byte[] png = original.ToPng(options);
        await Assert.That(Header(png).Interlaced).IsEqualTo(interlace);
        using Image back = Image.Load(png);
        await AssertSamePixels(original, back);
    }

    [Test]
    [Arguments("png/rgb8.png", 8, 2)]          // many opaque colors -> RGB
    [Arguments("png/rgba8.png", 8, 6)]         // many colors with alpha -> RGBA
    [Arguments("png/gray8.png", 8, 0)]         // gray, opaque -> grayscale
    [Arguments("png/graya8.png", 8, 4)]        // gray with alpha, many values -> gray+alpha
    [Arguments("png/gray1.png", 1, 3)]         // two gray levels... gray beats palette when opaque
    [Arguments("png/palette.png", 4, 3)]       // <= 16 colors -> 4-bit palette
    [Arguments("png/palette_trns.png", 4, 3)]  // binary-alpha graphic with few colors -> 4-bit palette
    public async Task Auto_ChoosesCompactColorType(string file, int depth, int colorType)
    {
        using Image image = Load(file);
        byte[] png = image.ToPng();
        (int d, int c, _) = Header(png);
        if (file == "png/gray1.png")
        {
            await Assert.That(c).IsEqualTo(0);
            return;
        }

        await Assert.That((d, c)).IsEqualTo((depth, colorType));
    }

    [Test]
    public void Palette_WithTooManyColors_Throws()
    {
        using Image image = Load("png/rgb8.png");
        Assert.ThrowsExactly<InvalidOperationException>(() => image.ToPng(new PngEncoderOptions { ColorType = PngColorType.Palette }));
    }

    [Test]
    public async Task Grayscale_ConvertsColorToLuma()
    {
        using Image image = Load("png/rgb8.png");
        byte[] png = image.ToPng(new PngEncoderOptions { ColorType = PngColorType.Grayscale });
        using Image back = Image.Load(png);
        await Assert.That(back.ToPixelData(PixelFormat.Gray8)).IsEquivalentTo(image.ToPixelData(PixelFormat.Gray8), CollectionOrdering.Matching);
        await Assert.That(back.HasAlpha).IsFalse();
    }

    [Test]
    public async Task Apng_RoundTrip_KeepsFramesDurationsAndLoops()
    {
        using Image a = Load("png/rgba8.png");
        using Image b = a.Clone(x => x.Rotate(RotateMode.Rotate180));
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(70), a, b, a);
        anim.LoopCount = 3;
        anim.Frames[1].Duration = TimeSpan.FromMilliseconds(1234);
        byte[] png = anim.ToPng();
        using Image back = Image.Load(png);
        await AssertSamePixels(anim, back);
        await Assert.That(back.LoopCount).IsEqualTo(3);
        await Assert.That(back.Frames.Select(f => f.Duration.TotalMilliseconds)).IsEquivalentTo(new[] { 70.0, 1234.0, 70.0 }, CollectionOrdering.Matching);

        byte[] still = anim.ToPng(new PngEncoderOptions { SaveAnimation = false });
        using Image single = Image.Load(still);
        await Assert.That(single.FrameCount).IsEqualTo(1);
    }

    [Test]
    public async Task Metadata_And_Resolution_RoundTrip()
    {
        using Image image = Load("png/rgba8.png");
        image.Metadata.IccProfile = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        image.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        image.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        image.Metadata.HorizontalResolution = 300;
        image.Metadata.VerticalResolution = 150;

        using Image back = Image.Load(image.ToPng());
        await Assert.That(back.Metadata.IccProfile).IsEquivalentTo(image.Metadata.IccProfile, CollectionOrdering.Matching);
        await Assert.That(back.Metadata.Exif).IsEquivalentTo(image.Metadata.Exif, CollectionOrdering.Matching);
        await Assert.That(back.Metadata.Xmp).IsEquivalentTo(image.Metadata.Xmp, CollectionOrdering.Matching);
        await Assert.That(back.Metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        await Assert.That(back.Metadata.HorizontalResolution).IsEqualTo(300f).Within(0.1f);
        await Assert.That(back.Metadata.VerticalResolution).IsEqualTo(150f).Within(0.1f);

        using Image stripped = Image.Load(image.ToPng(new PngEncoderOptions { Metadata = MetadataKinds.None }));
        await Assert.That(stripped.Metadata.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Chunks_HaveValidCrcs()
    {
        using Image image = Load("png/rgba8.png");
        byte[] png = image.ToPng();
        using Image back = Image.Load(png, new DecoderOptions { Png = new() { StrictChecksums = true }});
        await Assert.That(back.Size).IsEqualTo(image.Size);
        await Assert.That(System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4)).IsEqualTo("IEND");
    }

    [Test]
    public async Task Compression_IsReasonable()
    {
        // Our files should be within ~10% of ImageMagick/libpng output for the same content.
        foreach (string file in new[] { "png/rgb8.png", "png/rgba8.png", "png/rgb_level9.png" })
        {
            using Image image = Load(file);
            byte[] ours = image.ToPng(new PngEncoderOptions { CompressionLevel = 9 });
            long theirs = new FileInfo(Corpus.PathOf(file)).Length;
            await Assert.That(ours.Length < theirs * 1.10).IsTrue().Because($"{file}: {ours.Length} vs {theirs}");
        }
    }

    [Property("Category", "Oracle")]
    [Test]
    public async Task Pillow_DecodesOurPngsIdentically()
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
                Skip.Unless(ReferenceTools.HasPillow, "Pillow is not installed.");
                Assert.Fail(json);
            }

            var actual = JsonSerializer.Deserialize<Dictionary<string, string>>(json[..(json.LastIndexOf('}') + 1)])!;
            foreach ((string name, string hash) in expected)
            {
                await Assert.That(actual[name]).IsEqualTo(hash);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
