using PictureSharp.Domain.Hdr;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Hdr;

/// <summary>
/// JPEG with an ISO 21496-1 gain map written by <see cref="JpegEncoderOptions.GainMap"/> (0.6.9): container layout as
/// libultrahdr, copied metadata and map, edited maps, the modes and their failures.
/// </summary>
public class HdrGainMapEncoderTests
{
    private static readonly DecoderOptions Hdr = new() { LoadGainMap = true, ApplyExifOrientation = true };
    private static readonly JpegEncoderOptions Preserve = new() { GainMap = JpegGainMapMode.Preserve };
    private static readonly JpegEncoderOptions Require = new() { GainMap = JpegGainMapMode.Require };

    private static string File(string name) => HdrGainMapTests.File(name);

    public static IEnumerable<string> IsoCases() => HdrGainMapTests.Manifest.GetProperty("cases").EnumerateArray()
        .Where(c => c.GetProperty("model").GetString() == "iso").Select(c => c.GetProperty("name").GetString()!);

    internal static DecodedGainMap DecodeMap(GainMapAttachment attachment) =>
        attachment.EditedMap ?? attachment.Decoder!.Decode(attachment.MapData, 1, 1, CancellationToken.None);

    internal static (int Worst, int Count) Difference(DecodedGainMap a, DecodedGainMap b)
    {
        int worst = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(a.Pixels[i].R - b.Pixels[i].R), Math.Max(Math.Abs(a.Pixels[i].G - b.Pixels[i].G), Math.Abs(a.Pixels[i].B - b.Pixels[i].B))));
        }

        return (worst, a.Pixels.Length);
    }

    [Test]
    [MethodDataSource(nameof(IsoCases))]
    public async Task UneditedMap_RoundTrips_WithTheSameParametersMapAndBase(string name)
    {
        using Image image = Image.Load(File(name + ".jpg"), Hdr);
        byte[] hdr = image.ToJpeg(Preserve);
        using Image reloaded = Image.Load(hdr, Hdr);
        await Assert.That(reloaded.HasHdr).IsTrue();
        await Assert.That(reloaded.GainMap!.Detected).IsEqualTo(HdrGainMapKinds.Iso21496);   // no Apple or XMP declaration
        GainMapParameters a = image.AttachedGainMap!.Parameters!, b = reloaded.AttachedGainMap!.Parameters!;
        await Assert.That((b.Kind, b.ChannelCount, b.BaseHeadroom, b.AlternateHeadroom, b.MultiplicationSpace))
            .IsEqualTo((a.Kind, a.ChannelCount, a.BaseHeadroom, a.AlternateHeadroom, a.MultiplicationSpace));
        foreach ((float[] x, float[] y) in new[] { (a.GainMapMin, b.GainMapMin), (a.GainMapMax, b.GainMapMax), (a.Gamma, b.Gamma), (a.BaseOffset, b.BaseOffset), (a.AlternateOffset, b.AlternateOffset) })
        {
            await Assert.That(y).IsEquivalentTo(x, CollectionOrdering.Matching);
        }

        // The base is the SDR encoding of the same pixels; the map is decoded identically (or oriented as the base).
        using Image sdr = Image.Load(image.ToJpeg());
        await Assert.That(reloaded.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(sdr.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        DecodedGainMap source = DecodeMap(image.AttachedGainMap!), written = DecodeMap(reloaded.AttachedGainMap!);
        if (image.AttachedGainMap!.AppliedOrientation is ExifOrientation.Unknown or ExifOrientation.TopLeft)
        {
            await Assert.That(written.Pixels).IsEquivalentTo(source.Pixels, CollectionOrdering.Matching);
        }
        else
        {
            await Assert.That(new System.Drawing.Size(written.Width, written.Height)).IsEqualTo(new System.Drawing.Size(source.Height, source.Width));
        }
    }

    [Test]
    public async Task UneditedMap_IsCopiedWithoutReencoding()
    {
        using Image image = Image.Load(File("iso_rgb_p3.jpg"), Hdr);
        using Image thumbnail = Image.Load(File("iso_rgb_p3.jpg"), Hdr with { TargetSize = new(20, 15) });
        byte[] source = image.AttachedGainMap!.MapData.ToArray();
        int scan = source.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]);
        byte[] entropy = source[scan..];
        foreach (Image i in new[] { image, thumbnail })
        {
            byte[] file = i.ToJpeg(Preserve);
            await Assert.That(file.AsSpan().EndsWith(entropy)).IsTrue();
            using Image reloaded = Image.Load(file, Hdr);
            await Assert.That(reloaded.GainMap!.GainMapSize).IsEqualTo(image.GainMap!.GainMapSize);
        }
    }

    [Test]
    public async Task EditedMap_IsReencoded_AndReloadsCloseToTheEditedMap()
    {
        foreach (string name in new[] { "iso_mono_srgb", "iso_rgb_p3", "iso_alt_space", "iso_and_apple", "iso_orientation6" })
        {
            using Image image = Image.Load(File(name + ".jpg"), Hdr);
            image.Mutate(x => x.Crop(3, 2, image.Width - 7, image.Height - 5).Rotate(RotateMode.Rotate90).Resize(17, 25));
            DecodedGainMap edited = image.AttachedGainMap!.EditedMap!.Value;
            using Image reloaded = Image.Load(image.ToJpeg(Preserve), Hdr);
            await Assert.That(reloaded.HasHdr).IsTrue().Because(name);
            await Assert.That(reloaded.GainMap!.GainMapSize).IsEqualTo(new System.Drawing.Size(edited.Width, edited.Height)).Because(name);
            await Assert.That(reloaded.GainMap.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
            (int worst, _) = Difference(DecodeMap(reloaded.AttachedGainMap!), edited);
            await Assert.That(worst).IsLessThanOrEqualTo(8).Because($"{name}: JPEG quality 95");
            using HdrImage before = image.ToHdr(), after = reloaded.ToHdr();
            await Assert.That(after.Size).IsEqualTo(before.Size);
        }
    }

    /// <summary>
    /// An Apple-only map is written as ISO 21496-1 (0.8.1): gray map, gain bounds 0 and log2 H, gamma 1, no offsets,
    /// codes remapped; with the source's base pixels the reconstruction stays within the "apple_map" budget of
    /// tests/assets/hdr_reference.json at full headroom.
    /// </summary>
    [Test]
    [Arguments("apple_makernote")]
    [Arguments("apple_xmp_headroom")]
    public async Task AppleOnly_IsWrittenAsIso(string name)
    {
        using Image image = Image.Load(File(name + ".jpg"), Hdr);
        byte[] file = image.ToJpeg(Require);
        await Assert.That(image.ToJpeg(Preserve)).IsEquivalentTo(file, CollectionOrdering.Matching);
        using Image reloaded = Image.Load(file, Hdr);
        await Assert.That((reloaded.GainMap!.Detected, reloaded.GainMap.Selected, reloaded.GainMap.GainMapSize)).IsEqualTo((HdrGainMapKinds.Iso21496, HdrGainMapKinds.Iso21496, image.GainMap!.GainMapSize));
        GainMapParameters apple = image.AttachedGainMap!.Parameters!, iso = reloaded.AttachedGainMap!.Parameters!;
        await Assert.That((iso.ChannelCount, iso.BaseHeadroom, iso.UseBaseColorSpace)).IsEqualTo((1, 0f, true));
        await Assert.That((double)iso.AlternateHeadroom).IsEqualTo(apple.AlternateHeadroom).Within(1e-6);
        await Assert.That((double)iso.GainMapMax[0]).IsEqualTo(apple.AlternateHeadroom).Within(1e-6);
        await Assert.That((iso.GainMapMin[0], iso.Gamma[0], iso.BaseOffset[0], iso.AlternateOffset[0])).IsEqualTo((0f, 1f, 0f, 0f));

        using HdrImage expected = image.ToHdr();
        image.GetPixelSpan<Rgba32>().CopyTo(reloaded.GetPixelSpan<Rgba32>());
        using HdrImage actual = reloaded.ToHdr();
        float[] components = HdrSynthetic.Components(expected);
        HdrUltraHdrOracleTests.Stats s = HdrUltraHdrOracleTests.Measure(actual.GetPixelSpan(), components, 0, 0);
        System.Text.Json.JsonElement budget = HdrGainMapGeneratorTests.Budget;
        await Assert.That(s.MedianRelative).IsLessThanOrEqualTo(budget.GetProperty("apple_map_median_relative").GetDouble()).Because(s.ToString());
        await Assert.That(s.P99Relative).IsLessThanOrEqualTo(budget.GetProperty("apple_map_p99_relative").GetDouble()).Because(s.ToString());
    }

    [Test]
    public async Task AppleOnly_EditedOrThumbnail_IsWrittenAsIso()
    {
        using Image edited = Image.Load(File("apple_makernote.jpg"), Hdr);
        edited.Mutate(x => x.Rotate(RotateMode.Rotate90).Crop(2, 3, 20, 30));
        using Image thumbnail = Image.Load(File("apple_makernote.jpg"), Hdr with { TargetSize = new(24, 16) });
        foreach (Image image in new[] { edited, thumbnail })
        {
            using Image reloaded = Image.Load(image.ToJpeg(Require), Hdr);
            await Assert.That(reloaded.GainMap!.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
            await Assert.That(reloaded.Size).IsEqualTo(image.Size);
            using HdrImage hdr = reloaded.ToHdr();
            await Assert.That(hdr.Headroom).IsEqualTo(image.GainMap!.Headroom!.Value).Within(1e-3f);
        }
    }

    [Test]
    public async Task MissingOrUnusableMap_PreserveWritesSdr_RequireThrows()
    {
        using Image plain = Image.Load(File("iso_mono_srgb.jpg"));
        await Assert.That(plain.ToJpeg(Preserve)).IsEquivalentTo(plain.ToJpeg(), CollectionOrdering.Matching);
        Assert.ThrowsExactly<InvalidOperationException>(() => plain.ToJpeg(Require));
        using Image unsupported = Image.Load(File("iso_min_version1.jpg"), Hdr);
        await Assert.That(unsupported.ToJpeg(Preserve)).IsEquivalentTo(unsupported.ToJpeg(), CollectionOrdering.Matching);
        Assert.ThrowsExactly<NotSupportedException>(() => unsupported.ToJpeg(Require));
        using Image corrupt = Image.Load(File("iso_zero_denominator.jpg"), Hdr);
        Assert.ThrowsExactly<ImageFormatException>(() => corrupt.ToJpeg(Require));
        using Image removed = Image.Load(File("iso_mono_srgb.jpg"), Hdr);
        removed.Mutate(x => x.Grayscale());
        Assert.ThrowsExactly<InvalidOperationException>(() => removed.ToJpeg(Require));
    }

    [Test]
    public async Task DefaultSave_IsUnchangedByTheAttachedMap()
    {
        using Image image = Image.Load(File("iso_gamma_p3.jpg"), Hdr);
        using Image plain = Image.Load(File("iso_gamma_p3.jpg"));
        await Assert.That(image.ToJpeg()).IsEquivalentTo(plain.ToJpeg(), CollectionOrdering.Matching);
        await Assert.That(image.ToJpeg(new JpegEncoderOptions { GainMap = JpegGainMapMode.None })).IsEquivalentTo(plain.ToJpeg(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task GainMapXmp_IsLeftOut_OtherXmpIsKept()
    {
        byte[] declaring = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" hdrgm:Version=\"1.0\"/></rdf:RDF></x:xmpmeta>"u8.ToArray();
        byte[] other = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        using Image image = Image.Load(File("iso_mono_srgb.jpg"), Hdr);
        image.Metadata.Xmp = declaring;
        using (Image reloaded = Image.Load(image.ToJpeg(Preserve), Hdr))
        {
            await Assert.That(reloaded.Metadata.Xmp).IsNull();
            await Assert.That(reloaded.GainMap!.Detected).IsEqualTo(HdrGainMapKinds.Iso21496);
        }

        image.Metadata.Xmp = other;
        using (Image reloaded = Image.Load(image.ToJpeg(Preserve), Hdr))
        {
            await Assert.That(reloaded.Metadata.Xmp).IsEquivalentTo(other, CollectionOrdering.Matching);
            await Assert.That(reloaded.HasHdr).IsTrue();
        }
    }

    [Test]
    public async Task ExifIccAndOptionsAreKept()
    {
        using Image image = Image.Load(File("iso_orientation6.jpg"), new DecoderOptions { LoadGainMap = true });
        byte[] file = image.ToJpeg(Preserve with { Progressive = true, Quality = 90, RestartInterval = 2 });
        using Image reloaded = Image.Load(file, new DecoderOptions { LoadGainMap = true });
        await Assert.That(reloaded.Metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        await Assert.That(reloaded.Metadata.IccData).IsEquivalentTo(image.Metadata.IccData!, CollectionOrdering.Matching);
        await Assert.That(reloaded.HasHdr).IsTrue();
        await Assert.That(reloaded.GainMap!.GainMapSize).IsEqualTo(image.GainMap!.GainMapSize);
    }

    [Test]
    public async Task Options_ValidateGainMapSettings()
    {
        await Assert.That(new JpegEncoderOptions().GainMapQuality).IsEqualTo(95);
        await Assert.That(new JpegEncoderOptions().GainMap).IsEqualTo(JpegGainMapMode.None);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new JpegEncoderOptions { GainMapQuality = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new JpegEncoderOptions { GainMap = (JpegGainMapMode)7 });
    }

    private static byte[] WithBrokenMapHuffmanTable(string name)
    {
        byte[] data = System.IO.File.ReadAllBytes(File(name));
        int map = data.AsSpan().LastIndexOf([(byte)0xFF, (byte)0xD8, (byte)0xFF]);
        int dht = data.AsSpan(map).IndexOf([(byte)0xFF, (byte)0xC4]) + map;
        data.AsSpan(dht + 5, 16).Fill(0xFF);
        return data;
    }

    [Test]
    public async Task DamagedMap_UneditedIsCopied_OrientedFailsAsCorrupt()
    {
        // Unedited maps are copied without decoding: the damage travels with the file and ToHdr reports it.
        using Image copied = Image.Load(WithBrokenMapHuffmanTable("iso_mono_srgb.jpg"), Hdr);
        using Image reloaded = Image.Load(copied.ToJpeg(Require), Hdr);
        await Assert.That(reloaded.HasHdr).IsTrue();
        Assert.ThrowsExactly<ImageFormatException>(() => reloaded.ToHdr());

        // A map that has to be oriented is decoded first.
        using Image oriented = Image.Load(WithBrokenMapHuffmanTable("iso_orientation6.jpg"), Hdr);
        Assert.ThrowsExactly<ImageFormatException>(() => oriented.ToJpeg(Require));
        await Assert.That(oriented.ToJpeg(Preserve)).IsEquivalentTo(oriented.ToJpeg(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CanceledSave_WritesNothing()
    {
        using Image image = Image.Load(File("iso_orientation6.jpg"), Hdr);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stream = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() => image.SaveAsJpegAsync(stream, Require, cts.Token));
        await Assert.That(stream.Length).IsEqualTo(0);
    }
}
