using PictureSharp.Formats;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Guards the plugin SPI declared stable in docs/plugin-spi.md: until 2.0 it may only grow. Removing or changing a line
/// of tests/assets/PluginSpi.approved.txt, or adding an interface member without a default implementation, breaks
/// plugins built against an earlier version and fails as a breaking change. After an intended addition, copy
/// PluginSpi.received.txt over the approved file.
/// </summary>
public class PluginSpiSnapshotTests
{
    /// <summary>The types of docs/plugin-spi.md: the plugin contracts and the model plugins create and fill.</summary>
    internal static readonly Type[] StableTypes =
    [
        // Contracts.
        typeof(IImageFormatModule), typeof(IImageFormatDecoder), typeof(IImageFormatEncoder), typeof(IImageFormatEncoder<>),
        typeof(IImageFormatMetadataCodec), typeof(ImageDecodeContext), typeof(FormatDecoderOptions), typeof(FormatMetadata),
        typeof(EmbeddedPartOptions), typeof(GainMapMetadata), typeof(GainMapImage), typeof(EncodableGainMap),
        typeof(GainMapExtensions), typeof(ImageFormat), typeof(ImageFormatException), typeof(ImageConfigurationBuilder),
        typeof(DecoderOptions), typeof(EncoderOptions), typeof(ImageInfo), typeof(ImageColorInfo), typeof(ImageColorModel),
        typeof(ImageSampleFormat), typeof(ImageAlphaMode), typeof(ImageCompression), typeof(CicpColorDescription),
        typeof(ImageResolution),
        // Model.
        typeof(Image), typeof(ImageFrame), typeof(ImageFrameCollection), typeof(Rgba32), typeof(Rgba64), typeof(PixelType), typeof(ImageMetadata),
        typeof(MetadataKinds), typeof(GainMapInfo), typeof(HdrGainMapKinds), typeof(HdrColorSpace),
    ];

    [Test]
    public void PluginSpi_OnlyGrows()
    {
        string[] actual = Describe();
        string approvedPath = Path.Combine(Corpus.Assets, "PluginSpi.approved.txt");
        string receivedPath = Path.Combine(Corpus.Assets, "PluginSpi.received.txt");
        string[] approved = File.Exists(approvedPath)
            ? File.ReadAllText(approvedPath).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')
            : [];
        if (approved.SequenceEqual(actual))
        {
            File.Delete(receivedPath);
            return;
        }

        File.WriteAllText(receivedPath, string.Join('\n', actual) + "\n");
        var approvedSet = new HashSet<string>(approved, StringComparer.Ordinal);
        string[] removed = [.. approved.Except(actual, StringComparer.Ordinal)];
        string[] added = [.. actual.Except(approved, StringComparer.Ordinal)];
        // A new abstract member of an interface that existed before must be implemented by every plugin.
        string[] newAbstract = [.. added.Where(line =>
            line.Split(" :: ", 2) is [string type, string member]
            && !member.StartsWith("method default ", StringComparison.Ordinal) && !member.StartsWith("property default ", StringComparison.Ordinal)
            && approvedSet.Contains(type) && type.StartsWith("interface ", StringComparison.Ordinal))];
        if (removed.Length > 0 || newAbstract.Length > 0)
        {
            Assert.Fail("Breaking change to the stable plugin SPI (docs/plugin-spi.md); only additions are allowed until 2.0:\n"
                + string.Join('\n', removed.Select(l => "- " + l).Concat(newAbstract.Select(l => "+ (abstract) " + l))));
        }

        Assert.Fail($"The plugin SPI grew; review {receivedPath} and copy it over {approvedPath}:\n"
            + string.Join('\n', added.Select(l => "+ " + l)));
    }

    /// <summary>One line per type, then one per member qualified by its type's line, so the diff is order-free.</summary>
    private static string[] Describe()
    {
        var lines = new List<string>();
        foreach (Type type in StableTypes.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            string header = PublicApiTests.Header(type);
            lines.Add(header);
            lines.AddRange(PublicApiTests.Members(type, markDefaults: true).Select(member => $"{header} :: {member}"));
        }

        return [.. lines];
    }
}
