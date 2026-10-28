using System.Text.Json;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;

namespace PictureSharp.Tests.ColorManagement;

/// <summary>ConvertToSrgb, DecoderOptions.ColorConversion and the PNG color description (gAMA, cHRM, sRGB).</summary>
public class ColorConversionTests
{
    private static Rgba32[] Pixels(Image image) => [.. image.Frames.SelectMany(f => f.GetPixelSpan<Rgba32>().ToArray())];

    private static JsonElement PngCases => IccConversionTests.Manifest.GetProperty("png");

    public static IEnumerable<string> PngNames() => PngCases.EnumerateObject().Select(p => p.Name);

    [Test]
    [MethodDataSource(nameof(PngNames))]
    public async Task PngColorChunks_FollowPrecedence_AndMatchLittleCms(string name)
    {
        JsonElement entry = PngCases.GetProperty(name);
        string path = IccConversionTests.Color(entry.GetProperty("file").GetString()!);
        using Image converted = Image.Load(path, new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
        await Assert.That(converted.Metadata.IccProfile).IsNull();
        await Assert.That(converted.Metadata.HasColorDescription).IsFalse();

        string? reference = entry.GetProperty("reference").GetString();
        using Image expected = Image.Load(reference is null ? IccConversionTests.Color("input_rgb.png") : IccConversionTests.Color(reference));
        if (reference is null)
        {
            // Declared or implied sRGB: unchanged.
            await Assert.That(Pixels(converted)).IsEquivalentTo(Pixels(expected), CollectionOrdering.Matching);
        }
        else
        {
            await IccConversionTests.AssertWithinTolerance(converted, expected, gray: false, name);
        }
    }

    [Test]
    public async Task PngDecoders_ReadTheColorDescription()
    {
        string path = IccConversionTests.Color("png_srgb_overrides_gama.png");
        using Image image = Image.Load(path);
        await Assert.That(image.Metadata.SrgbRenderingIntent).IsEqualTo(RenderingIntent.Perceptual);
        await Assert.That(image.Metadata.Gamma).IsEqualTo(0.55556f);
        await Assert.That(image.Metadata.Chromaticities!.Value.RedX).IsEqualTo(0.68f);
        await Assert.That(Image.ReadMetadata(path).Gamma).IsEqualTo(0.55556f);

        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(path);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Metadata.Gamma).IsEqualTo(0.55556f);
        await Assert.That(reader.Metadata.SrgbRenderingIntent).IsEqualTo(RenderingIntent.Perceptual);

        using Image late = Image.Load(IccConversionTests.Color("png_gama_after_idat_ignored.png"));
        await Assert.That(late.Metadata.Gamma).IsNull();
        using Image zero = Image.Load(IccConversionTests.Color("png_gama_zero_ignored.png"));
        await Assert.That(zero.Metadata.Gamma).IsNull();
    }

