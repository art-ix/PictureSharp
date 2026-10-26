using PictureSharp.Formats;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;
using PictureSharp.Tests.Hdr;

namespace PictureSharp.Tests.Extensibility;

/// <summary>
/// The 0.7.0 plugin SPI: saving through <see cref="EncoderOptions.Configuration"/>, gain maps attached by a plugin
/// (reconstruction equal to the JPEG path for the same data, limits, failures and edits) and metadata without pixels.
/// </summary>
public class PluginSpiTests
{
    private static readonly ImageConfiguration WithContainer =
        ImageConfiguration.Default.ToBuilder().AddFormat(new GainMapContainerFormat()).Build();

    private static readonly ImageConfiguration WithReadOnlyContainer =
        ImageConfiguration.Default.ToBuilder().AddFormat(new GainMapContainerFormat(editableMetadata: false)).Build();

    private static DecoderOptions Hdr(DecoderOptions? options = null) =>
        (options ?? new DecoderOptions()) with { LoadGainMap = true, Configuration = WithContainer };

    private static byte[] Container(string name) => GainMapContainerFormat.FromJpeg(File.ReadAllBytes(HdrGainMapTests.File(name + ".jpg")));

    // ------------------------------------------------------------ EncoderOptions.Configuration

    [Test]
    public async Task Save_UsesTheConfigurationOfTheOptions()
    {
        var withPpm = ImageConfiguration.Default.ToBuilder().AddFormat(new PpmFormat()).Build();
        using var image = new Image(3, 2, new Rgba32(10, 20, 30, 255));
        await Assert.That(() => image.Encode(new PpmEncoderOptions())).Throws<NotSupportedException>();

        byte[] encoded = image.Encode(new PpmEncoderOptions { Configuration = withPpm });
        using Image decoded = Image.Load(encoded, new DecoderOptions { Configuration = withPpm });
        await Assert.That(decoded.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);

        using var stream = new MemoryStream();
        image.Save(stream, new PpmEncoderOptions { Configuration = withPpm });
        await Assert.That(stream.ToArray()).IsEquivalentTo(encoded, CollectionOrdering.Matching);
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.ppm");
        try
        {
            await image.SaveAsync(path, new PpmEncoderOptions { Configuration = withPpm });
            await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(encoded, CollectionOrdering.Matching);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task FrameWriter_UsesTheConfigurationOfTheOptions()
    {
        var pngOnly = ImageConfiguration.Default.ToBuilder().KeepOnly(ImageFormat.Png).Build();
        await Assert.That(() => Image.CreateFrameWriter(new MemoryStream(), new System.Drawing.Size(4, 4), new GifEncoderOptions { Configuration = pngOnly }))
            .Throws<NotSupportedException>();
        await using ImageFrameWriter writer = Image.CreateFrameWriter(new MemoryStream(), new System.Drawing.Size(4, 4), new GifEncoderOptions());
        await Assert.That(writer).IsNotNull();
    }

    // ------------------------------------------------------------ gain maps attached by a plugin

    public static IEnumerable<string> ReconstructionCases() =>
        ["iso_mono_srgb", "iso_rgb_p3", "iso_alt_space", "iso_common_denominator", "iso_gamma_p3",
            "iso_orientation6", "iso_orientation7", "iso_odd_scale", "iso_and_apple", "apple_makernote", "apple_xmp_headroom"];

    [Test]
    [MethodDataSource(nameof(ReconstructionCases))]
    public async Task ToHdr_EqualsTheJpegPath(string name)
    {
        var options = new DecoderOptions { LoadGainMap = true, ApplyExifOrientation = true };
        using Image jpeg = Image.Load(HdrGainMapTests.File(name + ".jpg"), options);
        using Image plugin = Image.Load(Container(name), Hdr(options));
        await Assert.That(plugin.HasHdr).IsTrue().Because(plugin.GainMap?.UnsupportedReason ?? "");
        await Assert.That(plugin.GainMap).IsEqualTo(jpeg.GainMap);
        await Assert.That(plugin.GetPixelSpan().ToArray()).IsEquivalentTo(jpeg.GetPixelSpan().ToArray(), CollectionOrdering.Matching);

        using HdrImage expected = jpeg.ToHdr();
        using HdrImage actual = plugin.ToHdr();
        await Assert.That((actual.Width, actual.Height, actual.ColorSpace, actual.Headroom))
            .IsEqualTo((expected.Width, expected.Height, expected.ColorSpace, expected.Headroom));
        await Assert.That(actual.GetPixelSpan().ToArray()).IsEquivalentTo(expected.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task AttachGainMap_ReportsEveryDetectedModel()
    {
        using Image image = Image.Load(Container("iso_and_apple"), Hdr());
        await Assert.That(image.GainMap!.Detected).IsEqualTo(HdrGainMapKinds.Iso21496 | HdrGainMapKinds.Apple);
        await Assert.That(image.GainMap.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
    }

    [Test]
    [Arguments(HdrGainMapKinds.Apple)]                          // without the metadata's ISO model
    [Arguments(HdrGainMapKinds.None)]
    [Arguments(HdrGainMapKinds.Iso21496 | (HdrGainMapKinds)64)] // undefined flag
    public async Task AttachGainMap_RejectsInconsistentDetectedModels(HdrGainMapKinds detected)
    {
        Dictionary<byte, ReadOnlyMemory<byte>> sections = GainMapContainerFormat.Read(Container("iso_mono_srgb"));
        GainMapMetadata metadata = GainMapMetadata.ParseIso(sections[GainMapContainerFormat.Iso].Span, 1);
        Exception? thrown = null;
        var format = new PpmFormat();
        format.Decoder = new AttachProbe(format.Decoder!, context =>
        {
            try
            {
                context.AttachGainMap(metadata, new SolidMap(), detected);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });
        var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(format).Build();
        using var source = new Image(2, 2, new Rgba32(1, 2, 3, 255));
        byte[] ppm = source.Encode(new PpmEncoderOptions { Configuration = configuration });
        using Image image = Image.Load(ppm, new DecoderOptions { Configuration = configuration, LoadGainMap = true });
        await Assert.That(thrown).IsTypeOf<ArgumentException>();
        await Assert.That(image.GainMap).IsNull();
    }

    private sealed class AttachProbe(IImageFormatDecoder inner, Action<ImageDecodeContext> probe) : IImageFormatDecoder
    {
        public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => inner.Identify(data, options);

        public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
        {
            Image image = inner.Decode(data, context);
            probe(context);
            return image;
        }
    }

    private sealed class SolidMap() : GainMapImage(new System.Drawing.Size(1, 1))
    {
        public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken) => new(1, 1, new Rgba32(128, 128, 128, 255));
    }

    [Test]
    public async Task ToHdr_FollowsColorConversionLikeJpeg()
    {
        var options = new DecoderOptions { LoadGainMap = true, ColorConversion = ColorConversionMode.ToSrgb };
        using Image jpeg = Image.Load(HdrGainMapTests.File("iso_rgb_p3.jpg"), options);
        using Image plugin = Image.Load(Container("iso_rgb_p3"), Hdr(options));
        using HdrImage expected = jpeg.ToHdr();
        using HdrImage actual = plugin.ToHdr();
        await Assert.That(actual.ColorSpace).IsEqualTo(HdrColorSpace.LinearSrgb);
        await Assert.That(actual.GetPixelSpan().ToArray()).IsEquivalentTo(expected.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task TargetSizeAndEdits_KeepThePluginMap()
    {
        using Image thumbnail = Image.Load(Container("iso_orientation6"), Hdr(new DecoderOptions { TargetSize = new(40, 40), ApplyExifOrientation = true }));
        await Assert.That(thumbnail.HasHdr).IsTrue();
        await Assert.That(Math.Max(thumbnail.Width, thumbnail.Height)).IsEqualTo(40);
        using (HdrImage hdr = thumbnail.ToHdr())
        {
            await Assert.That((hdr.Width, hdr.Height)).IsEqualTo((thumbnail.Width, thumbnail.Height));
        }

        thumbnail.Mutate(x => x.Rotate(RotateMode.Rotate90).Crop(new System.Drawing.Rectangle(2, 3, 20, 15)));
        await Assert.That(thumbnail.HasHdr).IsTrue();
        using HdrImage edited = thumbnail.ToHdr();
        await Assert.That((edited.Width, edited.Height)).IsEqualTo((20, 15));
    }

    [Test]
    public async Task Identify_AndLoadWithoutLoadGainMap_ReportNoMap()
    {
        using Image image = Image.Load(Container("iso_mono_srgb"), new DecoderOptions { Configuration = WithContainer });
        await Assert.That(image.GainMap).IsNull();
        ImageInfo info = Image.Identify(Container("iso_mono_srgb"), new DecoderOptions { Configuration = WithContainer });
        await Assert.That((info.Format, info.ContainerItemCount)).IsEqualTo((GainMapContainerFormat.Format, (int?)2));
    }

    [Test]
    [Arguments("iso_truncated", typeof(ImageFormatException))]
    [Arguments("iso_zero_denominator", typeof(ImageFormatException))]
    [Arguments("iso_negative_offset", typeof(ImageFormatException))]
    [Arguments("iso_backward", typeof(NotSupportedException))]
    [Arguments("apple_unknown_version", typeof(NotSupportedException))]
    [Arguments("base_adobe_rgb", typeof(NotSupportedException))]
    public async Task UnusableMaps_AreReportedWithoutFailingTheLoad(string name, Type exception)
    {
        using Image image = Image.Load(Container(name), Hdr());
        await Assert.That(image.GainMap).IsNotNull();
        await Assert.That((image.GainMap!.CanDecode, image.HasHdr)).IsEqualTo((false, false));
        await Assert.That(image.GainMap.UnsupportedReason).IsNotNull();
        Exception? thrown = null;
        try
        {
            image.ToHdr().Dispose();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown?.GetType()).IsEqualTo(exception);
    }

    [Test]
    public async Task MapOverMaxPixels_IsReportedAndTheBaseLoads()
    {
        byte[] data = Container("iso_mono_srgb");
        ImageInfo info = Image.Identify(data, new DecoderOptions { Configuration = WithContainer });
        using Image image = Image.Load(data, Hdr(new DecoderOptions { MaxPixels = ((long)info.Width * info.Height) + 1 }));
        await Assert.That(image.HasHdr).IsFalse();
        await Assert.That(image.GainMap!.UnsupportedReason!).Contains("MaxPixels");
    }

    [Test]
    public async Task CorruptMapPixels_FailToHdrWithThePluginFormat()
    {
        Dictionary<byte, ReadOnlyMemory<byte>> sections = GainMapContainerFormat.Read(Container("iso_mono_srgb"));
        byte[] map = sections[GainMapContainerFormat.Map].ToArray();
        Array.Clear(map, map.Length / 2, map.Length / 2);   // keep the headers, destroy the scan
        map[^2] = 0x12;
        sections[GainMapContainerFormat.Map] = map;
        byte[] data = GainMapContainerFormat.Write(sections.Select(s => (s.Key, s.Value.ToArray())));
        using Image image = Image.Load(data, Hdr());
        await Assert.That(image.HasHdr).IsTrue();
        ImageFormatException? error = null;
        try
        {
            image.ToHdr().Dispose();
        }
        catch (ImageFormatException ex)
        {
            error = ex;
        }

        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Format).IsEqualTo(GainMapContainerFormat.Format);
    }

    [Test]
    public async Task SavingAPluginMapToJpeg_WritesTheMap()
    {
        // Details in PluginGainMapJpegTests; before 0.8.0, Preserve wrote SDR and Require threw NotSupportedException.
        using Image image = Image.Load(Container("iso_mono_srgb"), Hdr());
        byte[] hdr = image.Encode(new JpegEncoderOptions { GainMap = JpegGainMapMode.Preserve });
        using Image reloaded = Image.Load(hdr, new DecoderOptions { LoadGainMap = true });
        await Assert.That(reloaded.HasHdr).IsTrue();
    }

    // ------------------------------------------------------------ metadata without pixels

    [Test]
    public async Task Metadata_IsReadAndRewrittenByThePlugin()
    {
        byte[] data = GainMapContainerFormat.Write([(GainMapContainerFormat.Base, Encode()), (GainMapContainerFormat.Xmp, "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray())]);
        var options = new DecoderOptions { Configuration = WithContainer };
        ImageMetadata metadata = Image.ReadMetadata(data, options);
        await Assert.That(GainMapContainerFormat.Text(metadata.Xmp)).Contains("xmpmeta");

        int before = GainMapContainerFormat.RewriteCount;
        byte[] unchanged = Image.EditMetadata(data, _ => { }, options);
        await Assert.That(unchanged).IsEquivalentTo(data, CollectionOrdering.Matching);
        await Assert.That(GainMapContainerFormat.RewriteCount).IsEqualTo(before);

        byte[] edited = Image.EditMetadata(data, m => m.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><edited/></x:xmpmeta>"u8.ToArray(), options);
        await Assert.That(GainMapContainerFormat.Text(Image.ReadMetadata(edited, options).Xmp)).Contains("edited");
        using Image image = Image.Load(edited, options);
        await Assert.That(GainMapContainerFormat.Text(image.Metadata.Xmp)).Contains("edited");
    }

    [Test]
    public async Task Metadata_ReadOnlyCodecAndLimits()
    {
        byte[] xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        byte[] data = GainMapContainerFormat.Write([(GainMapContainerFormat.Base, Encode()), (GainMapContainerFormat.Xmp, xmp)]);
        var options = new DecoderOptions { Configuration = WithReadOnlyContainer };
        await Assert.That(Image.ReadMetadata(data, options).Xmp).IsNotNull();
        await Assert.That(() => Image.EditMetadata(data, m => m.Xmp = null, options)).Throws<NotSupportedException>();
        await Assert.That(() => Image.ReadMetadata(data, options with { MaxMetadataBytes = xmp.Length - 1 })).Throws<ImageFormatException>();
        await Assert.That(() => Image.ReadMetadata(data, new DecoderOptions())).Throws<ImageFormatException>();
    }

    private static byte[] Encode()
    {
        using var image = new Image(4, 3, new Rgba32(200, 100, 50, 255));
        return image.Encode(ImageFormat.Png);
    }
}
