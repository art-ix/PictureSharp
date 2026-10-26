using PictureSharp.Domain.Hdr;
using PictureSharp.Formats;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;
using PictureSharp.Tests.Hdr;

namespace PictureSharp.Tests.Extensibility;

/// <summary>
/// A plugin encoder writing the attached gain map through <c>GetGainMap</c> (0.8.0): maps from JPEG and from the plugin
/// itself, unedited, oriented and edited, reload with the same metadata and a map within 8 codes (JPEG quality 95, the
/// 0.6.9 budget); an unedited plugin map is copied byte for byte.
/// </summary>
public class PluginGainMapEncoderTests
{
    private static readonly ImageConfiguration WithContainer =
        ImageConfiguration.Default.ToBuilder().AddFormat(new GainMapContainerFormat()).Build();

    private static readonly DecoderOptions Hdr = new() { LoadGainMap = true, ApplyExifOrientation = true, Configuration = WithContainer };

    private static GainMapContainerEncoderOptions Options(JpegGainMapMode mode) => new() { GainMap = mode, Configuration = WithContainer };

    private static byte[] Container(string name) => PluginGainMapJpegTests.Container(name);

    private static DecodedGainMap MapOf(Image image)
    {
        using Image map = image.GetGainMap()!.Map.Decode(-1, default);
        return new(map.GetPixelSpan().ToArray(), map.Width, map.Height);
    }

    private static async Task AssertReloads(Image source, byte[] written, string name)
    {
        using Image reloaded = Image.Load(written, Hdr);
        await Assert.That(reloaded.SourceFormat).IsEqualTo(GainMapContainerFormat.Format);
        await Assert.That(reloaded.HasHdr).IsTrue().Because(reloaded.GainMap?.UnsupportedReason ?? name);
        GainMapParameters expected = source.AttachedGainMap!.Parameters!, actual = reloaded.AttachedGainMap!.Parameters!;
        await Assert.That(actual.GainMapMax).IsEquivalentTo(expected.GainMapMax, CollectionOrdering.Matching).Because(name);
        await Assert.That(actual.GainMapMin).IsEquivalentTo(expected.GainMapMin, CollectionOrdering.Matching).Because(name);
        await Assert.That(actual.Gamma).IsEquivalentTo(expected.Gamma, CollectionOrdering.Matching).Because(name);
        await Assert.That(actual.BaseOffset).IsEquivalentTo(expected.BaseOffset, CollectionOrdering.Matching).Because(name);
        await Assert.That(actual.AlternateOffset).IsEquivalentTo(expected.AlternateOffset, CollectionOrdering.Matching).Because(name);
        await Assert.That((actual.BaseHeadroom, actual.AlternateHeadroom)).IsEqualTo((expected.BaseHeadroom, expected.AlternateHeadroom)).Because(name);
        await Assert.That(actual.MultiplicationSpace).IsEqualTo(expected.MultiplicationSpace).Because(name);

        DecodedGainMap before = MapOf(source), after = MapOf(reloaded);
        await Assert.That((after.Width, after.Height)).IsEqualTo((before.Width, before.Height)).Because(name);
        (int worst, _) = HdrGainMapEncoderTests.Difference(after, before);
        await Assert.That(worst).IsLessThanOrEqualTo(8).Because($"{name}: JPEG quality 95");
        await Assert.That((reloaded.Width, reloaded.Height)).IsEqualTo((source.Width, source.Height));
        using HdrImage hdr = reloaded.ToHdr();
        await Assert.That(hdr.Size).IsEqualTo(source.Size);
    }

    [Test]
    [MethodDataSource(typeof(PluginGainMapJpegTests), nameof(PluginGainMapJpegTests.Cases))]
    public async Task JpegMap_IsWrittenByThePlugin(string name)
    {
        using Image image = Image.Load(HdrGainMapTests.File(name + ".jpg"), Hdr);
        await AssertReloads(image, image.Encode(Options(JpegGainMapMode.Require)), name);
    }

    [Test]
    [MethodDataSource(typeof(PluginGainMapJpegTests), nameof(PluginGainMapJpegTests.Cases))]
    public async Task OwnMap_IsWrittenBack(string name)
    {
        using Image image = Image.Load(Container(name), Hdr);
        byte[] written = image.Encode(Options(JpegGainMapMode.Require));
        await AssertReloads(image, written, name);

        // Unrotated maps come back as the plugin's own instance and are copied without re-encoding.
        bool oriented = name.StartsWith("iso_orientation", StringComparison.Ordinal);
        byte[] original = GainMapContainerFormat.Read(Container(name))[GainMapContainerFormat.Map].ToArray();
        byte[] map = GainMapContainerFormat.Read(written)[GainMapContainerFormat.Map].ToArray();
        await Assert.That(map.AsSpan().SequenceEqual(original)).IsEqualTo(!oriented).Because(name);
    }

    [Test]
    [Arguments("iso_rgb_p3")]
    [Arguments("iso_mono_srgb")]
    public async Task EditedMap_IsWrittenInTheEditedGeometry(string name)
    {
        using Image image = Image.Load(Container(name), Hdr);
        image.Mutate(x => x.Crop(3, 2, image.Width - 7, image.Height - 5).Rotate(RotateMode.Rotate90).Resize(17, 25));
        await AssertReloads(image, image.Encode(Options(JpegGainMapMode.Preserve)), name);
    }

    [Test]
    [Arguments("iso_mono_srgb", 1)]
    [Arguments("iso_rgb_p3", 3)]
    public async Task MapComponents_FollowTheMap(string name, int components)
    {
        using Image image = Image.Load(HdrGainMapTests.File(name + ".jpg"), Hdr);
        byte[] written = image.Encode(Options(JpegGainMapMode.Require));
        await Assert.That((int)GainMapContainerFormat.Read(written)[GainMapContainerFormat.Channels].Span[0]).IsEqualTo(components);
    }

    [Test]
    public async Task Modes_FollowTheJpegEncoder()
    {
        using Image plain = Image.Load(HdrGainMapTests.File("iso_mono_srgb.jpg"));   // LoadGainMap = false
        await Assert.That(GainMapContainerFormat.Read(plain.Encode(Options(JpegGainMapMode.Preserve))).ContainsKey(GainMapContainerFormat.Map)).IsFalse();
        await Assert.That(() => plain.Encode(Options(JpegGainMapMode.Require))).Throws<InvalidOperationException>();

        using Image apple = Image.Load(HdrGainMapTests.File("apple_makernote.jpg"), Hdr);
        // An Apple-only map is handed to the plugin converted to ISO 21496-1 (0.8.1).
        await Assert.That(GainMapContainerFormat.Read(apple.Encode(Options(JpegGainMapMode.Preserve))).ContainsKey(GainMapContainerFormat.Map)).IsTrue();
        await Assert.That(apple.Encode(Options(JpegGainMapMode.Require))).IsEquivalentTo(apple.Encode(Options(JpegGainMapMode.Preserve)), CollectionOrdering.Matching);

        using Image hdr = Image.Load(HdrGainMapTests.File("iso_mono_srgb.jpg"), Hdr);
        await Assert.That(GainMapContainerFormat.Read(hdr.Encode(Options(JpegGainMapMode.None))).ContainsKey(GainMapContainerFormat.Map)).IsFalse();
    }
}
