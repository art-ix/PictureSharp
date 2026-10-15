using System.Text.Json;
using PictureSharp.Infrastructure.Codecs.Jpeg;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Jpeg;

public class JpegScaledDecodeTests
{
    private static readonly Lazy<JsonDocument> ScaledReference =
        new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Assets, "jpeg_scaled_reference.json"))));

    public static TestCases<string, int> ScaledCases()
    {
        var data = new TestCases<string, int>();
        foreach (JsonProperty file in ScaledReference.Value.RootElement.GetProperty("files").EnumerateObject())
        {
            foreach (int scale in (int[])[2, 4, 8])
            {
                data.Add(file.Name, scale);
            }
        }

        return data;
    }

    [Test]
    [MethodDataSource(nameof(ScaledCases))]
    public async Task ScaledDecode_IsBitExactWithLibjpegTurbo(string file, int scale)
    {
        JsonElement reference = ScaledReference.Value.RootElement.GetProperty("files").GetProperty(file).GetProperty(scale.ToString());
        using Image image = JpegDecoder.DecodeScaled(Corpus.Read(file), DecoderOptions.Default, scale);
        await Assert.That((image.Width, image.Height)).IsEqualTo((reference.GetProperty("width").GetInt32(), reference.GetProperty("height").GetInt32()));
        if (Environment.GetEnvironmentVariable("PICTURESHARP_DUMP_SCALED") is { } dump)
        {
            File.WriteAllBytes(Path.Combine(dump, $"{Path.GetFileName(file)}.{scale}.ours"), image.ToPixelData());
        }

        await Assert.That(Corpus.Sha256(image.ToPixelData())).IsEqualTo(reference.GetProperty("sha256").GetString());
    }

    public static TestCases<int, bool, int, int> OrientationCases()
    {
        var data = new TestCases<int, bool, int, int>();
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

    [Test]
    [MethodDataSource(nameof(OrientationCases))]
    public async Task TargetSize_GivesTheThumbnailSize_InTheReturnedOrientation(int orientation, bool apply, int width, int height)
    {
        string file = Corpus.PathOf($"jpeg/orientation_{orientation}.jpg");
        var options = new DecoderOptions { ApplyExifOrientation = apply };
        using Image expected = Image.Load(file, options);
        expected.Mutate(x => x.Thumbnail(width, height));
        using Image image = Image.Load(file, options with { TargetSize = new(width, height) });

        await Assert.That(image.Size).IsEqualTo(expected.Size);
        await Assert.That(image.Metadata.Orientation).IsEqualTo(expected.Metadata.Orientation);
        await Assert.That(await Psnr(expected, image) > 30).IsTrue().Because($"PSNR {await Psnr(expected, image):F1} dB");
    }

    [Test]
    [Arguments("jpeg/q90_420.jpg")]
    [Arguments("jpeg/q90_422.jpg")]
    [Arguments("jpeg/q90_440.jpg")]
    [Arguments("jpeg/q90_411.jpg")]
    [Arguments("jpeg/progressive_420.jpg")]
    [Arguments("jpeg/gray_progressive.jpg")]
    [Arguments("jpeg/cmyk.jpg")]
    [Arguments("jpeg/crafted_ycck.jpg")]
    [Arguments("jpeg/odd_420.jpg")]
    [Arguments("jpeg/odd_progressive.jpg")]
    public async Task TargetSize_IsCloseToFullDecodeAndThumbnail(string file)
    {
        // A reduced decode is a different algorithm: with no gap, 1/8 scale replicates the chroma of 4:1:1 four times.
        foreach ((double gap, double minPsnr) in ((double, double)[])[(2, 30), (1, 25)])
        {
            foreach (int side in (int[])[8, 20, 33, 64])
            {
                using Image expected = Image.Load(Corpus.PathOf(file));
                expected.Mutate(x => x.Thumbnail(side, side));
                using Image image = Image.Load(Corpus.PathOf(file), new DecoderOptions { TargetSize = new(side, side), TargetReducingGap = gap });
                await Assert.That(image.Size).IsEqualTo(expected.Size);
                double psnr = await Psnr(expected, image);
                await Assert.That(psnr > minPsnr).IsTrue().Because($"{side}, gap {gap}: PSNR {psnr:F1} dB");
            }
        }
    }

    [Test]
    public async Task TargetSize_WithoutReduction_EqualsLoadAndThumbnail()
    {
        // 256x192 to 200x150 leaves no room for a 1/2 decode, so the result is the full decode resized.
        string file = Corpus.PathOf("jpeg/q90_420.jpg");
        using Image expected = Image.Load(file);
        expected.Mutate(x => x.Thumbnail(200, 200, ResamplingFilter.Bicubic));
        using Image image = Image.Load(file, new DecoderOptions { TargetSize = new(200, 200), TargetResampling = ResamplingFilter.Bicubic });
        await Assert.That(image.ToPixelData()).IsEquivalentTo(expected.ToPixelData(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(32, 0, 2.0, 4)]
    [Arguments(32, 0, 1.0, 8)]
    [Arguments(16, 0, 2.0, 8)]
    [Arguments(0, 48, 2.0, 2)]
    [Arguments(0, 48, 1.5, 2)]
    [Arguments(0, 48, 1.0, 4)]
    [Arguments(100, 100, 1.0, 2)]
    [Arguments(200, 200, 1.0, 1)]
    [Arguments(500, 500, 1.0, 1)]
    public async Task SelectScale_KeepsTheReducingGap(int width, int height, double gap, int expected)
    {
        JpegFile file = JpegFile.Read(Corpus.Read("jpeg/q90_420.jpg"), DecoderOptions.Default, headerOnly: true);
        var options = new DecoderOptions { TargetSize = new(width, height), TargetReducingGap = gap };
        await Assert.That(JpegDecoder.SelectScale(file.Frame!, file.Exif, options)).IsEqualTo(expected);
    }

    [Test]
    public async Task SelectScale_SwapsTheBoxForRotatedOrientations()
    {
        // 256x192 stored with orientation 6 is 192x256 once oriented: 32 px wide is 32x43, i.e. 43x32 stored.
        JpegFile file = JpegFile.Read(Corpus.Read("jpeg/orientation_6.jpg"), DecoderOptions.Default, headerOnly: true);
        var options = new DecoderOptions { TargetSize = new(32, 0), TargetReducingGap = 1 };
        await Assert.That(JpegDecoder.SelectScale(file.Frame!, file.Exif, options with { ApplyExifOrientation = true })).IsEqualTo(4);
        await Assert.That(JpegDecoder.SelectScale(file.Frame!, file.Exif, options)).IsEqualTo(8);
    }

    [Test]
    public async Task TargetSize_DoesNotBypassTheSourceLimits()
    {
        var options = new DecoderOptions { TargetSize = new(8, 8), MaxPixels = (256 * 192) - 1 };
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(Corpus.PathOf("jpeg/q90_420.jpg"), options));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(Corpus.PathOf("jpeg/progressive_420.jpg"), options));
        using Image image = Image.Load(Corpus.PathOf("jpeg/q90_420.jpg"), options with { MaxPixels = 256 * 192 });
        await Assert.That((image.Width, image.Height)).IsEqualTo((8, 6));
    }

    [Test]
    [Arguments("png/odd_rgba.png")]
    [Arguments("gif/pil_anim_dispose2.gif")]
    [Arguments("webp/lossy/graphic_alpha_q75.webp")]
    public async Task TargetSize_ForOtherFormats_IsFullDecodeAndThumbnail(string file)
    {
        using Image expected = Image.Load(Corpus.PathOf(file));
        expected.Mutate(x => x.Thumbnail(20, 20));
        using Image image = Image.Load(Corpus.PathOf(file), new DecoderOptions { TargetSize = new(20, 20) });
        await Assert.That(image.FrameCount).IsEqualTo(expected.FrameCount);
        for (int i = 0; i < image.FrameCount; i++)
        {
            await Assert.That(image.Frames[i].Size).IsEqualTo(expected.Frames[i].Size);
            await Assert.That(image.Frames[i].Pixels).IsEquivalentTo(expected.Frames[i].Pixels, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task TargetSize_LargerThanTheImage_KeepsIt()
    {
        string file = Corpus.PathOf("jpeg/progressive_444.jpg");
        using Image expected = Image.Load(file);
        using Image image = Image.Load(file, new DecoderOptions { TargetSize = new(1000, 0), TargetReducingGap = 1 });
        await Assert.That(image.ToPixelData()).IsEquivalentTo(expected.ToPixelData(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task TargetOptions_AreValidated()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetSize = new(0, 0) });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetSize = new(-1, 10) });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetReducingGap = 0.99 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetReducingGap = double.NaN });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { TargetResampling = (ResamplingFilter)99 });
        await Assert.That(new DecoderOptions { TargetSize = new(10, 0) }.TargetSize).IsEqualTo(new System.Drawing.Size(10, 0));
    }

    [Test]
    public async Task ScaledDecode_OfTruncatedAndCorruptInput_FailsCleanly()
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
                await Assert.That(image.Width <= 16 && image.Height <= 16).IsTrue();
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }
    }

    private static async Task<double> Psnr(Image expected, Image actual)
    {
        byte[] a = expected.ToPixelData(), b = actual.ToPixelData();
        await Assert.That(b.Length).IsEqualTo(a.Length);
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (sum / a.Length));
    }
}