    [Test]
    public async Task IgnoreMetadata_SkipsColorChunksWithTheProfile()
    {
        // The gAMA/cHRM chunks back up the ignored iCCP; keeping them alone would describe the wrong colors.
        string path = IccConversionTests.Color("png_iccp_overrides_all.png");
        var options = new DecoderOptions { IgnoreMetadata = true };
        using Image image = Image.Load(path, options);
        await Assert.That(image.Metadata.IccProfile).IsNull();
        await Assert.That(image.Metadata.HasColorDescription).IsFalse();
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(path, options);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Metadata.HasColorDescription).IsFalse();
    }

    [Test]
    public async Task IcoLoadAll_HonorsColorConversion()
    {
        using Image variant = Image.Load(IccConversionTests.Color("input_rgb.png"));
        using Image small = variant.Clone(x => x.Resize(32, 32));
        small.Metadata.IccProfile = File.ReadAllBytes(IccConversionTests.Color("adobe_rgb_v2_gamma.icc"));
        using var ico = new MemoryStream();
        IcoFile.Save([small], ico);
        using IcoImageCollection plain = IcoFile.LoadAll(ico.ToArray());
        using IcoImageCollection converted = IcoFile.LoadAll(ico.ToArray(), new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
        await Assert.That(plain[0].Metadata.IccProfile).IsNotNull();
        await Assert.That(converted[0].Metadata.IccProfile).IsNull();
        using Image expected = plain[0].Clone(x => x.ConvertToSrgb());
        await Assert.That(Pixels(converted[0])).IsEquivalentTo(Pixels(expected), CollectionOrdering.Matching);
        Assert.ThrowsExactly<ArgumentException>(() => IcoFile.LoadAll(ico.ToArray(),
            new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb, IgnoreMetadata = true }));
    }

    [Test]
    [Property("Category", "Slow")]
    [NotInParallel]
    public async Task ConvertToSrgb_CanceledMidway_LeavesPixelsAndProfile()
    {
        using var frame = new Image(3000, 3000, new Rgba32(200, 40, 90));
        using Image image = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), frame, frame, frame);
        image.Metadata.IccProfile = File.ReadAllBytes(IccConversionTests.Color("adobe_rgb_v2_gamma.icc"));
        Rgba32 before = image[0, 0];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(15));
        try
        {
            image.Mutate(x => x.ConvertToSrgb(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Nothing is committed: every frame keeps its pixels and the profile still describes them.
            foreach (ImageFrame f in image.Frames) await Assert.That(f[2999, 2999]).IsEqualTo(before);
            await Assert.That(image.Metadata.IccProfile).IsNotNull();
            return;
        }

        // A machine fast enough to finish converts everything.
        await Assert.That(image.Frames[2][2999, 2999]).IsNotEqualTo(before);
        await Assert.That(image.Metadata.IccProfile).IsNull();
    }

    [Test]
    public async Task ConvertToSrgb_MatchesDecoderOption_ConvertsEveryFrame_AndIsIdempotent()
    {
        byte[] icc = File.ReadAllBytes(IccConversionTests.Color("adobe_rgb_v2_gamma.icc"));
        using Image source = Image.Load(IccConversionTests.Color("input_rgb.png"));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), source, source);
        animation.Metadata.IccProfile = icc;
        byte[] png = animation.Encode(new PngEncoderOptions());

        using Image decoded = Image.Load(png, new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
        using Image mutated = Image.Load(png);
        mutated.Mutate(x => x.ConvertToSrgb());
        using Image expected = Image.Load(IccConversionTests.Color("adobe_rgb_v2_gamma.ref.png"));
        foreach (Image image in new[] { decoded, mutated })
        {
            await Assert.That(image.FrameCount).IsEqualTo(2);
            await Assert.That(image.Metadata.IccProfile).IsNull();
            for (int i = 0; i < 2; i++)
            {
                using Image frame = image.Frames.CloneFrame(i);
                await IccConversionTests.AssertWithinTolerance(frame, expected, gray: false, $"frame {i}");
            }
        }

        await Assert.That(Pixels(decoded)).IsEquivalentTo(Pixels(mutated), CollectionOrdering.Matching);
        Rgba32[] once = Pixels(mutated);
        mutated.Mutate(x => x.ConvertToSrgb());
        await Assert.That(Pixels(mutated)).IsEquivalentTo(once, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConvertToSrgb_GrayProfile_KeepsGrayPixelsGray()
    {
        using Image image = Image.Load(IccConversionTests.Color("input_gray.png"));
        image.Metadata.IccProfile = File.ReadAllBytes(IccConversionTests.Color("gray_v2_gamma22.icc"));
        image.Mutate(x => x.ConvertToSrgb());
        using Image expected = Image.Load(IccConversionTests.Color("gray_v2_gamma22.ref.png"));
        await IccConversionTests.AssertWithinTolerance(image, expected, gray: true, "gray");
        await Assert.That(Pixels(image).All(p => p.R == p.G && p.G == p.B)).IsTrue();
    }

    [Test]
    public async Task ConvertToSrgb_WithoutDescription_LeavesPixels()
    {
        using Image image = Image.Load(IccConversionTests.Color("input_rgb.png"));
        Rgba32[] before = Pixels(image);
        image.Mutate(x => x.ConvertToSrgb());
        await Assert.That(Pixels(image)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConvertToSrgb_InvalidProfiles_ThrowAndKeepTheImage()
    {
        foreach (JsonProperty entry in IccConversionTests.Manifest.GetProperty("invalid").EnumerateObject())
        {
            using Image image = Image.Load(IccConversionTests.Color("input_rgb.png"));
            Rgba32[] before = Pixels(image);
            image.Metadata.IccProfile = File.ReadAllBytes(IccConversionTests.Color(entry.Value.GetProperty("file").GetString()!));
            Exception? error = null;
            try { image.Mutate(x => x.ConvertToSrgb()); }
            catch (Exception ex) { error = ex; }
            await Assert.That(error?.GetType().Name).IsEqualTo(entry.Value.GetProperty("expected").GetString()).Because(entry.Name);
            await Assert.That(Pixels(image)).IsEquivalentTo(before, CollectionOrdering.Matching);
            await Assert.That(image.Metadata.IccProfile).IsNotNull(); // The profile is kept for transfer.
        }
    }

    [Test]
    public async Task DecoderOption_RejectsIgnoreMetadata_AndFrameReaders()
    {
        byte[] png = File.ReadAllBytes(IccConversionTests.Color("png_gama_chrm.png"));
        var options = new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb };
        Assert.ThrowsExactly<ArgumentException>(() => Image.Load(png, options with { IgnoreMetadata = true }));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await Image.OpenFrameReaderAsync(new MemoryStream(png), options));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new DecoderOptions { ColorConversion = (ColorConversionMode)7 });
        using Image unconverted = Image.Load(png);
        await Assert.That(unconverted.Metadata.Gamma).IsEqualTo(0.45455f);
    }

    [Test]
    public async Task PngEncoder_WritesTheColorDescription_AndRoundTrips()
    {
        using var image = new Image(2, 2);
        image.Metadata.Gamma = 0.5f;
        image.Metadata.Chromaticities = CieChromaticities.Srgb;
        image.Metadata.SrgbRenderingIntent = RenderingIntent.Saturation;
        using Image read = Image.Load(image.Encode(new PngEncoderOptions()));
        await Assert.That(read.Metadata.Gamma).IsEqualTo(0.5f);
        await Assert.That(read.Metadata.Chromaticities).IsEqualTo(CieChromaticities.Srgb);
        await Assert.That(read.Metadata.SrgbRenderingIntent).IsEqualTo(RenderingIntent.Saturation);

        using Image filtered = Image.Load(image.Encode(new PngEncoderOptions { Metadata = MetadataKinds.None }));
        await Assert.That(filtered.Metadata.HasColorDescription).IsFalse();

        // PNG forbids sRGB together with iCCP; the profile wins.
        image.Metadata.IccProfile = File.ReadAllBytes(IccConversionTests.Color("display_p3_v4_para3.icc"));
        using Image withProfile = Image.Load(image.Encode(new PngEncoderOptions()));
        await Assert.That(withProfile.Metadata.SrgbRenderingIntent).IsNull();
        await Assert.That(withProfile.Metadata.Gamma).IsEqualTo(0.5f);
    }

    [Test]
    public async Task EditMetadata_ChangesPngColorChunks_AndRejectsOtherFormats()
    {
        byte[] png = File.ReadAllBytes(IccConversionTests.Color("png_gama_chrm.png"));
        byte[] edited = Image.EditMetadata(png, m =>
        {
            m.Gamma = null;
            m.Chromaticities = null;
            m.SrgbRenderingIntent = RenderingIntent.RelativeColorimetric;
        });
        ImageMetadata read = Image.ReadMetadata(edited);
        await Assert.That(read.Gamma).IsNull();
        await Assert.That(read.Chromaticities).IsNull();
        await Assert.That(read.SrgbRenderingIntent).IsEqualTo(RenderingIntent.RelativeColorimetric);
        using Image a = Image.Load(png), b = Image.Load(edited);
        await Assert.That(Pixels(b)).IsEquivalentTo(Pixels(a), CollectionOrdering.Matching);
        await Assert.That(Image.EditMetadata(png, _ => { }).AsSpan().SequenceEqual(png)).IsTrue();

        byte[] webp = a.Encode(new WebPEncoderOptions { Lossless = true });
        Assert.ThrowsExactly<NotSupportedException>(() => Image.EditMetadata(webp, m => m.Gamma = 0.45455f));
        byte[] jpeg = a.Encode(ImageFormat.Jpeg);
        Assert.ThrowsExactly<NotSupportedException>(() => Image.EditMetadata(jpeg, m => m.SrgbRenderingIntent = RenderingIntent.Perceptual));
    }

    [Test]
    public async Task Metadata_ValidatesAndCopiesTheColorDescription()
    {
        var metadata = new ImageMetadata();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => metadata.Gamma = 0);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => metadata.Gamma = float.NaN);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => metadata.Gamma = 1e-6f);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => metadata.Gamma = 30000f);
        metadata.Gamma = 21474.83647f;
        using (var extreme = new Image(1, 1))
        {
            extreme.Metadata.Gamma = 0.00001f;
            using Image read = Image.Load(extreme.Encode(new PngEncoderOptions()));
            await Assert.That(read.Metadata.Gamma).IsEqualTo(0.00001f);
        }

        // Collinear primaries, and a white point outside the primaries' triangle.
        Assert.ThrowsExactly<ArgumentException>(() => _ = new CieChromaticities(0.3f, 0.3f, 0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f));
        Assert.ThrowsExactly<ArgumentException>(() => _ = new CieChromaticities(0.05f, 0.9f, 0.64f, 0.33f, 0.3f, 0.6f, 0.15f, 0.06f));
        Assert.ThrowsExactly<ArgumentException>(() => metadata.Chromaticities = default(CieChromaticities));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => metadata.SrgbRenderingIntent = (RenderingIntent)9);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new CieChromaticities(0.3f, 0, 0.6f, 0.3f, 0.3f, 0.6f, 0.15f, 0.06f));
        metadata.Gamma = 0.45455f;
        metadata.Chromaticities = CieChromaticities.Srgb;
        ImageMetadata copy = metadata.Clone();
        metadata.Clear(); // Blocks only.
        await Assert.That(metadata.HasColorDescription).IsTrue();
        metadata.ClearColorDescription();
        await Assert.That(metadata.HasColorDescription).IsFalse();
        await Assert.That(copy.Gamma).IsEqualTo(0.45455f);
        await Assert.That(copy.Chromaticities).IsEqualTo(CieChromaticities.Srgb);
    }
}
