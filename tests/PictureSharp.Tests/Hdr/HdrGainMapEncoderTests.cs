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

    private static DecodedGainMap DecodeMap(GainMapAttachment attachment) =>
        attachment.EditedMap ?? attachment.Decoder!.Decode(attachment.MapData, 1, 1, CancellationToken.None);

    private static (int Worst, int Count) Difference(DecodedGainMap a, DecodedGainMap b)
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
        await Assert.That(reloaded.Hdr!.Detected).IsEqualTo(HdrGainMapKinds.Iso21496);   // no Apple or XMP declaration
        GainMapParameters a = image.GainMap!.Parameters!, b = reloaded.GainMap!.Parameters!;
        await Assert.That((b.Kind, b.ChannelCount, b.BaseHeadroom, b.AlternateHeadroom, b.MultiplicationSpace))
            .IsEqualTo((a.Kind, a.ChannelCount, a.BaseHeadroom, a.AlternateHeadroom, a.MultiplicationSpace));
        foreach ((float[] x, float[] y) in new[] { (a.GainMapMin, b.GainMapMin), (a.GainMapMax, b.GainMapMax), (a.Gamma, b.Gamma), (a.BaseOffset, b.BaseOffset), (a.AlternateOffset, b.AlternateOffset) })
        {
            await Assert.That(y).IsEquivalentTo(x, CollectionOrdering.Matching);
        }

        // The base is the SDR encoding of the same pixels; the map is decoded identically (or oriented as the base).
        using Image sdr = Image.Load(image.ToJpeg());
        await Assert.That(reloaded.GetPixelSpan().ToArray()).IsEquivalentTo(sdr.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        DecodedGainMap source = DecodeMap(image.GainMap!), written = DecodeMap(reloaded.GainMap!);
        if (image.GainMap!.AppliedOrientation is ExifOrientation.Unknown or ExifOrientation.TopLeft)
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
        byte[] source = image.GainMap!.MapData.ToArray();
        int scan = source.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]);
        byte[] entropy = source[scan..];
        foreach (Image i in new[] { image, thumbnail })
        {
            byte[] file = i.ToJpeg(Preserve);
            await Assert.That(file.AsSpan().EndsWith(entropy)).IsTrue();
            using Image reloaded = Image.Load(file, Hdr);
            await Assert.That(reloaded.Hdr!.GainMapSize).IsEqualTo(image.Hdr!.GainMapSize);
        }
    }

    [Test]
    public async Task EditedMap_IsReencoded_AndReloadsCloseToTheEditedMap()
    {
        foreach (string name in new[] { "iso_mono_srgb", "iso_rgb_p3", "iso_alt_space", "iso_and_apple", "iso_orientation6" })
        {
            using Image image = Image.Load(File(name + ".jpg"), Hdr);
            image.Mutate(x => x.Crop(3, 2, image.Width - 7, image.Height - 5).Rotate(RotateMode.Rotate90).Resize(17, 25));
            DecodedGainMap edited = image.GainMap!.EditedMap!.Value;
            using Image reloaded = Image.Load(image.ToJpeg(Preserve), Hdr);
            await Assert.That(reloaded.HasHdr).IsTrue().Because(name);
            await Assert.That(reloaded.Hdr!.GainMapSize).IsEqualTo(new System.Drawing.Size(edited.Width, edited.Height)).Because(name);
            await Assert.That(reloaded.Hdr.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
            (int worst, _) = Difference(DecodeMap(reloaded.GainMap!), edited);
            await Assert.That(worst).IsLessThanOrEqualTo(8).Because($"{name}: JPEG quality 95");
            using HdrImage before = image.ToHdr(), after = reloaded.ToHdr();
            await Assert.That(after.Size).IsEqualTo(before.Size);
        }
    }

    [Test]
    public async Task AppleOnly_PreserveWritesSdr_RequireThrows()
    {
        using Image image = Image.Load(File("apple_makernote.jpg"), Hdr);
        await Assert.That(image.ToJpeg(Preserve)).IsEquivalentTo(image.ToJpeg(), CollectionOrdering.Matching);
        Assert.ThrowsExactly<NotSupportedException>(() => image.ToJpeg(Require));
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
            await Assert.That(reloaded.Hdr!.Detected).IsEqualTo(HdrGainMapKinds.Iso21496);
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
        await Assert.That(reloaded.Hdr!.GainMapSize).IsEqualTo(image.Hdr!.GainMapSize);
    }

    [Test]
    public async Task Options_ValidateGainMapSettings()
    {
        await Assert.That(new JpegEncoderOptions().GainMapQuality).IsEqualTo(95);
        await Assert.That(new JpegEncoderOptions().GainMap).IsEqualTo(JpegGainMapMode.None);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new JpegEncoderOptions { GainMapQuality = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new JpegEncoderOptions { GainMap = (JpegGainMapMode)7 });
    }
}
