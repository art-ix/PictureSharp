using System.Drawing;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Algorithms.Hdr;
using PictureSharp.Domain.Hdr;
using PictureSharp.Formats;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.ColorManagement;
using PictureSharp.Tests.Extensibility;
using static PictureSharp.Tests.Core.HighBitDepthProcessingTests;

namespace PictureSharp.Tests.Core;

/// <summary>
/// 0.9.0 step 4: color management and HDR on 16-bit images. Float <c>ConvertToSrgb</c> against a double-precision
/// reference, <c>ToSrgb</c> with an 8-bit request rounding once (and its budget), plugin output that is already sRGB,
/// 16-bit tone mapping, gain maps generated and reconstructed against the stored 16-bit base, and 16-bit plugin maps
/// through budget, geometry and reconstruction.
/// </summary>
public class HighBitDepthColorTests
{
    private const int W = 48, H = 40;

    private static Rgba64[] Pixels16(Image image) => image.GetPixelSpan<Rgba64>().ToArray();

    private static async Task AssertWithin(Rgba64[] actual, Rgba64[] expected, int max, double rms, string name)
    {
        int worst = 0;
        double sum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Rgba64 a = actual[i], e = expected[i];
            await Assert.That(a.A).IsEqualTo(e.A);
            foreach (int d in new[] { a.R - e.R, a.G - e.G, a.B - e.B })
            {
                worst = Math.Max(worst, Math.Abs(d));
                sum += (double)d * d;
            }
        }

