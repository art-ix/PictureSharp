using PictureSharp.Formats;
using PictureSharp.Processing;
using PictureSharp.Tests.Hdr;

namespace PictureSharp.Tests.Extensibility;

/// <summary><c>GetGainMap</c>: the attached gain map as an encoder of a format plugin sees it.</summary>
public class GainMapEncodingTests
{
    private static readonly ImageConfiguration WithContainer =
        ImageConfiguration.Default.ToBuilder().AddFormat(new GainMapContainerFormat()).Build();

    private static readonly DecoderOptions Hdr = new() { LoadGainMap = true, ApplyExifOrientation = true, Configuration = WithContainer };

    private static byte[] Container(string name) => GainMapContainerFormat.FromJpeg(File.ReadAllBytes(HdrGainMapTests.File(name + ".jpg")));

    private static Image LoadJpeg(string name) => Image.Load(HdrGainMapTests.File(name + ".jpg"), Hdr);

    private static Rgba32[] Pixels(GainMapImage map)
    {
        using Image image = map.Decode(-1, default);
        return image.GetPixelSpan<Rgba32>().ToArray();
    }

    [Test]
    public async Task UneditedPluginMap_IsThePluginsOwnInstance()
    {
        using Image image = Image.Load(Container("iso_rgb_p3"), Hdr);
        EncodableGainMap gainMap = image.GetGainMap()!;
        await Assert.That(gainMap.Map.GetType().DeclaringType).IsEqualTo(typeof(GainMapContainerFormat));
        await Assert.That(gainMap.Map.Size).IsEqualTo(image.GainMap!.GainMapSize);
        await Assert.That(image.GetGainMap()!.Map).IsSameReferenceAs(gainMap.Map);
    }

