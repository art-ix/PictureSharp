using System.Text;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Hdr;

/// <summary>
/// Private iPhone photos in samples/iphone/ (not committed): every JPEG declares both an ISO 21496-1 and an Apple gain
/// map. Produces no cases without the samples.
/// </summary>
[Property("Category", "Local")]
public class HdrSamplesTests
{
    public static IEnumerable<string> Samples()
    {
        string dir = Path.Combine(Corpus.SamplesDir, "iphone");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => Path.GetExtension(f).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
            : [];
    }

    [Test]
    [MethodDataSource(nameof(Samples))]
    public async Task IphoneJpeg_ReconstructsHdr(string path)
    {
        ImageInfo info = Image.Identify(path, new DecoderOptions { LoadGainMap = true });
        await Assert.That(info.GainMap!.Detected).IsEqualTo(HdrGainMapKinds.Iso21496 | HdrGainMapKinds.Apple);
        await Assert.That(info.GainMap.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
        await Assert.That(info.GainMap.GainMapSize).IsEqualTo(new System.Drawing.Size(info.Width / 2, info.Height / 2));

        using Image image = Image.Load(path, new DecoderOptions { LoadGainMap = true, ApplyExifOrientation = true, TargetSize = new(512, 512) });
        using HdrImage hdr = image.ToHdr();
        await Assert.That(hdr.ColorSpace).IsEqualTo(HdrColorSpace.LinearDisplayP3);
        float max = 0;
        foreach (Rgba128 p in hdr.GetPixelSpan())
        {
            max = Math.Max(max, Math.Max(p.R, Math.Max(p.G, p.B)));
        }

        await Assert.That(max).IsGreaterThan(1.5f);
        await Assert.That(max).IsLessThanOrEqualTo(hdr.Headroom * 1.001f);
    }

    [Test]
    [MethodDataSource(nameof(Samples))]
    public async Task AppleHeadroom_MatchesIsoHeadroom(string path)
    {
        // Hiding the ISO segments leaves the Apple model, whose headroom comes from MakerNote tags 33 and 48.
        byte[] data = File.ReadAllBytes(path);
        byte[] urn = Encoding.ASCII.GetBytes("urn:iso:std:iso:ts:21496:-1");
        for (int at = data.AsSpan().IndexOf(urn); at >= 0; at = data.AsSpan().IndexOf(urn))
        {
            data[at] = (byte)'x';
        }

        GainMapInfo iso = Image.Identify(path, new DecoderOptions { LoadGainMap = true }).GainMap!;
        GainMapInfo apple = Image.Identify(data, new DecoderOptions { LoadGainMap = true }).GainMap!;
        await Assert.That(apple.Selected).IsEqualTo(HdrGainMapKinds.Apple);
        await Assert.That((double)apple.Headroom!.Value).IsEqualTo(iso.Headroom!.Value).Within(2e-3);

        using Image image = Image.Load(data, new DecoderOptions { LoadGainMap = true, TargetSize = new(256, 256) });
        using HdrImage hdr = image.ToHdr();
        await Assert.That(hdr.GainMapKind).IsEqualTo(HdrGainMapKinds.Apple);
    }

    public static IEnumerable<(string Path, HdrColorSpace Space)> SamplesBySpace() =>
        Samples().SelectMany(p => HdrGainMapGeneratorTests.Spaces().Select(s => (p, s)));

    /// <summary>
    /// hdr.ToGainMapImage() → JPEG (base q95 4:4:4, map q95) → ToHdr within the local "generator" budget; p99 leaves out
    /// components that are negative in the source (outside the gamut, clipped by the generator as in libultrahdr).
    /// </summary>
    [Test]
    [MethodDataSource(nameof(SamplesBySpace))]
    public async Task IphoneHdr_RoundTripsThroughAGeneratedMap(string path, HdrColorSpace space)
    {
        using Image source = Image.Load(path, new DecoderOptions { LoadGainMap = true, ApplyExifOrientation = true });
        using HdrImage hdr = source.ToHdr(new HdrOptions { ColorSpace = space });
        using Image image = hdr.ToGainMapImage();
        byte[] file = image.ToJpeg(new JpegEncoderOptions { Quality = 95, Subsampling = JpegSubsampling.Ratio444, GainMap = JpegGainMapMode.Require });
        using Image reloaded = Image.Load(file, new DecoderOptions { LoadGainMap = true });
        using HdrImage back = reloaded.ToHdr(new HdrOptions { ColorSpace = space });
        (double median, double p99) = Errors(back.GetPixelSpan(), hdr.GetPixelSpan());
        System.Text.Json.JsonElement budget = HdrGainMapGeneratorTests.Budget;
        double medianCap = budget.GetProperty(space == HdrColorSpace.LinearSrgb ? "local_median_relative_srgb" : "local_median_relative_p3").GetDouble();
        await Assert.That(median).IsLessThanOrEqualTo(medianCap).Because($"median {median:P2}, p99 {p99:P2}");
        await Assert.That(p99).IsLessThanOrEqualTo(budget.GetProperty("local_p99_relative").GetDouble()).Because($"median {median:P2}, p99 {p99:P2}");
    }

    /// <summary>
    /// The Apple map alone (ISO segments hidden) written as ISO 21496-1: with the source's base pixels, the
    /// reconstruction stays within the local "apple_map" budget at full headroom.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Samples))]
    public async Task AppleOnlyIphoneJpeg_IsWrittenAsIso(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        byte[] urn = Encoding.ASCII.GetBytes("urn:iso:std:iso:ts:21496:-1");
        for (int at = data.AsSpan().IndexOf(urn); at >= 0; at = data.AsSpan().IndexOf(urn))
        {
            data[at] = (byte)'x';
        }

        using Image apple = Image.Load(data, new DecoderOptions { LoadGainMap = true });
        await Assert.That(apple.GainMap!.Selected).IsEqualTo(HdrGainMapKinds.Apple);
        using HdrImage expected = apple.ToHdr();
        using Image reloaded = Image.Load(apple.ToJpeg(new JpegEncoderOptions { Quality = 95, GainMap = JpegGainMapMode.Preserve }), new DecoderOptions { LoadGainMap = true });
        await Assert.That(reloaded.GainMap!.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
        apple.GetPixelSpan().CopyTo(reloaded.GetPixelSpan());
        using HdrImage actual = reloaded.ToHdr();
        (double median, double p99) = Errors(actual.GetPixelSpan(), expected.GetPixelSpan());
        System.Text.Json.JsonElement budget = HdrGainMapGeneratorTests.Budget;
        await Assert.That(median).IsLessThanOrEqualTo(budget.GetProperty("local_apple_map_median_relative").GetDouble()).Because($"median {median:P2}, p99 {p99:P2}");
        await Assert.That(p99).IsLessThanOrEqualTo(budget.GetProperty("local_apple_map_p99_relative").GetDouble()).Because($"median {median:P2}, p99 {p99:P2}");
    }

    // Median over all components and p99 over the components that are not negative in the source, relative to
    // max(|expected|, 0.01) as in HdrUltraHdrOracleTests.Measure.
    private static (double Median, double P99) Errors(ReadOnlySpan<Rgba128> actual, ReadOnlySpan<Rgba128> expected)
    {
        var all = new List<double>(actual.Length * 3);
        var nonNegative = new List<double>(actual.Length * 3);
        for (int i = 0; i < actual.Length; i++)
        {
            ReadOnlySpan<float> a = [actual[i].R, actual[i].G, actual[i].B], e = [expected[i].R, expected[i].G, expected[i].B];
            for (int c = 0; c < 3; c++)
            {
                double r = Math.Abs(a[c] - e[c]) / Math.Max(Math.Abs(e[c]), 1e-2);
                all.Add(r);
                if (e[c] >= 0) nonNegative.Add(r);
            }
        }

        all.Sort();
        nonNegative.Sort();
        return (all[all.Count / 2], nonNegative[(int)(nonNegative.Count * 0.99)]);
    }
}
