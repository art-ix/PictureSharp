using System.Text.Json;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Hdr;
using PictureSharp.Formats;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Hdr;

/// <summary>Deterministic HDR test content shared by the gain map generation tests.</summary>
internal static class HdrSynthetic
{
    /// <summary>
    /// Smooth content, 384×256: a hue and saturation field over luminance 0.004–1, soft highlights up to 8× and one
    /// hard edge; components below 0 do not occur.
    /// </summary>
    public static HdrImage Smooth(HdrColorSpace space, int width = 384, int height = 256)
    {
        var pixels = new Rgba128[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double u = x / (double)width, v = y / (double)height;
                double lum = 0.004 * Math.Pow(250, u);
                double hue = 2 * Math.PI * v, sat = 0.6 * v;
                double r = lum * (1 + (sat * Math.Cos(hue))), g = lum * (1 + (sat * Math.Cos(hue - 2.094))), b = lum * (1 + (sat * Math.Cos(hue + 2.094)));
                double boost = 1;
                foreach ((double cx, double cy, double rad, double peak) in new[] { (0.3, 0.3, 0.08, 8.0), (0.75, 0.6, 0.12, 4.0), (0.5, 0.85, 0.05, 6.0) })
                {
                    double d2 = (((u - cx) * (u - cx)) + ((v - cy) * (v - cy) * 0.44)) / (rad * rad);
                    boost += (peak - 1) * Math.Exp(-d2) / Math.Max(lum, 0.05) * 0.5;
                }

                if (x >= width * 300 / 384 && y < height / 4)
                {
                    boost = 3;   // hard edge
                }

                pixels[(y * width) + x] = new Rgba128((float)(r * boost), (float)(g * boost), (float)(b * boost));
            }
        }

        return new HdrImage(width, height, pixels, space);
    }

    /// <summary>The R, G and B components of the pixels.</summary>
    public static float[] Components(HdrImage hdr) => [.. hdr.GetPixelSpan().ToArray().SelectMany(p => new[] { p.R, p.G, p.B })];
}

/// <summary>
/// <c>hdr.ToGainMapImage()</c> (0.8.1): the attached map, its options and failures, the error budget of
/// tests/assets/hdr_reference.json ("generator") on synthetic content, and the public <see cref="HdrImage"/>
/// constructor.
/// </summary>
public class HdrGainMapGeneratorTests
{
    internal static JsonElement Budget => HdrGainMapTests.Manifest.GetProperty("generator");

    private static readonly DecoderOptions Hdr = new() { LoadGainMap = true };
    private static readonly JpegEncoderOptions Require = new() { Quality = 95, Subsampling = JpegSubsampling.Ratio444, GainMap = JpegGainMapMode.Require };

    public static IEnumerable<HdrColorSpace> Spaces() => [HdrColorSpace.LinearSrgb, HdrColorSpace.LinearDisplayP3];

    [Test]
    public async Task Constructor_CopiesPixels_AndComputesTheHeadroom()
    {
        Rgba128[] pixels = [new(0.5f, 1, 2), new(-0.1f, 3.5f, 0, 0.5f), new(0, 0, 0), new(1, 1, 1)];
        var metadata = new ImageMetadata { IccProfile = [1, 2, 3] };
        using var hdr = new HdrImage(2, 2, pixels, HdrColorSpace.LinearDisplayP3, metadata: metadata);
        pixels[0] = default;
        metadata.IccProfile = null;
        await Assert.That(hdr[0, 0]).IsEqualTo(new Rgba128(0.5f, 1, 2));
        await Assert.That(hdr[1, 0]).IsEqualTo(new Rgba128(-0.1f, 3.5f, 0, 0.5f));
        await Assert.That(hdr.Headroom).IsEqualTo(3.5f);
        await Assert.That(hdr.ColorSpace).IsEqualTo(HdrColorSpace.LinearDisplayP3);
        await Assert.That(hdr.GainMapKind).IsEqualTo(HdrGainMapKinds.None);
        await Assert.That(hdr.Metadata.IccProfile).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);

