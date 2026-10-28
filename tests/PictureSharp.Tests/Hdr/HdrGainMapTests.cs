using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Hdr;

/// <summary>
/// JPEG gain maps (0.6.5) against the double-precision model of tests/assets/hdr_reference.py: reconstruction,
/// headroom, orientation, tone mapping, damaged and unsupported files, and the attachment rules of <see cref="Image"/>.
/// </summary>
public class HdrGainMapTests
{
    internal static JsonElement Manifest { get; } = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(Corpus.Assets, "hdr_reference.json"))).RootElement;

    private static readonly DecoderOptions Hdr = new() { LoadGainMap = true, ApplyExifOrientation = true };

    public static IEnumerable<string> ValidCases() => Manifest.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!);

    public static IEnumerable<string> InvalidCases() => Manifest.GetProperty("invalid").EnumerateArray().Select(c => c.GetProperty("name").GetString()!);

    internal static string File(string name) => Corpus.PathOf($"hdr/{name}");

    private static JsonElement Case(string name) =>
        Manifest.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static float[] Expected(string name, string variant) =>
        MemoryMarshal.Cast<byte, float>(System.IO.File.ReadAllBytes(File($"{name}.{variant}.f32"))).ToArray();

    /// <summary>The worst |actual − expected| beyond the plan's tolerance (≤ 0 passes), with its pixel index.</summary>
    private static (double Excess, int Index) Compare(ReadOnlySpan<Rgba128> actual, float[] expected)
    {
        JsonElement tolerance = Manifest.GetProperty("tolerance");
        double absolute = tolerance.GetProperty("absolute").GetDouble(), relative = tolerance.GetProperty("relative").GetDouble();
        double worst = double.NegativeInfinity;
        int index = -1;
        for (int i = 0; i < actual.Length; i++)
        {
            ReadOnlySpan<float> a = [actual[i].R, actual[i].G, actual[i].B];
            for (int c = 0; c < 3; c++)
            {
                double e = expected[(i * 3) + c];
                double excess = Math.Abs(a[c] - e) - (absolute + (relative * Math.Abs(e)));
                if (excess > worst)
                {
                    (worst, index) = (excess, i);
                }
            }
        }

        return (worst, index);
    }

    [Test]
    [MethodDataSource(nameof(ValidCases))]
    public async Task Reconstruction_MatchesTheModel(string name)
    {
        JsonElement c = Case(name);
        using Image image = Image.Load(File(name + ".jpg"), Hdr);
        await Assert.That(image.HasHdr).IsTrue();
        GainMapInfo info = image.GainMap!;
        await Assert.That(info.CanDecode).IsTrue();
        await Assert.That(info.Selected).IsEqualTo(c.GetProperty("model").GetString() == "iso" ? HdrGainMapKinds.Iso21496 : HdrGainMapKinds.Apple);
        HdrGainMapKinds detected = c.GetProperty("detected").EnumerateArray().Aggregate(HdrGainMapKinds.None, (k, e) => k | Enum.Parse<HdrGainMapKinds>(e.GetString()!));
        await Assert.That(info.Detected).IsEqualTo(detected);
        await Assert.That(info.GainMapSize).IsEqualTo(new System.Drawing.Size(c.GetProperty("map_width").GetInt32(), c.GetProperty("map_height").GetInt32()));

        foreach ((string variant, HdrOptions options) in new[] { ("full", HdrOptions.Default), ("srgb_t2", new HdrOptions { TargetHeadroom = 2, ColorSpace = HdrColorSpace.LinearSrgb }) })
        {
            JsonElement v = c.GetProperty("variants").GetProperty(variant);
            using HdrImage hdr = image.ToHdr(options);
            await Assert.That(hdr.Size).IsEqualTo(new System.Drawing.Size(c.GetProperty("width").GetInt32(), c.GetProperty("height").GetInt32()));
            await Assert.That(hdr.ColorSpace).IsEqualTo(v.GetProperty("space").GetString() == "p3" ? HdrColorSpace.LinearDisplayP3 : HdrColorSpace.LinearSrgb);
            await Assert.That((double)hdr.Headroom).IsEqualTo(v.GetProperty("headroom").GetDouble()).Within(1e-5);
            (double excess, int index) = Compare(hdr.GetPixelSpan(), Expected(name, variant));
            await Assert.That(excess).IsLessThanOrEqualTo(0).Because($"{name}/{variant}: pixel {index % hdr.Width},{index / hdr.Width}");
        }
    }

    [Test]
    [MethodDataSource(nameof(ValidCases))]
    public async Task ToneMap_MatchesTheModel(string name)
    {
        using Image image = Image.Load(File(name + ".jpg"), Hdr);
        using HdrImage hdr = image.ToHdr();
        int levels = Manifest.GetProperty("tolerance").GetProperty("tone_map_levels").GetInt32();
        foreach ((string variant, ToneMappingOptions? options) in new[] { ("tonemap", (ToneMappingOptions?)null), ("tonemap_ev", new ToneMappingOptions { Exposure = -0.5f, WhitePoint = 1.5f }) })
        {
            using Image sdr = hdr.ToneMap(options);
            Rgba32[] expected = MemoryMarshal.Cast<byte, Rgba32>(System.IO.File.ReadAllBytes(File($"{name}.{variant}.rgba"))).ToArray();
            (int worst, _) = ColorManagement.IccConversionTests.Difference(sdr.GetPixelSpan<Rgba32>(), expected);
            await Assert.That(worst).IsLessThanOrEqualTo(levels).Because($"{name}/{variant}");
            await Assert.That(sdr.Metadata.IccProfile).IsNull();
            await Assert.That(sdr.HasHdr).IsFalse();
        }
    }

    [Test]
    [MethodDataSource(nameof(ValidCases))]
    public async Task BasePixels_DoNotDependOnLoadGainMap(string name)
    {
        using Image plain = Image.Load(File(name + ".jpg"), new DecoderOptions { ApplyExifOrientation = true });
        using Image withMap = Image.Load(File(name + ".jpg"), Hdr);
        await Assert.That(withMap.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(plain.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        await Assert.That(plain.GainMap).IsNull();
        await Assert.That(plain.HasHdr).IsFalse();
        await Assert.That(Image.Identify(File(name + ".jpg")).GainMap).IsNull();
        await Assert.That(Image.Identify(File(name + ".jpg"), Hdr).GainMap).IsEqualTo(withMap.GainMap);
    }

    [Test]
    [MethodDataSource(nameof(InvalidCases))]
    public async Task DamagedOrUnsupportedHdr_LoadsTheBaseAndThrowsInToHdr(string name)
    {
        string failure = Manifest.GetProperty("invalid").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name).GetProperty("failure").GetString()!;
        using Image plain = Image.Load(File(name + ".jpg"));
        using Image image = Image.Load(File(name + ".jpg"), new DecoderOptions { LoadGainMap = true });
        await Assert.That(image.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(plain.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        await Assert.That(image.HasHdr).IsFalse();
        await Assert.That(image.GainMap).IsNotNull();
        await Assert.That(image.GainMap!.CanDecode).IsFalse();
        await Assert.That(image.GainMap.Selected).IsEqualTo(HdrGainMapKinds.None);
        await Assert.That(image.GainMap.UnsupportedReason).IsNotNull();
        await Assert.That(Image.Identify(File(name + ".jpg"), new DecoderOptions { LoadGainMap = true }).GainMap).IsEqualTo(image.GainMap);
        if (failure == "unsupported")
        {
            Assert.ThrowsExactly<NotSupportedException>(() => image.ToHdr());
        }
        else
        {
            Assert.ThrowsExactly<ImageFormatException>(() => image.ToHdr());
        }
    }

    [Test]
    public async Task PlainMultiPictureFile_HasNoGainMap()
    {
        using Image image = Image.Load(File("plain_mpo.jpg"), new DecoderOptions { LoadGainMap = true });
        await Assert.That(image.GainMap).IsNull();
        Assert.ThrowsExactly<InvalidOperationException>(() => image.ToHdr());
    }

    [Test]
    public async Task OtherFormatsAndDefaultOptions_HaveNoGainMap()
    {
        using Image png = new(4, 4);
        await Assert.That(png.GainMap).IsNull();
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => png.ToHdr());
        await Assert.That(error.Message).Contains("LoadGainMap");
        using Image jpeg = Image.Load(File("iso_mono_srgb.jpg"));
        await Assert.That(jpeg.HasHdr).IsFalse();
        Assert.ThrowsExactly<InvalidOperationException>(() => jpeg.ToHdr());
    }

    [Test]
    public async Task Mutate_RemovesTheMap_CloneKeepsIt()
    {
        using Image image = Image.Load(File("iso_mono_srgb.jpg"), Hdr);
        using Image clone = image.Clone();
        using Image processed = image.Clone(x => x.Brightness(1.1f));
        await Assert.That(clone.HasHdr).IsTrue();
        await Assert.That(processed.GainMap).IsNull();
        using (HdrImage a = image.ToHdr())
        using (HdrImage b = clone.ToHdr())
        {
            await Assert.That(b.GetPixelSpan().ToArray()).IsEquivalentTo(a.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        }

        image.Mutate(x => x.Grayscale());
        await Assert.That(image.GainMap).IsNull();
        clone.Frames.Add(new ImageFrame(clone.Width, clone.Height));
        await Assert.That(clone.GainMap).IsNull();
    }

    [Test]
    public async Task HdrImage_OwnsItsPixels_AndDisposes()
    {
        Image image = Image.Load(File("iso_gamma_p3.jpg"), Hdr);
        HdrImage hdr = image.ToHdr();
        Rgba128 first = hdr[0, 0];
        image.Dispose();
        await Assert.That(hdr[0, 0]).IsEqualTo(first);
        await Assert.That(hdr.GetRowSpan(1).Length).IsEqualTo(hdr.Width);
        await Assert.That(hdr.Metadata.IccProfile).IsNotNull();
        var copy = new Rgba128[hdr.Width * hdr.Height];
        hdr.CopyPixelDataTo(copy);
        await Assert.That(copy).IsEquivalentTo(hdr.GetPixelMemory().ToArray(), CollectionOrdering.Matching);
        hdr.Dispose();
        hdr.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = hdr.GetPixelSpan().Length);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = hdr[0, 0]);
        Assert.ThrowsExactly<ObjectDisposedException>(() => image.ToHdr());
        await Assert.That(hdr.ToString()).Contains("disposed");
    }

    [Test]
    public async Task TargetHeadroomOne_GivesTheLinearizedBase()
    {
        using Image image = Image.Load(File("iso_mono_srgb.jpg"), Hdr);
        using HdrImage hdr = image.ToHdr(new HdrOptions { TargetHeadroom = 1 });
        await Assert.That(hdr.Headroom).IsEqualTo(1f);
        ReadOnlySpan<Rgba32> source = image.GetPixelSpan<Rgba32>();
        double worst = 0;
        for (int i = 0; i < source.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(hdr.GetPixelSpan()[i].G - Algorithms.ColorManagement.SrgbTransfer.Decode(source[i].G)));
        }

        await Assert.That(worst).IsLessThan(1e-6);
    }

    [Test]
    public async Task Options_AreValidated()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HdrOptions { TargetHeadroom = 0.99f });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HdrOptions { TargetHeadroom = float.NaN });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HdrOptions { ColorSpace = (HdrColorSpace)7 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ToneMappingOptions { Exposure = float.PositiveInfinity });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ToneMappingOptions { WhitePoint = 0.5f });
        await Assert.That(new HdrOptions { TargetHeadroom = 1 }.TargetHeadroom).IsEqualTo(1f);
    }

    [Test]
    public async Task Thumbnail_ReconstructsAtTheReducedSize()
    {
        var options = Hdr with { TargetSize = new System.Drawing.Size(24, 24) };
        using Image thumb = Image.Load(File("iso_orientation6.jpg"), options);
        using HdrImage small = thumb.ToHdr();
        using Image full = Image.Load(File("iso_orientation6.jpg"), Hdr);
        using HdrImage large = full.ToHdr();
        await Assert.That(small.Size).IsEqualTo(thumb.Size);
        await Assert.That(small.Headroom).IsEqualTo(large.Headroom);

        // Every thumbnail pixel lies near the average of the full-size pixels it covers.
        double scale = (double)large.Width / small.Width, total = 0;
        for (int y = 0; y < small.Height; y++)
        {
            for (int x = 0; x < small.Width; x++)
            {
                double sum = 0;
                int n = 0;
                for (int yy = (int)(y * scale); yy < (int)((y + 1) * scale); yy++)
                {
                    for (int xx = (int)(x * scale); xx < (int)((x + 1) * scale); xx++, n++)
                    {
                        sum += large[xx, yy].G;
                    }
                }

                total += Math.Abs(small[x, y].G - (sum / n));
            }
        }

        await Assert.That(total / (small.Width * small.Height)).IsLessThan(0.05);
    }

    [Test]
    public async Task ToSrgbAtLoad_StillReconstructs()
    {
        using Image image = Image.Load(File("iso_gamma_p3.jpg"), Hdr with { ColorConversion = ColorConversionMode.ToSrgb });
        using HdrImage hdr = image.ToHdr();
        await Assert.That(hdr.ColorSpace).IsEqualTo(HdrColorSpace.LinearSrgb);
        using Image reference = Image.Load(File("iso_gamma_p3.jpg"), Hdr);
        using HdrImage expected = reference.ToHdr(new HdrOptions { ColorSpace = HdrColorSpace.LinearSrgb });
        double mean = 0;
        for (int i = 0; i < hdr.GetPixelSpan().Length; i++)
        {
            mean += Math.Abs(hdr.GetPixelSpan()[i].G - Math.Clamp(expected.GetPixelSpan()[i].G, 0, float.MaxValue));
        }

        await Assert.That(mean / hdr.GetPixelSpan().Length).IsLessThan(0.02);
    }

    [Test]
    public async Task MapBeyondMaxPixels_FailsOnlyTheHdrPart()
    {
        using Image probe = Image.Load(File("iso_mono_srgb.jpg"));
        var options = new DecoderOptions { LoadGainMap = true, MaxPixels = (long)probe.Width * probe.Height };
        using Image image = Image.Load(File("iso_mono_srgb.jpg"), options);
        await Assert.That(image.HasHdr).IsFalse();
        await Assert.That(image.GainMap!.UnsupportedReason).Contains("MaxPixels");
        Assert.ThrowsExactly<ImageFormatException>(() => image.ToHdr());
    }

    [Test]
    public async Task CanceledToken_Throws()
    {
        using Image image = Image.Load(File("iso_rgb_p3.jpg"), Hdr);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => image.ToHdr(cancellationToken: cts.Token));
        using HdrImage hdr = image.ToHdr();
        Assert.Throws<OperationCanceledException>(() => hdr.ToneMap(cancellationToken: cts.Token));
    }

    [Test]
    public async Task Result_DoesNotDependOnThreadCount()
    {
        using Image image = Image.Load(File("iso_rgb_p3.jpg"), Hdr);
        using HdrImage serial = image.ToHdr(new HdrOptions { MaxDegreeOfParallelism = 1 });
        using HdrImage parallel = image.ToHdr(new HdrOptions { MaxDegreeOfParallelism = -1 });
        await Assert.That(parallel.GetPixelSpan().ToArray()).IsEquivalentTo(serial.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Rgba128_ConvertsToVector4()
    {
        var p = new Rgba128(1.5f, -0.25f, 0f);
        Vector4 v = p;
        await Assert.That(v).IsEqualTo(new Vector4(1.5f, -0.25f, 0f, 1f));
        await Assert.That((Rgba128)v).IsEqualTo(p);
        await Assert.That(p == new Rgba128(1.5f, -0.25f, 0f, 1f)).IsTrue();
        await Assert.That(Marshal.SizeOf<Rgba128>()).IsEqualTo(16);
    }

    [Test]
    public async Task CorruptedBytes_NeverThrowUndocumentedExceptions()
    {
        byte[] good = System.IO.File.ReadAllBytes(File("iso_and_apple.jpg"));
        var random = new Random(65);
        for (int i = 0; i < 300; i++)
        {
            byte[] data = (byte[])good.Clone();
            for (int k = 0; k < 4; k++)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            try
            {
                using Image image = Image.Load(data, new DecoderOptions { LoadGainMap = true });
                _ = Image.Identify(data, new DecoderOptions { LoadGainMap = true });
                if (image.HasHdr)
                {
                    using HdrImage hdr = image.ToHdr();
                    using Image sdr = hdr.ToneMap();
                    foreach (Rgba128 p in hdr.GetPixelSpan())
                    {
                        if (!float.IsFinite(p.R) || !float.IsFinite(p.G) || !float.IsFinite(p.B))
                        {
                            Assert.Fail($"Non-finite pixel at iteration {i}.");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }

        await Assert.That(good.Length).IsGreaterThan(0);
    }
}
