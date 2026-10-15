using System.Text.Json;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Jpeg;

public class JpegScaledDecodeTests
{
    private static readonly Lazy<JsonDocument> ScaledReference =
        new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Assets, "jpeg_scaled_reference.json"))));

    public static TheoryData<string, int> ScaledCases()
    {
        var data = new TheoryData<string, int>();
        foreach (JsonProperty file in ScaledReference.Value.RootElement.GetProperty("files").EnumerateObject())
        {
            foreach (int scale in (int[])[2, 4, 8])
            {
                data.Add(file.Name, scale);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScaledCases))]
    public void ScaledDecode_IsBitExactWithLibjpegTurbo(string file, int scale)
    {
        JsonElement reference = ScaledReference.Value.RootElement.GetProperty("files").GetProperty(file).GetProperty(scale.ToString());
        using Image image = JpegDecoder.Decode(Corpus.Read(file), DecoderOptions.Default, scale);
        Assert.Equal((reference.GetProperty("width").GetInt32(), reference.GetProperty("height").GetInt32()), (image.Width, image.Height));
        if (Environment.GetEnvironmentVariable("PICTURESHARP_DUMP_SCALED") is { } dump)
        {
            File.WriteAllBytes(Path.Combine(dump, $"{Path.GetFileName(file)}.{scale}.ours"), image.ToPixelData());
        }

        Assert.Equal(reference.GetProperty("sha256").GetString(), Corpus.Sha256(image.ToPixelData()));
    }

    public static TheoryData<int, bool, int, int> OrientationCases()
    {
        var data = new TheoryData<int, bool, int, int>();
        for (int orientation = 1; orientation <= 8; orientation++)
        {
            foreach (bool apply in (bool[])[false, true])
            {
                foreach ((int w, int h) in ((int, int)[])[(48, 48), (100, 0), (0, 30), (31, 17), (300, 300)])
                {
                    data.Add(orientation, apply, w, h);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OrientationCases))]
    public void TargetSize_GivesTheThumbnailSize_InTheReturnedOrientation(int orientation, bool apply, int width, int height)
    {
        string file = Corpus.PathOf($"jpeg/orientation_{orientation}.jpg");
        var options = new DecoderOptions { ApplyExifOrientation = apply };
        using Image expected = Image.Load(file, options);
        expected.Mutate(x => x.Thumbnail(width, height));
        using Image image = Image.Load(file, options with { TargetSize = new(width, height) });

        Assert.Equal(expected.Size, image.Size);
        Assert.Equal(expected.Metadata.Orientation, image.Metadata.Orientation);
        Assert.True(Psnr(expected, image) > 30, $"PSNR {Psnr(expected, image):F1} dB");
    }

    [Theory]
    [InlineData("jpeg/q90_420.jpg")]
    [InlineData("jpeg/q90_422.jpg")]
    [InlineData("jpeg/q90_440.jpg")]
    [InlineData("jpeg/q90_411.jpg")]
    [InlineData("jpeg/progressive_420.jpg")]
    [InlineData("jpeg/gray_progressive.jpg")]
    [InlineData("jpeg/cmyk.jpg")]
    [InlineData("jpeg/crafted_ycck.jpg")]
    [InlineData("jpeg/odd_420.jpg")]
    [InlineData("jpeg/odd_progressive.jpg")]
    public void TargetSize_IsCloseToFullDecodeAndThumbnail(string file)
    {
        // A reduced decode is a different algorithm: with no gap, 1/8 scale replicates the chroma of 4:1:1 four times.
        foreach ((double gap, double minPsnr) in ((double, double)[])[(2, 30), (1, 25)])
        {
            foreach (int side in (int[])[8, 20, 33, 64])
            {
                using Image expected = Image.Load(Corpus.PathOf(file));
                expected.Mutate(x => x.Thumbnail(side, side));
                using Image image = Image.Load(Corpus.PathOf(file), new DecoderOptions { TargetSize = new(side, side), TargetReducingGap = gap });
                Assert.Equal(expected.Size, image.Size);
                Assert.Null(image.ReducedDecode);
                double psnr = Psnr(expected, image);
                Assert.True(psnr > minPsnr, $"{side}, gap {gap}: PSNR {psnr:F1} dB");
            }
        }
    }

    [Fact]
    public void TargetSize_WithoutReduction_EqualsLoadAndThumbnail()
    {
        // 256x192 to 200x150 leaves no room for a 1/2 decode, so the result is the full decode resized.
        string file = Corpus.PathOf("jpeg/q90_420.jpg");
        using Image expected = Image.Load(file);
        expected.Mutate(x => x.Thumbnail(200, 200, ResamplingFilter.Bicubic));
        using Image image = Image.Load(file, new DecoderOptions { TargetSize = new(200, 200), TargetResampling = ResamplingFilter.Bicubic });
        Assert.Equal(expected.ToPixelData(), image.ToPixelData());
    }

    [Theory]
    [InlineData(32, 0, 2.0, 4)]
    [InlineData(32, 0, 1.0, 8)]
    [InlineData(16, 0, 2.0, 8)]
    [InlineData(0, 48, 2.0, 2)]
    [InlineData(0, 48, 1.5, 2)]
    [InlineData(0, 48, 1.0, 4)]
    [InlineData(100, 100, 1.0, 2)]
    [InlineData(200, 200, 1.0, 1)]
    [InlineData(500, 500, 1.0, 1)]
    public void SelectScale_KeepsTheReducingGap(int width, int height, double gap, int expected)
    {
        JpegFile file = JpegFile.Read(Corpus.Read("jpeg/q90_420.jpg"), DecoderOptions.Default, headerOnly: true);
        var options = new DecoderOptions { TargetSize = new(width, height), TargetReducingGap = gap };
        Assert.Equal(expected, JpegDecoder.SelectScale(file.Frame!, file.Exif, options));
    }

    [Fact]
    public void SelectScale_SwapsTheBoxForRotatedOrientations()
    {
        // 256x192 stored with orientation 6 is 192x256 once oriented: 32 px wide is 32x43, i.e. 43x32 stored.
        JpegFile file = JpegFile.Read(Corpus.Read("jpeg/orientation_6.jpg"), DecoderOptions.Default, headerOnly: true);
        var options = new DecoderOptions { TargetSize = new(32, 0), TargetReducingGap = 1 };
        Assert.Equal(4, JpegDecoder.SelectScale(file.Frame!, file.Exif, options with { ApplyExifOrientation = true }));
        Assert.Equal(8, JpegDecoder.SelectScale(file.Frame!, file.Exif, options));
    }

    [Fact]
    public void TargetSize_DoesNotBypassTheSourceLimits()
    {
        var options = new DecoderOptions { TargetSize = new(8, 8), MaxPixels = (256 * 192) - 1 };
        Assert.Throws<ImageFormatException>(() => Image.Load(Corpus.PathOf("jpeg/q90_420.jpg"), options));
        Assert.Throws<ImageFormatException>(() => Image.Load(Corpus.PathOf("jpeg/progressive_420.jpg"), options));
        using Image image = Image.Load(Corpus.PathOf("jpeg/q90_420.jpg"), options with { MaxPixels = 256 * 192 });
        Assert.Equal((8, 6), (image.Width, image.Height));
    }

    [Theory]
    [InlineData("png/odd_rgba.png")]
    [InlineData("gif/pil_anim_dispose2.gif")]
    [InlineData("webp/lossy/graphic_alpha_q75.webp")]
    public void TargetSize_ForOtherFormats_IsFullDecodeAndThumbnail(string file)
    {
        using Image expected = Image.Load(Corpus.PathOf(file));
        expected.Mutate(x => x.Thumbnail(20, 20));
        using Image image = Image.Load(Corpus.PathOf(file), new DecoderOptions { TargetSize = new(20, 20) });
        Assert.Equal(expected.FrameCount, image.FrameCount);
        for (int i = 0; i < image.FrameCount; i++)
        {
            Assert.Equal(expected.Frames[i].Size, image.Frames[i].Size);
            Assert.Equal(expected.Frames[i].Pixels, image.Frames[i].Pixels);
        }
    }

    [Fact]
    public void TargetSize_LargerThanTheImage_KeepsIt()
    {
        string file = Corpus.PathOf("jpeg/progressive_444.jpg");
        using Image expected = Image.Load(file);
        using Image image = Image.Load(file, new DecoderOptions { TargetSize = new(1000, 0), TargetReducingGap = 1 });
        Assert.Equal(expected.ToPixelData(), image.ToPixelData());
    }

    [Fact]
    public void TargetOptions_AreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetSize = new(0, 0) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetSize = new(-1, 10) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetReducingGap = 0.99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetReducingGap = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetResampling = (ResamplingFilter)99 });
        Assert.Equal(new System.Drawing.Size(10, 0), new DecoderOptions { TargetSize = new(10, 0) }.TargetSize);
    }

    [Fact]
    public void ScaledDecode_OfTruncatedAndCorruptInput_FailsCleanly()
    {
        byte[] original = Corpus.Read("jpeg/progressive_420.jpg");
        var options = new DecoderOptions { TargetSize = new(16, 16), TargetReducingGap = 1 };
        var random = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            byte[] data = original.AsSpan(0, random.Next(2, original.Length)).ToArray();
            for (int j = random.Next(4); j > 0; j--)
            {
                data[random.Next(2, data.Length)] = (byte)random.Next(256);
            }

            try
            {
                using Image image = Image.Load(data, options);
                Assert.True(image.Width <= 16 && image.Height <= 16);
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }
    }

    private static double Psnr(Image expected, Image actual)
    {
        byte[] a = expected.ToPixelData(), b = actual.ToPixelData();
        Assert.Equal(a.Length, b.Length);
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (sum / a.Length));
    }
}
