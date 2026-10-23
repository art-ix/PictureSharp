using PictureSharp.Domain.Hdr;
using PictureSharp.Formats;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;
using PictureSharp.Tests.Hdr;

namespace PictureSharp.Tests.Extensibility;

/// <summary>
/// JPEG files written with a gain map that a format plugin attached (0.8.0): the map is encoded as an edited JPEG map
/// is (0.6.9, within 8 codes at quality 95) and the ISO 21496-1 metadata written from its parameters reloads exactly.
/// </summary>
public class PluginGainMapJpegTests
{
    private static readonly ImageConfiguration WithContainer =
        ImageConfiguration.Default.ToBuilder().AddFormat(new GainMapContainerFormat()).Build();

    private static readonly DecoderOptions Plugin = new() { LoadGainMap = true, ApplyExifOrientation = true, Configuration = WithContainer };
    private static readonly DecoderOptions Jpeg = new() { LoadGainMap = true, ApplyExifOrientation = true };
    private static readonly JpegEncoderOptions Require = new() { GainMap = JpegGainMapMode.Require };
    private static readonly JpegEncoderOptions Preserve = new() { GainMap = JpegGainMapMode.Preserve };

    internal static byte[] Container(string name) => GainMapContainerFormat.FromJpeg(File.ReadAllBytes(HdrGainMapTests.File(name + ".jpg")));

    public static IEnumerable<string> Cases() =>
        ["iso_mono_srgb", "iso_rgb_p3", "iso_alt_space", "iso_common_denominator", "iso_gamma_p3", "iso_orientation6",
            "iso_orientation7", "iso_odd_scale", "iso_and_apple"];

    private static DecodedGainMap MapOf(Image image)
    {
        using Image map = image.GetGainMap()!.Map.Decode(-1, default);
        return new(map.GetPixelSpan().ToArray(), map.Width, map.Height);
    }

    private static async Task AssertSameParameters(GainMapParameters actual, GainMapParameters expected, string name)
    {
        await Assert.That(actual.GainMapMin).IsEquivalentTo(expected.GainMapMin, CollectionOrdering.Matching).Because(name);
        await Assert.That(actual.GainMapMax).IsEquivalentTo(expected.GainMapMax, CollectionOrdering.Matching).Because(name);
        await Assert.That(actual.Gamma).IsEquivalentTo(expected.Gamma, CollectionOrdering.Matching).Because(name);
        await Assert.That(actual.BaseOffset).IsEquivalentTo(expected.BaseOffset, CollectionOrdering.Matching).Because(name);
        await Assert.That(actual.AlternateOffset).IsEquivalentTo(expected.AlternateOffset, CollectionOrdering.Matching).Because(name);
        await Assert.That((actual.BaseHeadroom, actual.AlternateHeadroom)).IsEqualTo((expected.BaseHeadroom, expected.AlternateHeadroom)).Because(name);
        await Assert.That(actual.MultiplicationSpace).IsEqualTo(expected.MultiplicationSpace).Because(name);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task PluginMap_IsWritten_AndReloadsWithTheSameMetadataAndAMapWithin8Codes(string name)
    {
        using Image image = Image.Load(Container(name), Plugin);
        using Image reloaded = Image.Load(image.ToJpeg(Require), Jpeg);
        await Assert.That(reloaded.HasHdr).IsTrue().Because(reloaded.Hdr?.UnsupportedReason ?? name);
        await Assert.That(reloaded.Hdr!.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
        await AssertSameParameters(reloaded.GainMap!.Parameters!, image.GainMap!.Parameters!, name);

        DecodedGainMap expected = MapOf(image), actual = HdrGainMapEncoderTests.DecodeMap(reloaded.GainMap);
        await Assert.That((actual.Width, actual.Height)).IsEqualTo((expected.Width, expected.Height)).Because(name);
        (int worst, _) = HdrGainMapEncoderTests.Difference(actual, expected);
        await Assert.That(worst).IsLessThanOrEqualTo(8).Because($"{name}: JPEG quality 95");
        using HdrImage before = image.ToHdr(), after = reloaded.ToHdr();
        await Assert.That((after.Size, after.ColorSpace)).IsEqualTo((before.Size, before.ColorSpace));
    }

    [Test]
    [Arguments("iso_mono_srgb", 1)]
    [Arguments("iso_rgb_p3", 3)]
    public async Task MapComponents_FollowTheMap(string name, int components)
    {
        using Image image = Image.Load(Container(name), Plugin);
        Dictionary<byte, ReadOnlyMemory<byte>> sections = GainMapContainerFormat.Read(GainMapContainerFormat.FromJpeg(image.ToJpeg(Require)));
        await Assert.That((int)sections[GainMapContainerFormat.Channels].Span[0]).IsEqualTo(components);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task MapIcc_IsWritten_OnlyWhenTheMultiplicationSpaceDiffersFromTheBase(string name)
    {
        using Image image = Image.Load(Container(name), Plugin);
        Dictionary<byte, ReadOnlyMemory<byte>> sections = GainMapContainerFormat.Read(GainMapContainerFormat.FromJpeg(image.ToJpeg(Require)));
        bool differs = image.GainMap!.Parameters!.MultiplicationSpace != image.GainMap.BaseSpace;
        await Assert.That(sections.ContainsKey(GainMapContainerFormat.MapIcc)).IsEqualTo(differs).Because(name);
        await Assert.That((sections[GainMapContainerFormat.Iso].Span[4] & 0x40) != 0).IsEqualTo(!differs).Because(name);
    }

    [Test]
    public async Task EditedPluginMap_IsWrittenInTheEditedGeometry()
    {
        using Image image = Image.Load(Container("iso_rgb_p3"), Plugin);
        image.Mutate(x => x.Crop(3, 2, image.Width - 7, image.Height - 5).Rotate(RotateMode.Rotate90).Resize(17, 25));
        DecodedGainMap edited = image.GainMap!.EditedMap!.Value;
        using Image reloaded = Image.Load(image.ToJpeg(Preserve), Jpeg);
        await Assert.That(reloaded.Hdr!.GainMapSize).IsEqualTo(new System.Drawing.Size(edited.Width, edited.Height));
        (int worst, _) = HdrGainMapEncoderTests.Difference(HdrGainMapEncoderTests.DecodeMap(reloaded.GainMap!), edited);
        await Assert.That(worst).IsLessThanOrEqualTo(8);
    }

    [Test]
    public async Task ApplePluginMap_PreserveWritesSdr_RequireThrows()
    {
        using Image image = Image.Load(Container("apple_makernote"), Plugin);
        await Assert.That(image.HasHdr).IsTrue();
        using Image sdr = Image.Load(image.ToJpeg(Preserve), Jpeg);
        await Assert.That(sdr.Hdr).IsNull();
        await Assert.That(() => image.ToJpeg(Require)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task UnchangedPluginMap_IsWrittenTheSameWay_EveryTime()
    {
        using Image image = Image.Load(Container("iso_gamma_p3"), Plugin);
        await Assert.That(image.ToJpeg(Require)).IsEquivalentTo(image.ToJpeg(Preserve), CollectionOrdering.Matching);
    }
}
