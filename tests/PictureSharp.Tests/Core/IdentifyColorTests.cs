using System.Text.Json;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// <see cref="ImageInfo.Color"/>, <see cref="ImageInfo.Compression"/> and <see cref="ImageInfo.Resolution"/> (0.8.5)
/// against identify_reference.json: synthetic files in corpus/identify/ whose expectations are the settings used to write
/// them, cross-checked with Pillow by make_reference.py.
/// </summary>
public class IdentifyColorTests
{
    private static readonly Lazy<Dictionary<string, JsonElement>> References = new(() =>
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Assets, "identify_reference.json")));
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    });

    public static TestCases<string> Files() => [.. References.Value.Keys.Order(StringComparer.Ordinal)];

    public static TestCases<string> CorpusFiles() =>
        [.. Directory.EnumerateFiles(Corpus.CorpusDir, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is not ".bin")
            .Select(p => Path.GetRelativePath(Corpus.CorpusDir, p).Replace('\\', '/'))
            .Where(p => !p.StartsWith("src/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Identify_MatchesReference(string file)
    {
        JsonElement expected = References.Value[file];
        ImageInfo info = Image.Identify(Corpus.Read("identify/" + file));
        ImageColorInfo color = info.Color;

        await Assert.That(color.Model.ToString()).IsEqualTo(expected.GetProperty("model").GetString());
        await Assert.That(color.ChannelCount).IsEqualTo(expected.GetProperty("channels").GetInt32());
        await Assert.That(color.BitsPerSample).IsEqualTo(expected.GetProperty("bits").GetInt32());
        await Assert.That(color.SampleFormat.ToString()).IsEqualTo(expected.GetProperty("sampleFormat").GetString());
        await Assert.That(color.AlphaMode.ToString()).IsEqualTo(expected.GetProperty("alpha").GetString());
        await Assert.That(color.HasIccProfile).IsEqualTo(expected.GetProperty("icc").GetBoolean());
        await Assert.That(color.Cicp).IsEqualTo(Cicp(expected.GetProperty("cicp")));
        await Assert.That(info.Compression.ToString()).IsEqualTo(expected.GetProperty("compression").GetString());
        await Assert.That(info.FrameCount).IsEqualTo(expected.GetProperty("frames").GetInt32());
        JsonElement resolution = expected.GetProperty("resolution");
        if (resolution.ValueKind == JsonValueKind.Null)
        {
            await Assert.That(info.Resolution).IsNull();
        }
        else
        {
            await Assert.That(info.Resolution).IsNotNull();
            await Assert.That(info.Resolution!.Value.Horizontal).IsEqualTo(resolution[0].GetSingle()).Within(1e-3f);
            await Assert.That(info.Resolution.Value.Vertical).IsEqualTo(resolution[1].GetSingle()).Within(1e-3f);
        }
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Color_DoesNotDependOnIgnoreMetadata_OrReserveMetadata(string file)
    {
        byte[] data = Corpus.Read("identify/" + file);
        ImageInfo reading = Image.Identify(data);
        if (reading.Format == ImageFormat.Jpeg && reading.Color.HasIccProfile)
        {
            return;   // JPEG Identify copies and counts the ICC chunks, as in 0.8.4
        }

        ImageInfo ignoring = Image.Identify(data, new DecoderOptions { IgnoreMetadata = true, MaxMetadataBytes = 0 });
        await Assert.That(ignoring.Color).IsEqualTo(reading.Color);
        await Assert.That(ignoring.Compression).IsEqualTo(reading.Compression);
    }

    [Test]
    public async Task Jpeg_IccPresence_StillCountsAgainstMaxMetadataBytes()
    {
        byte[] data = Corpus.Read("identify/jpeg/icc_two_chunks.jpg");
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(data, new DecoderOptions { MaxMetadataBytes = 1000 }));
        await Assert.That(Image.Identify(data, new DecoderOptions { IgnoreMetadata = true }).Color.HasIccProfile).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(CorpusFiles))]
    public async Task EveryCorpusFile_ReportsAConsistentColorDescription(string file)
    {
        ImageInfo info;
        try
        {
            info = Image.Identify(Corpus.Read(file));
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
            return;
        }

        ImageColorInfo color = info.Color;
        int colorChannels = color.Model switch
        {
            ImageColorModel.Gray or ImageColorModel.Palette => 1,
            ImageColorModel.Cmyk => 4,
            ImageColorModel.Rgb or ImageColorModel.Lab => 3,
            _ => 1,
        };
        await Assert.That(color.ChannelCount).IsGreaterThanOrEqualTo(colorChannels);
        if (color.Model == ImageColorModel.Palette)
        {
            await Assert.That(color.SampleFormat).IsEqualTo(ImageSampleFormat.UnsignedInteger);
            await Assert.That(color.BitsPerSample).IsLessThanOrEqualTo(8);
        }

        if (color.SampleFormat == ImageSampleFormat.Float)
        {
            await Assert.That(color.BitsPerSample is 16 or 24 or 32 or 64).IsTrue();
        }

        // make_reference.py records every ICO as having alpha; PNG entries report their own (as in 0.8.4).
        if (Corpus.References.TryGetValue(file, out ReferenceEntry? reference) && info.Format != ImageFormat.Ico)
        {
            await Assert.That(color.HasAlpha).IsEqualTo(reference.HasAlpha);
        }
    }

    [Test]
    public async Task CicpColorDescription_ChecksCodePoints_AndDetectsHdrTransfers()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CicpColorDescription(-1, 13, 0, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CicpColorDescription(1, 256, 0, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CicpColorDescription(1, 13, 256, true));
        await Assert.That(CicpColorDescription.Srgb).IsEqualTo(new CicpColorDescription(1, 13, 0, true));
        await Assert.That(CicpColorDescription.Srgb.IsHdrTransfer).IsFalse();
        await Assert.That(new CicpColorDescription(9, 16, 0, true).IsHdrTransfer).IsTrue();
        await Assert.That(new CicpColorDescription(9, 18, 9, false).IsHdrTransfer).IsTrue();
        await Assert.That(new CicpColorDescription(9, 14, 9, false).IsHdrTransfer).IsFalse();
    }

    [Test]
    [Arguments(0f, 72f)]
    [Arguments(72f, -1f)]
    [Arguments(float.NaN, 72f)]
    [Arguments(72f, float.PositiveInfinity)]
    public async Task ImageResolution_RejectsValuesThatAreNotPositiveAndFinite(float horizontal, float vertical)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ImageResolution(horizontal, vertical));
        await Assert.That(new ImageResolution(300, 150)).IsEqualTo(new ImageResolution(300, 150));
    }

    [Test]
    public async Task ImageColorInfo_ChecksRanges_AndDerivesHasAlpha()
    {
        ImageColorInfo rgb = new()
        {
            Model = ImageColorModel.Rgb,
            ChannelCount = 3,
            BitsPerSample = 8,
            SampleFormat = ImageSampleFormat.UnsignedInteger,
            AlphaMode = ImageAlphaMode.None,
            HasIccProfile = false,
        };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = rgb with { ChannelCount = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = rgb with { BitsPerSample = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = rgb with { BitsPerSample = 65 });
        await Assert.That(rgb.HasAlpha).IsFalse();
        await Assert.That((rgb with { AlphaMode = ImageAlphaMode.Premultiplied }).HasAlpha).IsTrue();
        await Assert.That((rgb with { AlphaMode = ImageAlphaMode.Straight }).HasAlpha).IsTrue();
    }

    [Test]
    public async Task ImageInfo_FrameCount_IsNullOrPositive()
    {
        ImageInfo info = Image.Identify(Corpus.Read("identify/png/rgba8.png"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = info with { FrameCount = 0 });
        await Assert.That((info with { FrameCount = null }).FrameCount).IsNull();
    }

    private static CicpColorDescription? Cicp(JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? null
        : new CicpColorDescription(value[0].GetInt32(), value[1].GetInt32(), value[2].GetInt32(), value[3].GetBoolean());
}
