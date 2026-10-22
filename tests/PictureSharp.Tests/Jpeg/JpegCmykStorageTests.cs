using System.Text.Json;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Jpeg;

public class JpegCmykStorageTests
{
    private static readonly Lazy<JsonDocument> PlainReference =
        new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Assets, "jpeg_cmyk_reference.json"))));

    private static readonly DecoderOptions Plain = new() { Jpeg = new JpegDecoderOptions { CmykStorage = JpegCmykStorage.Plain } };

    public static IEnumerable<string> FourComponentFiles() =>
        [.. PlainReference.Value.RootElement.GetProperty("files").EnumerateObject().Select(file => file.Name)];

    [Test]
    [MethodDataSource(nameof(FourComponentFiles))]
    public async Task Plain_MatchesPillowsConversionOfTheStoredValues(string file)
    {
        JsonElement reference = PlainReference.Value.RootElement.GetProperty("files").GetProperty(file);
        using Image image = Image.Load(Corpus.Read(file), Plain);
        await Assert.That((image.Width, image.Height)).IsEqualTo((reference.GetProperty("width").GetInt32(), reference.GetProperty("height").GetInt32()));
        await Assert.That(Corpus.Sha256(image.ToPixelData())).IsEqualTo(reference.GetProperty("sha256").GetString());
    }

    [Test]
    [MethodDataSource(nameof(FourComponentFiles))]
    public async Task AdobeInverted_IsTheDefault(string file)
    {
        byte[] data = Corpus.Read(file);
        using Image byDefault = Image.Load(data);
        using Image inverted = Image.Load(data, new DecoderOptions { Jpeg = new JpegDecoderOptions { CmykStorage = JpegCmykStorage.AdobeInverted } });
        using Image plain = Image.Load(data, Plain);
        await Assert.That(inverted.GetPixelSpan().SequenceEqual(byDefault.GetPixelSpan())).IsTrue();
        await Assert.That(plain.GetPixelSpan().SequenceEqual(byDefault.GetPixelSpan())).IsFalse();
    }

    [Test]
    [Arguments("jpeg/odd_420.jpg")]
    [Arguments("jpeg/crafted_adobe_rgb.jpg")]
    [Arguments("jpeg/gray.jpg")]
    public async Task Plain_IsIgnoredWithoutFourComponents(string file)
    {
        byte[] data = Corpus.Read(file);
        using Image byDefault = Image.Load(data);
        using Image plain = Image.Load(data, Plain);
        await Assert.That(plain.GetPixelSpan().SequenceEqual(byDefault.GetPixelSpan())).IsTrue();
    }

    [Test]
    public async Task UnknownStorage_IsRejected()
    {
        await Assert.That(() => new JpegDecoderOptions { CmykStorage = (JpegCmykStorage)2 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new DecoderOptions { Jpeg = null! }).Throws<ArgumentNullException>();
    }
}