        double error = Math.Sqrt(sum / (expected.Length * 3));
        Console.WriteLine($"{name}: max {worst}, RMS {error:F4} (16-bit codes)");
        await Assert.That(worst).IsLessThanOrEqualTo(max);
        await Assert.That(error).IsLessThanOrEqualTo(rms);
    }

    // ------------------------------------------------------------ ConvertToSrgb

    public static IEnumerable<string> Profiles() => IccConversionTests.Profiles();

    /// <summary>The conversion of <paramref name="p"/> in double precision: curves, the D50 matrix, the sRGB curve, one rounding.</summary>
    private static Rgba64 Reference(IccColorModel model, Rgba64 p)
    {
        double r = model.Red.Evaluate(p.R / 65535.0);
        if (model.Colorants is not { } colorants)
        {
            ushort Gray(ushort v) => WideReference.Round16(WideReference.Encode(model.Red.Evaluate(v / 65535.0)));
            return new Rgba64(Gray(p.R), Gray(p.G), Gray(p.B), p.A);
        }

        double g = model.Green!.Evaluate(p.G / 65535.0), b = model.Blue!.Evaluate(p.B / 65535.0);
        ColorMatrix m = ColorMatrix.D50ToSrgb * colorants;
        (double x, double y, double z) = m.Transform((r, g, b));
        return new Rgba64(WideReference.Round16(WideReference.Encode(x)), WideReference.Round16(WideReference.Encode(y)),
            WideReference.Round16(WideReference.Encode(z)), p.A);
    }

    [Test]
    [MethodDataSource(nameof(Profiles))]
    public async Task ConvertToSrgb_OnSixteenBits_MatchesTheDoubleReference(string name)
    {
        byte[] icc = File.ReadAllBytes(IccConversionTests.Color(name + ".icc"));
        IccColorModel model = IccProfileParser.Parse(icc);
        using Image image = Image16(W, H, Alpha.Varying, seed: 51);
        Rgba64[] source = Pixels16(image);
        image.Metadata.IccProfile = icc;
        image.Mutate(x => x.ConvertToSrgb());
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(image.Metadata.IccProfile).IsNull();
        await AssertWithin(Pixels16(image), [.. source.Select(p => Reference(model, p))], 1, 0.3, $"ConvertToSrgb {name}");
    }

    [Test]
    public async Task ConvertToSrgb_OnSixteenBits_ConvertsAColorDescription()
    {
        var chromaticities = new CieChromaticities(0.3127f, 0.329f, 0.68f, 0.32f, 0.265f, 0.69f, 0.15f, 0.06f);
        using Image image = Image16(W, H, Alpha.Opaque, seed: 52);
        Rgba64[] source = Pixels16(image);
        image.Metadata.Gamma = 1 / 2.2f;
        image.Metadata.Chromaticities = chromaticities;
        IccColorModel model = ColorModels.FromDescription(image.Metadata.Gamma, chromaticities)!;
        image.Mutate(x => x.ConvertToSrgb());
        await AssertWithin(Pixels16(image), [.. source.Select(p => Reference(model, p))], 1, 0.3, "ConvertToSrgb gAMA/cHRM");
    }

    /// <summary>8-bit images keep the 256-entry tables: the result equals the table conversion of the same pixels.</summary>
    [Test]
    public async Task ConvertToSrgb_OnEightBits_KeepsTheTables()
    {
        byte[] icc = File.ReadAllBytes(IccConversionTests.Color("display_p3_v4_para3.icc"));
        using Image image = Image16(W, H, Alpha.Varying, seed: 53).Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        Rgba32[] expected = image.GetPixelSpan<Rgba32>().ToArray();
        new SrgbConversion(IccProfileParser.Parse(icc)).Apply(expected);
        image.Metadata.IccProfile = icc;
        image.Mutate(x => x.ConvertToSrgb());
        await Assert.That(image.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    // ------------------------------------------------------------ ToSrgb while decoding

    private static byte[] Png16WithProfile(int width, int height, byte[]? icc, int seed = 54)
    {
        using Image image = Image16(width, height, Alpha.Varying, seed);
        image.Metadata.IccProfile = icc;
        return image.ToPng(new PngEncoderOptions { BitDepth = PngBitDepth.Sixteen });
    }

    private static byte[] DisplayP3 => File.ReadAllBytes(IccConversionTests.Color("display_p3_v4_para3.icc"));

    /// <summary>
    /// With an 8-bit request, <c>ToSrgb</c> converts the 16-bit samples in floating point and rounds once: the result
    /// is the 16-bit conversion rounded to 8 bits, within half a level of the exact one.
    /// </summary>
    [Test]
    public async Task ToSrgb_WithAnEightBitRequest_RoundsOnce()
    {
        byte[] png = Png16WithProfile(W, H, DisplayP3);
        using Image narrow = Image.Load(png, new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
        using Image wide = Image.Load(png, new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb, PreserveBitDepth = true });
        using Image source = Image.Load(png, new DecoderOptions { PreserveBitDepth = true });
        await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);
        await Assert.That(wide.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(narrow.Metadata.IccProfile).IsNull();
        Rgba32[] actual = narrow.GetPixelSpan<Rgba32>().ToArray();
        await Assert.That(actual).IsEquivalentTo(Pixels16(wide).Select(p => p.ToRgba32()).ToArray(), CollectionOrdering.Matching);

        // Against the exact conversion of the 16-bit samples rounded to 8 bits; converting the rounded 8-bit samples
        // through the tables (the old path) is further off.
        IccColorModel model = IccProfileParser.Parse(DisplayP3);
        Rgba64[] samples = Pixels16(source);
        int exact = 0, oldExact = 0;
        Rgba32[] old = [.. samples.Select(p => p.ToRgba32())];
        new SrgbConversion(model).Apply(old);
        for (int i = 0; i < samples.Length; i++)
        {
            Rgba32 e = Reference(model, samples[i]).ToRgba32();
            int d = Math.Max(Math.Abs(actual[i].R - e.R), Math.Max(Math.Abs(actual[i].G - e.G), Math.Abs(actual[i].B - e.B)));
            await Assert.That(d).IsLessThanOrEqualTo(1);
            exact += d == 0 ? 1 : 0;
            oldExact += old[i].R == e.R && old[i].G == e.G && old[i].B == e.B ? 1 : 0;
        }

        Console.WriteLine($"ToSrgb exact pixels: {exact} of {samples.Length}; 8-bit tables: {oldExact}");
        await Assert.That(exact).IsGreaterThan(oldExact);
        await Assert.That(exact).IsGreaterThanOrEqualTo(samples.Length * 99 / 100);
    }

    /// <summary>An 8-bit source converts through the tables as before.</summary>
    [Test]
    public async Task ToSrgb_OnAnEightBitSource_IsUnchanged()
    {
        using Image image = Image16(W, H, Alpha.Varying, seed: 55).Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        image.Metadata.IccProfile = DisplayP3;
        byte[] png = image.ToPng();
        Rgba32[] expected = image.GetPixelSpan<Rgba32>().ToArray();
        new SrgbConversion(IccProfileParser.Parse(DisplayP3)).Apply(expected);
        foreach (bool preserve in new[] { false, true })
        {
            using Image loaded = Image.Load(png, new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb, PreserveBitDepth = preserve });
            await Assert.That(loaded.PixelType).IsEqualTo(PixelType.Rgba32);
            await Assert.That(loaded.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    /// <summary>
    /// <c>ToSrgb</c> with an 8-bit request decodes a deeper source at 16 bits, so it is charged as 16-bit: two units
    /// per pixel; an 8-bit source is charged one unit as before, and without conversion a 16-bit source too.
    /// </summary>
    [Test]
    public async Task ToSrgb_IsChargedAtTheDecodedDepth()
    {
        const int Width = 20, Height = 10, Pixels = Width * Height;
        byte[] deep = Png16WithProfile(Width, Height, DisplayP3);
        using Image eight = Image16(Width, Height, Alpha.Opaque).Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        eight.Metadata.IccProfile = DisplayP3;
        byte[] shallow = eight.ToPng();
        DecoderOptions ToSrgb(long maxPixels) => new() { ColorConversion = ColorConversionMode.ToSrgb, MaxPixels = maxPixels };

        using (Image.Load(deep, ToSrgb(2 * Pixels)))
        {
        }

        await Assert.That(() => Image.Load(deep, ToSrgb((2 * Pixels) - 1))).Throws<ImageFormatException>();
        using (Image.Load(deep, new DecoderOptions { MaxPixels = Pixels }))
        {
        }

        using (Image.Load(shallow, ToSrgb(Pixels)))
        {
        }

        await Assert.That(() => Image.Load(shallow, ToSrgb(Pixels - 1))).Throws<ImageFormatException>();
    }

    /// <summary>
    /// A plugin that renders to sRGB itself (as the JPEG XL plugin renders XYB) reports no profile, so the library does
    /// not convert again; with an 8-bit request it still receives <c>PreserveBitDepth</c> and its result is rounded once.
    /// </summary>
    [Test]
    public async Task ToSrgb_DoesNotConvertPluginOutputAgain()
    {
        var module = new SelfConvertingFormat();
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(module).Build();
        byte[] data = SelfConvertingFormat.Create(W, H);
        using Image wide = Image.Load(data, new DecoderOptions { Configuration = configuration, ColorConversion = ColorConversionMode.ToSrgb, PreserveBitDepth = true });
        await Assert.That(Pixels16(wide)).IsEquivalentTo(SelfConvertingFormat.Rendered(W, H), CollectionOrdering.Matching);
        using Image narrow = Image.Load(data, new DecoderOptions { Configuration = configuration, ColorConversion = ColorConversionMode.ToSrgb });
        await Assert.That(module.LastPreserveBitDepth).IsTrue();
        await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);
        await Assert.That(narrow.GetPixelSpan<Rgba32>().ToArray())
            .IsEquivalentTo(SelfConvertingFormat.Rendered(W, H).Select(p => p.ToRgba32()).ToArray(), CollectionOrdering.Matching);
        using Image raw = Image.Load(data, new DecoderOptions { Configuration = configuration });
        await Assert.That(module.LastPreserveBitDepth).IsFalse();
        await Assert.That(raw.Metadata.IccProfile).IsNotNull();
    }

    /// <summary>
    /// A 16-bit decoder that converts to sRGB itself for <see cref="ColorConversionMode.ToSrgb"/> (any deterministic
    /// rendering serves) and otherwise returns its samples with a Display P3 profile.
    /// </summary>
    private sealed class SelfConvertingFormat : IImageFormatModule, IImageFormatDecoder
    {
        public static ImageFormat Format { get; } = ImageFormat.Create("SRGB16", "image/x-self-converting", ".srgb16");

        ImageFormat IImageFormatModule.Format => Format;

        public int HeaderSize => 6;

        public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("SRGB16"u8);

        public IImageFormatDecoder Decoder => this;

        public bool LastPreserveBitDepth { get; private set; }

        public static byte[] Create(int width, int height) => [.. "SRGB16"u8, (byte)width, (byte)height];

        public static Rgba64[] Stored(int width, int height) => HighBitDepthProcessingTests.Pixels(width, height, Alpha.Varying, 56);

        public static Rgba64[] Rendered(int width, int height) =>
            [.. Stored(width, height).Select(p => new Rgba64((ushort)(65535 - p.G), p.B, (ushort)(p.R ^ 0x5A5A), p.A))];

        public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
            new() { Format = Format, Width = data.Span[6], Height = data.Span[7], Color = TestColor.Rgb8, Compression = ImageCompression.Lossless };

        public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
        {
            int width = data.Span[6], height = data.Span[7];
            LastPreserveBitDepth = context.Options.PreserveBitDepth;
            PixelType type = context.Options.PreserveBitDepth ? PixelType.Rgba64 : PixelType.Rgba32;
            context.ReservePixels(width, height, type);
            bool render = context.Options.ColorConversion == ColorConversionMode.ToSrgb;
            Rgba64[] pixels = render ? Rendered(width, height) : Stored(width, height);
            Image image = Image.LoadPixelData<Rgba64>(pixels, width, height);
            if (type == PixelType.Rgba32)
            {
                image.Mutate(x => x.ConvertPixelType(PixelType.Rgba32));
            }

            if (!render)
            {
                image.Metadata.IccProfile = DisplayP3;
            }

            return image;
        }
    }

    // ------------------------------------------------------------ tone mapping and gain maps

    private static HdrImage Hdr(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new Rgba128[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            float level = MathF.Pow(2, (float)((random.NextDouble() * 7) - 4));
            pixels[i] = new Rgba128(level * (float)random.NextDouble(), level * (float)random.NextDouble(), level * (float)random.NextDouble(),
                i % 7 == 0 ? (float)random.NextDouble() : 1f);
        }

        return new HdrImage(width, height, pixels, HdrColorSpace.LinearSrgb, 8f);
    }

    [Test]
    public async Task ToneMap_ToSixteenBits_MatchesTheReference()
    {
        HdrImage hdr = Hdr(W, H, 61);
        var options = new ToneMappingOptions { Exposure = -0.5f, WhitePoint = 6f, PixelType = PixelType.Rgba64 };
        using Image image = hdr.ToneMap(options);
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
        // Alpha is quantized from its float, as the 8-bit operator does; the color is computed in double precision.
        double exposure = Math.Pow(2, options.Exposure), inverseWhite = 1.0 / (6.0 * 6.0);
        Rgba64[] expected = [.. hdr.GetPixelSpan().ToArray().Select(p =>
        {
            double r = p.R * exposure, g = p.G * exposure, b = p.B * exposure;
            double l = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            double scale = l > 0 ? (1 + (l * inverseWhite)) / (1 + l) : 0;
            return new Rgba64(WideReference.Round16(WideReference.Encode(r * scale)), WideReference.Round16(WideReference.Encode(g * scale)),
                WideReference.Round16(WideReference.Encode(b * scale)), (ushort)(int)((Math.Clamp(p.A, 0f, 1f) * 65535f) + 0.5f));
        })];
        await AssertWithin(Pixels16(image), expected, 1, 0.3, "ToneMap 16-bit");
        using Image narrow = hdr.ToneMap(options with { PixelType = PixelType.Rgba32 });
        await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);
    }

    /// <summary>
    /// A gain map generated with a 16-bit SDR base describes the stored 16-bit samples: <c>ToHdr</c> reproduces the
    /// ISO 21496-1 reconstruction of exactly those samples, and the HDR image closer than with an 8-bit base.
    /// </summary>
    [Test]
    public async Task GainMap_WithASixteenBitBase_ReconstructsFromTheStoredBase()
    {
        HdrImage hdr = Hdr(W, H, 62);
        var options16 = new GainMapOptions { ToneMapping = new ToneMappingOptions { PixelType = PixelType.Rgba64 } };
        using Image image = hdr.ToGainMapImage(options16);
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
        GainMapAttachment attachment = image.AttachedGainMap!;
        DecodedGainMap map = attachment.EditedMap!.Value;
        GainMapParameters parameters = attachment.Parameters!;
        Rgba64[] stored = Pixels16(image);
        Rgba128[] reconstructed = image.ToHdr().GetPixelSpan().ToArray();
        double worst = 0;
        for (int i = 0; i < stored.Length; i++)
        {
            Rgba64 p = stored[i];
            Rgba32 code = map.Pixels.Rgba32![i];
            ReadOnlySpan<ushort> values = [p.R, p.G, p.B];
            ReadOnlySpan<byte> codes = [code.R, code.G, code.B];
            ReadOnlySpan<float> actual = [reconstructed[i].R, reconstructed[i].G, reconstructed[i].B];
            for (int c = 0; c < 3; c++)
            {
                double g = codes[c] / 255.0;
                double factor = Math.Pow(2, (parameters.GainMapMin[c] * (1 - g)) + (parameters.GainMapMax[c] * g));
                double e = ((WideReference.Decode(values[c] / 65535.0) + parameters.BaseOffset[c]) * factor) - parameters.AlternateOffset[c];
                worst = Math.Max(worst, Math.Abs(actual[c] - e) / Math.Max(1, Math.Abs(e)));
            }
        }

        Console.WriteLine($"ToHdr against the stored 16-bit base: worst relative error {worst:E2}");
        await Assert.That(worst).IsLessThan(2e-5);

        using Image eight = hdr.ToGainMapImage();
        await Assert.That(Distance(image.ToHdr(), hdr)).IsLessThan(Distance(eight.ToHdr(), hdr));
    }

    // Mean relative distance of the colors of opaque pixels.
    private static double Distance(HdrImage a, HdrImage b)
    {
        ReadOnlySpan<Rgba128> x = a.GetPixelSpan(), y = b.GetPixelSpan();
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += (Math.Abs(x[i].R - y[i].R) / (y[i].R + 0.01)) + (Math.Abs(x[i].G - y[i].G) / (y[i].G + 0.01)) + (Math.Abs(x[i].B - y[i].B) / (y[i].B + 0.01));
        }

        return sum / x.Length;
    }

    [Test]
    public async Task ToGainMapImage_AcceptsASixteenBitBase()
    {
        HdrImage hdr = Hdr(W, H, 63);
        using Image sdr = hdr.ToneMap(new ToneMappingOptions { PixelType = PixelType.Rgba64 });
        using Image image = hdr.ToGainMapImage(sdr);
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(Pixels16(image)).IsEquivalentTo(Pixels16(sdr), CollectionOrdering.Matching);
        await Assert.That(image.HasHdr).IsTrue();
        using Image generated = hdr.ToGainMapImage(new GainMapOptions { ToneMapping = new ToneMappingOptions { PixelType = PixelType.Rgba64 } });
        await Assert.That(image.AttachedGainMap!.EditedMap!.Value.Pixels.Rgba32!)
            .IsEquivalentTo(generated.AttachedGainMap!.EditedMap!.Value.Pixels.Rgba32!, CollectionOrdering.Matching);
    }

    /// <summary>Exact widening keeps the map; rounding the base to 8 bits changes the samples it describes and drops it.</summary>
    [Test]
    public async Task ConvertPixelType_KeepsTheMapOnlyWhenWidening()
    {
        HdrImage hdr = Hdr(W, H, 64);
        using Image eight = hdr.ToGainMapImage();
        using Image widened = eight.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        await Assert.That(widened.HasHdr).IsTrue();
        using Image rounded = widened.Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        await Assert.That(rounded.HasHdr).IsFalse();
    }

    // ------------------------------------------------------------ 16-bit maps from plugins

    private static readonly ImageConfiguration WithWideMaps =
        ImageConfiguration.Default.ToBuilder().AddFormat(new WideGainMapFormat()).Build();

    private static DecoderOptions Hdr16(long maxPixels = long.MaxValue) =>
        new() { LoadGainMap = true, PreserveBitDepth = true, Configuration = WithWideMaps, MaxPixels = maxPixels };

    [Test]
    public async Task PluginMap_SixteenBits_IsChargedAtItsDeclaredType()
    {
        byte[] data = WideGainMapFormat.Create(16, 12, 8, 6);
        long required = (16 * 12 * 2) + (8 * 6 * 2);
        using (Image image = Image.Load(data, Hdr16(required)))
        {
            await Assert.That(image.HasHdr).IsTrue();
        }

        using Image over = Image.Load(data, Hdr16(required - 1));
        await Assert.That(over.HasHdr).IsFalse();
        await Assert.That(over.GainMap!.UnsupportedReason).Contains("MaxPixels");
    }

    /// <summary>A 16-bit map stays 16-bit through quarter turns (exactly), crops and resizes.</summary>
    [Test]
    public async Task PluginMap_SixteenBits_KeepsItsPrecisionThroughGeometry()
    {
        using Image image = Image.Load(WideGainMapFormat.Create(16, 12, 16, 12), Hdr16());
        using Image turned = image.Clone(x => x.Rotate(RotateMode.Rotate90));
        DecodedGainMap map = turned.AttachedGainMap!.EditedMap!.Value;
        await Assert.That(map.PixelType).IsEqualTo(PixelType.Rgba64);
        Rgba64[] stored = WideGainMapFormat.Map(16, 12);
        // Rotate90 clockwise: the new pixel (x, y) is the old (y, height − 1 − x).
        await Assert.That(map.Pixels.Rgba64![(3 * 12) + 2]).IsEqualTo(stored[((12 - 1 - 2) * 16) + 3]);
        await Assert.That(stored.Any(p => p.R % 257 != 0)).IsTrue();
        using Image cropped = image.Clone(x => x.Crop(new Rectangle(3, 2, 9, 7)).Resize(5, 4));
        await Assert.That(cropped.AttachedGainMap!.EditedMap!.Value.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(cropped.HasHdr).IsTrue();
        _ = cropped.ToHdr();
    }

    /// <summary>The 16-bit map is sampled at its precision: the reconstruction uses the codes divided by 65535.</summary>
    [Test]
    public async Task PluginMap_SixteenBits_ReconstructsAtItsPrecision()
    {
        using Image image = Image.Load(WideGainMapFormat.Create(16, 12, 16, 12), Hdr16());
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
        GainMapParameters parameters = image.AttachedGainMap!.Parameters!;
        Rgba128[] hdr = image.ToHdr().GetPixelSpan().ToArray();
        Rgba64[] stored = Pixels16(image), map = WideGainMapFormat.Map(16, 12);
        double worst = 0;
        for (int i = 0; i < stored.Length; i++)
        {
            double g = map[i].R / 65535.0;
            double factor = Math.Pow(2, (parameters.GainMapMin[0] * (1 - g)) + (parameters.GainMapMax[0] * g));
            double e = ((WideReference.Decode(stored[i].R / 65535.0) + parameters.BaseOffset[0]) * factor) - parameters.AlternateOffset[0];
            worst = Math.Max(worst, Math.Abs(hdr[i].R - e) / Math.Max(1, Math.Abs(e)));
        }

        Console.WriteLine($"ToHdr with a 16-bit map: worst relative error {worst:E2}");
        await Assert.That(worst).IsLessThan(2e-5);
    }

    [Test]
    public async Task PluginMap_OfAnotherTypeThanDeclared_IsCorrupt()
    {
        using Image image = Image.Load(WideGainMapFormat.Create(16, 12, 8, 6, mismatch: true), Hdr16());
        await Assert.That(image.HasHdr).IsTrue();
        await Assert.That(() => image.ToHdr()).Throws<ImageFormatException>();
    }

    /// <summary>JPEG stores 8-bit map codes: a 16-bit map is rounded when the image is saved, and the HDR survives.</summary>
    [Test]
    public async Task PluginMap_SixteenBits_IsRoundedForJpeg()
    {
        using Image image = Image.Load(WideGainMapFormat.Create(16, 12, 16, 12), Hdr16());
        byte[] jpeg = image.ToJpeg(new JpegEncoderOptions { GainMap = JpegGainMapMode.Require, Quality = 100, GainMapQuality = 100 });
        using Image reloaded = Image.Load(jpeg, new DecoderOptions { LoadGainMap = true });
        await Assert.That(reloaded.HasHdr).IsTrue();
    }

    /// <summary>
    /// A plugin whose base and gain map are 16-bit (procedural samples between 8-bit levels), with ISO 21496-1 metadata
    /// of one channel; the map declares <see cref="PixelType.Rgba64"/> and may decode to another type on request.
    /// </summary>
    private sealed class WideGainMapFormat : IImageFormatModule, IImageFormatDecoder
    {
        private static readonly byte[] Iso = GainMapMetadata.From(new GainMapParameters
        {
            Kind = HdrGainMapKinds.Iso21496,
            ChannelCount = 1,
            GainMapMin = [-0.5f, -0.5f, -0.5f],
            GainMapMax = [2.5f, 2.5f, 2.5f],
            BaseOffset = [1 / 64f, 1 / 64f, 1 / 64f],
            AlternateOffset = [1 / 64f, 1 / 64f, 1 / 64f],
            AlternateHeadroom = 2.5f,
        }).ToIso();

        public static ImageFormat Format { get; } = ImageFormat.Create("WMAP16", "image/x-wide-gain-map", ".wmap16");

        ImageFormat IImageFormatModule.Format => Format;

        public int HeaderSize => 6;

        public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("WMAP16"u8);

        public IImageFormatDecoder Decoder => this;

        public static byte[] Create(int width, int height, int mapWidth, int mapHeight, bool mismatch = false) =>
            [.. "WMAP16"u8, (byte)width, (byte)height, (byte)mapWidth, (byte)mapHeight, mismatch ? (byte)1 : (byte)0];

        public static Rgba64[] Base(int width, int height) => HighBitDepthProcessingTests.Pixels(width, height, Alpha.Opaque, 57);

        public static Rgba64[] Map(int width, int height)
        {
            var map = new Rgba64[width * height];
            for (int i = 0; i < map.Length; i++)
            {
                ushort v = (ushort)((i * 4099) % 65536);
                map[i] = new Rgba64(v, v, v);
            }

            return map;
        }

        public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
            new() { Format = Format, Width = data.Span[6], Height = data.Span[7], Color = TestColor.Rgb8, Compression = ImageCompression.Lossless };

        public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
        {
            ReadOnlySpan<byte> header = data.Span;
            int width = header[6], height = header[7];
            context.ReservePixels(width, height, PixelType.Rgba64);
            Image image = Image.LoadPixelData<Rgba64>(Base(width, height), width, height);
            context.AttachGainMap(GainMapMetadata.ParseIso(Iso, 1), new WideMap(new Size(header[8], header[9]), header[10] == 1));
            return image;
        }

        private sealed class WideMap(Size size, bool mismatch) : GainMapImage(size, PixelType.Rgba64)
        {
            public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken)
            {
                Image map = Image.LoadPixelData<Rgba64>(Map(Size.Width, Size.Height), Size.Width, Size.Height);
                if (mismatch)
                {
                    map.Mutate(x => x.ConvertPixelType(PixelType.Rgba32));
                }

                return map;
            }
        }
    }
}