    [Test]
    [Arguments("iso_mono_srgb")]
    [Arguments("iso_alt_space")]
    [Arguments("iso_rgb_p3")]
    public async Task Metadata_WritesTheFilesValues(string name)
    {
        using Image image = Image.Load(Container(name), Hdr);
        GainMapMetadata metadata = image.GetGainMap()!.Metadata;
        Dictionary<byte, ReadOnlyMemory<byte>> sections = GainMapContainerFormat.Read(Container(name));
        byte[] icc = sections.GetValueOrDefault(GainMapContainerFormat.MapIcc).ToArray();
        GainMapMetadata file = GainMapMetadata.ParseIso(sections[GainMapContainerFormat.Iso].Span, 3, icc);
        await Assert.That(metadata.Kind).IsEqualTo(HdrGainMapKinds.Iso21496);
        // Loading fills in the base primaries for reconstruction; the written metadata keeps the file's flag.
        await Assert.That(metadata.MultiplicationSpace).IsEqualTo(file.MultiplicationSpace);
        await Assert.That(metadata.ToIso()).IsEquivalentTo(file.ToIso(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("iso_orientation6")]
    [Arguments("iso_orientation7")]
    public async Task OrientedPluginMap_IsDecodedInTheBaseOrientation(string name)
    {
        using Image plugin = Image.Load(Container(name), Hdr);
        GainMapImage map = plugin.GetGainMap()!.Map;
        await Assert.That(map.GetType().DeclaringType).IsNotEqualTo(typeof(GainMapContainerFormat));
        await Assert.That(map.Size).IsEqualTo(new System.Drawing.Size(plugin.GainMap!.GainMapSize.Height, plugin.GainMap.GainMapSize.Width));

        // Independently: the JPEG loaded unoriented, then oriented by an edit that carries the map along.
        using Image jpeg = Image.Load(HdrGainMapTests.File(name + ".jpg"), new DecoderOptions { LoadGainMap = true });
        jpeg.Mutate(x => x.AutoOrient());
        await Assert.That((jpeg.Width, jpeg.Height)).IsEqualTo((plugin.Width, plugin.Height));
        await Assert.That(Pixels(map)).IsEquivalentTo(jpeg.AttachedGainMap!.EditedMap!.Value.Pixels, CollectionOrdering.Matching);
        await Assert.That((map.Size.Width, map.Size.Height)).IsEqualTo((jpeg.AttachedGainMap.EditedMap.Value.Width, jpeg.AttachedGainMap.EditedMap.Value.Height));
    }

    [Test]
    public async Task EditedMap_HasTheEditedGeometry_AndEveryDecodeIsACopy()
    {
        using Image image = LoadJpeg("iso_rgb_p3");
        image.Mutate(x => x.Crop(new System.Drawing.Rectangle(3, 2, image.Width / 2, image.Height / 2)).Resize(40, 30));
        GainMapImage map = image.GetGainMap()!.Map;
        await Assert.That(map.Size).IsEqualTo(image.GainMap!.GainMapSize);

        using Image first = map.Decode(1, default);
        Rgba32[] expected = image.AttachedGainMap!.EditedMap!.Value.Pixels;
        await Assert.That(first.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        first.GetPixelSpan<Rgba32>()[0] = new Rgba32(1, 2, 3, 4);
        await Assert.That(Pixels(map)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task UneditedJpegMap_IsDecodedAtFullSize()
    {
        using Image image = LoadJpeg("iso_mono_srgb");
        GainMapImage map = image.GetGainMap()!.Map;
        await Assert.That(map.Size).IsEqualTo(image.GainMap!.GainMapSize);
        Rgba32[] pixels = Pixels(map);
        await Assert.That(pixels.Length).IsEqualTo(map.Size.Width * map.Size.Height);
        await Assert.That(pixels.All(p => p.R == p.G && p.G == p.B)).IsTrue();
    }

    [Test]
    public async Task Decode_ObservesCancellation()
    {
        using Image image = LoadJpeg("iso_mono_srgb");
        GainMapImage map = image.GetGainMap()!.Map;
        await Assert.That(() => map.Decode(-1, new CancellationToken(canceled: true))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WithoutAMap_ReturnsNull_OrThrowsWhenRequired()
    {
        using Image image = Image.Load(HdrGainMapTests.File("iso_mono_srgb.jpg"));   // LoadGainMap = false
        await Assert.That(image.GetGainMap()).IsNull();
        await Assert.That(() => image.GetGainMap(require: true)).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("apple_makernote")]
    [Arguments("apple_xmp_headroom")]
    public async Task AppleMap_IsConvertedToIso(string name)
    {
        using Image image = LoadJpeg(name);
        await Assert.That(image.HasHdr).IsTrue();
        EncodableGainMap gainMap = image.GetGainMap(require: true)!;
        await Assert.That((gainMap.Metadata.Kind, gainMap.Metadata.ChannelCount, gainMap.Map.Size)).IsEqualTo((HdrGainMapKinds.Iso21496, 1, image.GainMap!.GainMapSize));
        await Assert.That((double)gainMap.Metadata.Headroom).IsEqualTo(image.GainMap.Headroom!.Value).Within(1e-4);
        await Assert.That(gainMap.Metadata.ToIso().Length).IsGreaterThan(0);
        using Image map = gainMap.Map.Decode(-1, default);
        await Assert.That(map.GetPixelSpan<Rgba32>().ToArray().All(p => p.R == p.G && p.G == p.B)).IsTrue();
        await Assert.That(image.AttachedGainMap!.Parameters!.Kind).IsEqualTo(HdrGainMapKinds.Apple);   // the image keeps its own map
    }

    [Test]
    [MethodDataSource(typeof(HdrGainMapTests), nameof(HdrGainMapTests.InvalidCases))]
    public async Task InvalidMap_ThrowsWhatToHdrThrows(string name)
    {
        using Image image = LoadJpeg(name);
        Type expected = Record(() => image.ToHdr().Dispose())!.GetType();
        await Assert.That(image.GetGainMap()).IsNull();
        await Assert.That(Record(() => image.GetGainMap(require: true))?.GetType()).IsEqualTo(expected);
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