        using var dark = new HdrImage(1, 1, [new Rgba128(0.2f, 0.2f, 0.2f)], HdrColorSpace.LinearSrgb);
        await Assert.That(dark.Headroom).IsEqualTo(1f);
        using var given = new HdrImage(1, 1, [new Rgba128(0.2f, 0.2f, 0.2f)], HdrColorSpace.LinearSrgb, headroom: 6);
        await Assert.That(given.Headroom).IsEqualTo(6f);
        await Assert.That(given.Metadata).IsNotNull();
    }

    [Test]
    public async Task Constructor_ValidatesItsArguments()
    {
        Rgba128[] one = [new(1, 1, 1)];
        await Assert.That(() => new HdrImage(0, 1, one, HdrColorSpace.LinearSrgb)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HdrImage(1, -1, one, HdrColorSpace.LinearSrgb)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HdrImage(1, 2, one, HdrColorSpace.LinearSrgb)).Throws<ArgumentException>();
        await Assert.That(() => new HdrImage(1, 1, one, (HdrColorSpace)7)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HdrImage(1, 1, one, HdrColorSpace.LinearSrgb, headroom: 0.5f)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HdrImage(1, 1, one, HdrColorSpace.LinearSrgb, headroom: float.PositiveInfinity)).Throws<ArgumentOutOfRangeException>();
        foreach (Rgba128 bad in new[] { new Rgba128(float.NaN, 0, 0), new Rgba128(0, float.PositiveInfinity, 0), new Rgba128(0, 0, 0, float.NaN) })
        {
            await Assert.That(() => new HdrImage(1, 1, [bad], HdrColorSpace.LinearSrgb)).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Options_Validate()
    {
        await Assert.That(() => new GainMapOptions { Scale = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GainMapOptions { Scale = 129 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GainMapOptions { ChannelCount = 2 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GainMapOptions { Gamma = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GainMapOptions { Gamma = float.NaN }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GainMapOptions { Headroom = 0.99f }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GainMapOptions { Headroom = float.NaN }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GainMapOptions { MaxDegreeOfParallelism = 0 }).Throws<ArgumentOutOfRangeException>();
        GainMapOptions defaults = GainMapOptions.Default;
        await Assert.That((defaults.Scale, defaults.ChannelCount, defaults.Gamma, defaults.Headroom, defaults.MaxDegreeOfParallelism)).IsEqualTo((1, 3, 1f, (float?)null, -1));
    }

    [Test]
    [MethodDataSource(nameof(Spaces))]
    public async Task ToneMappedBase_CarriesAnIsoMap_WithinTheBudget(HdrColorSpace space)
    {
        using HdrImage hdr = HdrSynthetic.Smooth(space);
        float[] expected = HdrSynthetic.Components(hdr);
        using Image image = hdr.ToGainMapImage();
        using Image toneMapped = hdr.ToneMap();
        await Assert.That(image.GetPixelSpan().ToArray()).IsEquivalentTo(toneMapped.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        await Assert.That(image.HasHdr).IsTrue();
        HdrInfo info = image.Hdr!;
        await Assert.That((info.Detected, info.Selected, info.GainMapSize, info.Headroom)).IsEqualTo((HdrGainMapKinds.Iso21496, HdrGainMapKinds.Iso21496, hdr.Size, (float?)hdr.Headroom));
        GainMapParameters p = image.GainMap!.Parameters!;
        await Assert.That((p.ChannelCount, p.BaseHeadroom, p.MultiplicationSpace, p.UseBaseColorSpace))
            .IsEqualTo((3, 0f, (HdrColorSpace?)space, space == HdrColorSpace.LinearSrgb));
        await Assert.That((double)p.AlternateHeadroom).IsEqualTo(Math.Log2(hdr.Headroom)).Within(1e-6);

        // Without compression: the map alone.
        using (HdrImage direct = image.ToHdr(new HdrOptions { ColorSpace = space }))
        {
            HdrUltraHdrOracleTests.Stats s = HdrUltraHdrOracleTests.Measure(direct.GetPixelSpan(), expected, 0, 0);
            await Assert.That(s.MedianRelative).IsLessThanOrEqualTo(Budget.GetProperty("synthetic_map_median_relative").GetDouble()).Because(s.ToString());
            await Assert.That(s.P99Relative).IsLessThanOrEqualTo(Budget.GetProperty("synthetic_map_p99_relative").GetDouble()).Because(s.ToString());
        }

        // Through JPEG: base q95 4:4:4, map q95.
        using Image reloaded = Image.Load(image.ToJpeg(Require), Hdr);
        await Assert.That(reloaded.Hdr!.Detected).IsEqualTo(HdrGainMapKinds.Iso21496);
        await Assert.That(reloaded.GainMap!.Parameters!.MultiplicationSpace).IsEqualTo(space);
        using (HdrImage back = reloaded.ToHdr(new HdrOptions { ColorSpace = space }))
        {
            HdrUltraHdrOracleTests.Stats s = HdrUltraHdrOracleTests.Measure(back.GetPixelSpan(), expected, 0, 0);
            await Assert.That(s.MedianRelative).IsLessThanOrEqualTo(Budget.GetProperty("synthetic_median_relative").GetDouble()).Because(s.ToString());
            await Assert.That(s.P99Relative).IsLessThanOrEqualTo(Budget.GetProperty("synthetic_p99_relative").GetDouble()).Because(s.ToString());
        }
    }

    [Test]
    public async Task GivenBase_IsCopied_AndItsColorSpaceIsUsed()
    {
        // A base with an attached map and a Display P3 profile: its pixels are read as P3 and its map is replaced.
        using HdrImage hdr = HdrSynthetic.Smooth(HdrColorSpace.LinearDisplayP3, 96, 64);
        using Image source = hdr.ToGainMapImage(new GainMapOptions { Scale = 4 });
        source.Metadata.IccProfile = IccProfileWriter.WriteSrgbTransfer(HdrColorSpace.LinearDisplayP3);
        GainMapAttachment? before = source.GainMap;
        using Image image = hdr.ToGainMapImage(source);
        await Assert.That(source.GainMap).IsSameReferenceAs(before);
        await Assert.That(image.Hdr!.GainMapSize).IsEqualTo(hdr.Size);
        await Assert.That(image.GetPixelSpan().ToArray()).IsEquivalentTo(source.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        GainMapAttachment attachment = image.GainMap!;
        await Assert.That((attachment.BaseSpace, attachment.Parameters!.UseBaseColorSpace, attachment.Parameters.MultiplicationSpace))
            .IsEqualTo((HdrColorSpace.LinearDisplayP3, true, (HdrColorSpace?)HdrColorSpace.LinearDisplayP3));
        using HdrImage back = image.ToHdr();
        await Assert.That(back.ColorSpace).IsEqualTo(HdrColorSpace.LinearDisplayP3);
        HdrUltraHdrOracleTests.Stats s = HdrUltraHdrOracleTests.Measure(back.GetPixelSpan(), HdrSynthetic.Components(hdr), 0, 0);
        await Assert.That(s.MedianRelative).IsLessThanOrEqualTo(Budget.GetProperty("synthetic_map_median_relative").GetDouble()).Because(s.ToString());
        await Assert.That(s.P99Relative).IsLessThanOrEqualTo(Budget.GetProperty("synthetic_map_p99_relative").GetDouble()).Because(s.ToString());
    }

    [Test]
    public async Task GivenBase_MustMatch()
    {
        using HdrImage hdr = HdrSynthetic.Smooth(HdrColorSpace.LinearSrgb, 48, 32);
        using Image small = new(47, 32);
        await Assert.That(() => hdr.ToGainMapImage(small)).Throws<ArgumentException>();
        using Image animated = new(48, 32);
        animated.Frames.Add(new ImageFrame(48, 32));
        await Assert.That(() => hdr.ToGainMapImage(animated)).Throws<ArgumentException>();
        using Image adobe = Image.Load(HdrGainMapTests.File("base_adobe_rgb.jpg"));
        using HdrImage sized = HdrSynthetic.Smooth(HdrColorSpace.LinearSrgb, adobe.Width, adobe.Height);
        await Assert.That(() => sized.ToGainMapImage(adobe)).Throws<NotSupportedException>();
        await Assert.That(() => hdr.ToGainMapImage((Image)null!)).Throws<ArgumentNullException>();
    }

    [Test]
    [Arguments(2, 1)]
    [Arguments(3, 3)]
    [Arguments(7, 1)]
    public async Task Scale_And_Channels_ShapeTheMap(int scale, int channels)
    {
        using HdrImage hdr = HdrSynthetic.Smooth(HdrColorSpace.LinearSrgb, 50, 31);
        using Image image = hdr.ToGainMapImage(new GainMapOptions { Scale = scale, ChannelCount = channels, Gamma = 2 });
        DecodedGainMap map = image.GainMap!.EditedMap!.Value;
        await Assert.That((map.Width, map.Height)).IsEqualTo((50 / scale, 31 / scale));
        await Assert.That(image.GainMap.Parameters!.ChannelCount).IsEqualTo(channels);
        await Assert.That(image.GainMap.Parameters.Gamma).IsEquivalentTo(new[] { 2f, 2f, 2f }, CollectionOrdering.Matching);
        await Assert.That(map.Pixels.All(p => p.A == 255 && (channels == 3 || (p.R == p.G && p.G == p.B)))).IsTrue();
        using Image reloaded = Image.Load(image.ToJpeg(Require), Hdr);
        await Assert.That(reloaded.Hdr!.GainMapSize).IsEqualTo(new System.Drawing.Size(map.Width, map.Height));
    }

    [Test]
    public async Task ScaleLeavingNoMapPixel_FallsBackAsLibUltraHdr()
    {
        using HdrImage hdr = HdrSynthetic.Smooth(HdrColorSpace.LinearSrgb, 100, 20);
        using Image image = hdr.ToGainMapImage(new GainMapOptions { Scale = 64 });
        await Assert.That(image.Hdr!.GainMapSize).IsEqualTo(new System.Drawing.Size(100 / 2, 20 / 2));   // 20 / 8 = 2
        using HdrImage tiny = HdrSynthetic.Smooth(HdrColorSpace.LinearSrgb, 9, 5);
        using Image one = tiny.ToGainMapImage(new GainMapOptions { Scale = 16 });
        await Assert.That(one.Hdr!.GainMapSize).IsEqualTo(new System.Drawing.Size(9, 5));
    }

    [Test]
    public async Task HeadroomOption_SetsTheAlternateHeadroom()
    {
        using HdrImage hdr = HdrSynthetic.Smooth(HdrColorSpace.LinearSrgb, 48, 32);
        using Image image = hdr.ToGainMapImage(new GainMapOptions { Headroom = 16 });
        await Assert.That(image.Hdr!.Headroom).IsEqualTo(16f);
        await Assert.That(image.GainMap!.Parameters!.AlternateHeadroom).IsEqualTo(4f);
        using Image reloaded = Image.Load(image.ToJpeg(Require), Hdr);
        await Assert.That(reloaded.Hdr!.Headroom!.Value).IsEqualTo(16f).Within(1e-4f);
    }

    [Test]
    public async Task GeneratedMap_FollowsGeometricEdits_AndTheEncoderApi()
    {
        using HdrImage hdr = HdrSynthetic.Smooth(HdrColorSpace.LinearSrgb, 96, 64);
        using Image image = hdr.ToGainMapImage(new GainMapOptions { Scale = 2 });
        using (Image clone = image.Clone())
        {
            clone.Mutate(x => x.Rotate(RotateMode.Rotate90).Resize(32, 48));
            await Assert.That(clone.HasHdr).IsTrue();
            await Assert.That(clone.Hdr!.GainMapSize).IsEqualTo(new System.Drawing.Size(16, 24));
            using HdrImage rotated = clone.ToHdr();
            await Assert.That(rotated.Size).IsEqualTo(new System.Drawing.Size(32, 48));
            using Image reloaded = Image.Load(clone.ToJpeg(Require), Hdr);
            await Assert.That(reloaded.HasHdr).IsTrue();
        }

        EncodableGainMap gainMap = image.GetGainMap(require: true)!;
        await Assert.That((gainMap.Metadata.Kind, gainMap.Metadata.ChannelCount, gainMap.Map.Size)).IsEqualTo((HdrGainMapKinds.Iso21496, 3, new System.Drawing.Size(48, 32)));
        await Assert.That(gainMap.Metadata.ToIso().Length).IsGreaterThan(0);
        using Image map = gainMap.Map.Decode(-1, default);
        await Assert.That(map.GetPixelSpan().ToArray()).IsEquivalentTo(image.GainMap!.EditedMap!.Value.Pixels, CollectionOrdering.Matching);
        image.Mutate(x => x.Grayscale());
        await Assert.That(image.HasHdr).IsFalse();
    }

    [Test]
    public async Task CanceledOrDisposed_Throws()
    {
        using HdrImage hdr = HdrSynthetic.Smooth(HdrColorSpace.LinearSrgb, 48, 32);
        await Assert.That(() => hdr.ToGainMapImage(cancellationToken: new CancellationToken(canceled: true))).Throws<OperationCanceledException>();
        hdr.Dispose();
        await Assert.That(() => hdr.ToGainMapImage()).Throws<ObjectDisposedException>();
        await Assert.That(() => ((HdrImage)null!).ToGainMapImage()).Throws<ArgumentNullException>();
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task Map_DoesNotDependOnTheThreadCount()
    {
        using HdrImage hdr = HdrSynthetic.Smooth(HdrColorSpace.LinearDisplayP3, 768, 512);
        using Image one = hdr.ToGainMapImage(new GainMapOptions { MaxDegreeOfParallelism = 1 });
        using Image many = hdr.ToGainMapImage(new GainMapOptions { MaxDegreeOfParallelism = 8, ChannelCount = 3 });
        await Assert.That(many.GainMap!.EditedMap!.Value.Pixels).IsEquivalentTo(one.GainMap!.EditedMap!.Value.Pixels, CollectionOrdering.Matching);
        await Assert.That(many.GainMap.Parameters!.GainMapMax).IsEquivalentTo(one.GainMap.Parameters!.GainMapMax, CollectionOrdering.Matching);
    }
}
