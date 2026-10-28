using System.Runtime.InteropServices;
using System.Text.Json;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Png;

/// <summary>
/// 16-bit PNG decoding and encoding (0.9.0 step 1). The corpus in tests/assets/corpus/png16 comes from
/// png16_reference.py: an independent writer with ImageMagick cross-checks; encoder output is read back with
/// <see cref="Png16Reader"/>, which shares no code with PictureSharp's decoder.
/// </summary>
public class Png16Tests
{
    private static readonly DecoderOptions Preserve = new() { PreserveBitDepth = true };

    private static readonly Lazy<Dictionary<string, string[]>> References = new(() =>
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Assets, "png16_reference.json")));
        return json.RootElement.GetProperty("files").EnumerateObject().ToDictionary(
            f => f.Name, f => f.Value.GetProperty("frames").EnumerateArray().Select(h => h.GetString()!).ToArray());
    });

    public static IEnumerable<string> Files() => References.Value.Keys.Order(StringComparer.Ordinal);

    private static string Hash(ImageFrame frame) => Corpus.Sha256(MemoryMarshal.AsBytes(frame.GetPixelSpan<Rgba64>()));

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Decode_WithPreserveBitDepth_MatchesIndependentReference(string file)
    {
        using Image image = Image.Load(Corpus.PathOf("png16/" + file), Preserve);
        string[] expected = References.Value[file];
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(image.FrameCount).IsEqualTo(expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            await Assert.That(Hash(image.Frames[i])).IsEqualTo(expected[i]);
        }
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Decode_ByDefault_RoundsThe16BitSamplesOnce(string file)
    {
        using Image wide = Image.Load(Corpus.PathOf("png16/" + file), Preserve);
        using Image narrow = Image.Load(Corpus.PathOf("png16/" + file));
        await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);
        if (file == "apng16.png")
        {
            return;   // composed at different depths; the 16-bit composition is checked against the reference above
        }

        Rgba32[] rounded = [.. wide.GetPixelSpan<Rgba64>().ToArray().Select(p => p.ToRgba32())];
        await Assert.That(narrow.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(rounded, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Decode_EightBitPng_WithPreserveBitDepth_StaysRgba32()
    {
        using Image source = Core.ImageTests.Pattern(5, 4);
        using Image image = Image.Load(source.ToPng(), Preserve);
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba32);
        await Assert.That(image.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(source.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Identify_DescribesSourceSamples_RegardlessOfPreserve()
    {
        ImageInfo info = Image.Identify(Corpus.PathOf("png16/rgba16.png"), Preserve);
        await Assert.That(info.Color.BitsPerSample).IsEqualTo(16);
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task FrameReader_ReturnsTheSameFrames(string file)
    {
        string[] expected = References.Value[file];
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(Corpus.PathOf("png16/" + file), Preserve);
        int index = 0;
        await foreach (ImageFrame frame in reader.ReadFramesAsync())
        {
            await Assert.That(frame.PixelType).IsEqualTo(PixelType.Rgba64);
            await Assert.That(Hash(frame)).IsEqualTo(expected[index++]);
        }

        await Assert.That(index).IsEqualTo(expected.Length);
    }

    public static IEnumerable<(PngColorType, bool, PngFilterStrategy)> EncodeCases()
    {
        PngColorType[] types = [PngColorType.Auto, PngColorType.Grayscale, PngColorType.GrayscaleAlpha, PngColorType.Rgb, PngColorType.Rgba];
        foreach (PngColorType type in types)
        {
            foreach (bool interlace in new[] { false, true })
            {
                yield return (type, interlace, PngFilterStrategy.Adaptive);
            }
        }

        foreach (PngFilterStrategy filter in Enum.GetValues<PngFilterStrategy>())
        {
            yield return (PngColorType.Rgba, false, filter);
        }
    }

    [Test]
    [MethodDataSource(nameof(EncodeCases))]
    public async Task Encode_Sixteen_IsReadByIndependentReader_AndRoundTrips(PngColorType colorType, bool interlace, PngFilterStrategy filter)
    {
        using Image image = Ramp(19, 9);
        var options = new PngEncoderOptions { BitDepth = PngBitDepth.Sixteen, ColorType = colorType, Interlace = interlace, FilterStrategy = filter };
        byte[] png = image.ToPng(options);

        Png16Reader.Result read = Png16Reader.Read(png);
        await Assert.That(read.BitDepth).IsEqualTo(16);
        Rgba64[] expected = Expected(image.GetPixelSpan<Rgba64>(), colorType);
        await Assert.That(read.Pixels).IsEquivalentTo(expected, CollectionOrdering.Matching);

        using Image decoded = Image.Load(png, Preserve);
        await Assert.That(decoded.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Encode_Auto_FullRamp_KeepsAll65536Levels()
    {
        var pixels = new Rgba64[65536];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba64((ushort)i, (ushort)(65535 - i), (ushort)(i ^ 0x5A5A), (ushort)((i * 7) & 0xFFFF));
        }

        using Image image = Image.LoadPixelData<Rgba64>(pixels, 256, 256);
        byte[] png = image.ToPng();
        await Assert.That(Png16Reader.Read(png).Pixels).IsEquivalentTo(pixels, CollectionOrdering.Matching);
        using Image decoded = Image.Load(png, Preserve);
        await Assert.That(decoded.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(pixels, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Encode_Auto_OnWidened8BitContent_WritesTheSameFileAsThe8BitImage()
    {
        using Image narrow = Core.ImageTests.Pattern(11, 6);
        using Image wide = narrow.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        byte[] expected = narrow.ToPng();
        byte[] actual = wide.ToPng();
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        using Image decoded = Image.Load(actual, Preserve);
        await Assert.That(decoded.PixelType).IsEqualTo(PixelType.Rgba32);
    }

    [Test]
    public async Task Encode_Auto_OnTrue16BitContent_DetectsGrayAndOpacity()
    {
        var pixels = new Rgba64[12];
        for (int i = 0; i < pixels.Length; i++)
        {
            ushort v = (ushort)((i * 4099) + 1);
            pixels[i] = new Rgba64(v, v, v);
        }

        using Image image = Image.LoadPixelData<Rgba64>(pixels, 4, 3);
        Png16Reader.Result read = Png16Reader.Read(image.ToPng());
        await Assert.That((read.BitDepth, read.ColorType)).IsEqualTo((16, 0));
        await Assert.That(read.Pixels).IsEquivalentTo(pixels, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Encode_Sixteen_Widens8BitImageExactly()
    {
        using Image image = Core.ImageTests.Pattern(7, 5);
        Png16Reader.Result read = Png16Reader.Read(image.ToPng(new PngEncoderOptions { BitDepth = PngBitDepth.Sixteen }));
        await Assert.That(read.BitDepth).IsEqualTo(16);
        Rgba64[] widened = [.. image.GetPixelSpan<Rgba32>().ToArray().Select(Rgba64.FromRgba32)];
        await Assert.That(read.Pixels).IsEquivalentTo(Expected(widened, PngColorType.Auto), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Encode_UpTo8_RoundsAndKeepsTheLayoutSelection()
    {
        using Image image = Ramp(9, 4);
        byte[] png = image.ToPng(new PngEncoderOptions { BitDepth = PngBitDepth.UpTo8 });
        using Image decoded = Image.Load(png, Preserve);
        await Assert.That(decoded.PixelType).IsEqualTo(PixelType.Rgba32);
        Rgba32[] rounded = [.. image.GetPixelSpan<Rgba64>().ToArray().Select(p => p.ToRgba32())];
        await Assert.That(decoded.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(rounded, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Encode_PaletteAtSixteenBits_FailsBeforeWriting()
    {
        using Image image = Ramp(4, 4);
        using var output = new MemoryStream();
        Assert.ThrowsExactly<ArgumentException>(() => image.Save(output, new PngEncoderOptions { ColorType = PngColorType.Palette }));
        Assert.ThrowsExactly<ArgumentException>(() => image.Save(output, new PngEncoderOptions { ColorType = PngColorType.Palette, BitDepth = PngBitDepth.Sixteen }));
        await Assert.That(output.Length).IsEqualTo(0);
        image.Save(output, new PngEncoderOptions { ColorType = PngColorType.Palette, BitDepth = PngBitDepth.UpTo8 });
        await Assert.That(output.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Encode_Animation_KeepsEveryFrameAt16Bits()
    {
        using Image image = Ramp(6, 5);
        using Image second = Ramp(6, 5, seed: 3);
        image.Frames.Add(second, TimeSpan.FromMilliseconds(40));
        byte[] png = image.ToPng();
        using Image decoded = Image.Load(png, Preserve);
        await Assert.That(decoded.FrameCount).IsEqualTo(2);
        for (int i = 0; i < 2; i++)
        {
            await Assert.That(decoded.Frames[i].GetPixelSpan<Rgba64>().ToArray())
                .IsEquivalentTo(image.Frames[i].GetPixelSpan<Rgba64>().ToArray(), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task FrameWriter_Writes16BitFrames()
    {
        using Image first = Ramp(6, 5);
        using Image second = Ramp(6, 5, seed: 9);
        using var output = new MemoryStream();
        await using (ImageFrameWriter writer = Image.CreateFrameWriter(output, new System.Drawing.Size(6, 5), new PngEncoderOptions(),
            new ImageFrameWriterOptions { FrameCount = 2 }))
        {
            await writer.WriteAsync(first.Frames[0]);
            await writer.WriteAsync(second.Frames[0]);
            await writer.CompleteAsync();
        }

        using Image decoded = Image.Load(output.ToArray(), Preserve);
        await Assert.That(decoded.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(decoded.Frames[1].GetPixelSpan<Rgba64>().ToArray())
            .IsEquivalentTo(second.GetPixelSpan<Rgba64>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task OtherEncoders_Round16BitImagesWithoutChangingTheSource()
    {
        using Image image = Ramp(8, 6);
        Rgba64[] before = image.GetPixelSpan<Rgba64>().ToArray();
        using Image rounded = image.Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        foreach (EncoderOptions options in new EncoderOptions[] { new Formats.Bmp.BmpEncoderOptions(), new Formats.WebP.WebPEncoderOptions { Lossless = true } })
        {
            using Image decoded = Image.Load(image.Encode(options));
            await Assert.That(decoded.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(rounded.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        }

        using Image ico = Image.Load(image.ToIco());
        await Assert.That(ico.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(rounded.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        await Assert.That(image.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
    }

    /// <summary>A 16-bit image whose samples are not exact 8-bit values, with partial alpha.</summary>
    internal static Image Ramp(int width, int height, int seed = 0)
    {
        var pixels = new Rgba64[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            int v = (i * 2741) + (seed * 977) + 1;
            pixels[i] = new Rgba64((ushort)v, (ushort)(v * 3), (ushort)(v * 7), (ushort)(65535 - (i * 97)));
        }

        return Image.LoadPixelData<Rgba64>(pixels, width, height);
    }

    /// <summary>The pixels a 16-bit PNG of <paramref name="colorType"/> stores, as straight RGBA.</summary>
    private static Rgba64[] Expected(ReadOnlySpan<Rgba64> pixels, PngColorType colorType)
    {
        var result = new Rgba64[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba64 p = pixels[i];
            ushort gray = p.R == p.G && p.G == p.B ? p.R : (ushort)(((19595u * p.R) + (38470u * p.G) + (7471u * p.B) + 32768u) >> 16);
            result[i] = colorType switch
            {
                PngColorType.Grayscale => new Rgba64(gray, gray, gray),
                PngColorType.GrayscaleAlpha => new Rgba64(gray, gray, gray, p.A),
                PngColorType.Rgb => new Rgba64(p.R, p.G, p.B),
                _ => p,
            };
        }

        return result;
    }
}
