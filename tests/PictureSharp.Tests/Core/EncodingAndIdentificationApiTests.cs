using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class EncodingAndIdentificationApiTests
{
    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan().ToArray();

    public static TestCases<ImageFormat, EncoderOptions> Formats => new()
    {
        { ImageFormat.WebP, new WebPEncoderOptions() },
        { ImageFormat.Png, new PngEncoderOptions() },
        { ImageFormat.Jpeg, new JpegEncoderOptions() },
        { ImageFormat.Bmp, new BmpEncoderOptions() },
        { ImageFormat.Gif, new GifEncoderOptions() },
        { ImageFormat.Pcx, new PcxEncoderOptions() },
        { ImageFormat.Ico, new IcoEncoderOptions() },
    };

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task Encode_UsesDefaultOptionsAndMatchesStreamSave(ImageFormat format, EncoderOptions options)
    {
        using Image image = Image.Load(Corpus.Read("png/tiny.png"));
        using var stream = new MemoryStream();
        image.Save(stream, options);
        await Assert.That(image.Encode(options)).IsEquivalentTo(stream.ToArray(), CollectionOrdering.Matching);
        await Assert.That(image.Encode(format)).IsEquivalentTo(stream.ToArray(), CollectionOrdering.Matching);
        await Assert.That(Image.Identify(image.Encode(format)).Format).IsEqualTo(format);
    }

    [Test]
    [Arguments("a.webp", "WebP")]
    [Arguments("a.png", "PNG")]
    [Arguments("a.apng", "PNG")]
    [Arguments("a.jpg", "JPEG")]
    [Arguments("a.bmp", "BMP")]
    [Arguments("a.DIB", "BMP")]
    [Arguments("a.gif", "GIF")]
    [Arguments("a.pcx", "PCX")]
    [Arguments("a.ico", "ICO")]
    public async Task SaveToFile_ChoosesFormatFromExtension_AndMatchesEncode(string file, string format)
    {
        using Image image = Image.Load(Corpus.Read("png/tiny.png"));
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, file);
            image.Save(path);
            ImageFormat expected = ImageFormat.All.Single(f => f.Name == format);
            await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(image.Encode(expected), CollectionOrdering.Matching);
            using Image loaded = Image.Load(path);
            await Assert.That(loaded.SourceFormat).IsSameReferenceAs(expected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Encode_ValidatesNullAndDisposedImage()
    {
        using var image = new Image(1, 1);
        await Assert.That(Assert.ThrowsExactly<ArgumentNullException>(() => image.Encode((ImageFormat)null!)).ParamName).IsEqualTo("format");
        await Assert.That(Assert.ThrowsExactly<ArgumentNullException>(() => image.Encode((EncoderOptions)null!)).ParamName).IsEqualTo("options");
        image.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => image.Encode(ImageFormat.Png));
        Assert.ThrowsExactly<ObjectDisposedException>(() => image.Encode(new PngEncoderOptions()));
    }

    [Test]
    [Arguments("png/exif.png")]
    [Arguments("jpeg/q90_444.jpg")]
    [Arguments("webp/anim/alpha_lossless.webp")]
    [Arguments("gif/pil_anim_alpha.gif")]
    public async Task Identify_ArrayMemoryAndSpanAgree_AndRespectSlices(string path)
    {
        byte[] data = Corpus.Read(path);
        byte[] padded = [99, 98, .. data, 97];
        ImageInfo expected = Image.Identify(data);
        await Assert.That(Image.Identify(padded.AsMemory(2, data.Length))).IsEqualTo(expected);
        await Assert.That(Image.Identify(padded.AsSpan(2, data.Length))).IsEqualTo(expected);
        await Assert.That(Image.Identify((ReadOnlyMemory<byte>)data)).IsEqualTo(expected);
        await Assert.That(Image.Identify((ReadOnlySpan<byte>)data)).IsEqualTo(expected);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(ReadOnlyMemory<byte>.Empty));
    }

    [Test]
    public async Task Identify_MemoryDoesNotCopyLargeInput()
    {
        byte[] png = Corpus.Read("png/tiny.png");
        byte[] padded = new byte[2 * 1024 * 1024];
        png.CopyTo(padded, 0);
        ReadOnlyMemory<byte> memory = padded;
        ImageInfo expected = Image.Identify(memory);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ImageInfo actual = Image.Identify(memory);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(actual).IsEqualTo(expected);
        await Assert.That(allocated < padded.Length / 2).IsTrue().Because($"Identify allocated {allocated} bytes for {padded.Length} input bytes.");
    }

    [Test]
    public async Task LoadAndSave_ThroughEveryEntryPoint()
    {
        using Image img = ImageTests.Pattern(5, 4);
        byte[] png = img.ToPng();
        using Image fromArray = Image.Load(png);
        using Image fromSpan = Image.Load(png.AsSpan());
        using Image fromStream = Image.Load(new MemoryStream(png));
        await Assert.That(fromArray.SourceFormat).IsEqualTo(ImageFormat.Png);
        await Assert.That(Pixels(fromSpan)).IsEquivalentTo(Pixels(fromArray), CollectionOrdering.Matching);
        await Assert.That(Pixels(fromStream)).IsEquivalentTo(Pixels(fromArray), CollectionOrdering.Matching);
        await Assert.That(Image.DetectFormat(png)).IsEqualTo(ImageFormat.Png);

        using var stream = new MemoryStream();
        img.SaveAsWebP(stream, new() { Lossless = true, Exact = true });
        stream.Position = 0;
        await Assert.That(Image.DetectFormat(stream)).IsEqualTo(ImageFormat.WebP);
        await Assert.That(stream.Position).IsEqualTo(0);
        using Image webp = Image.Load(stream);
        await Assert.That(Pixels(webp)).IsEquivalentTo(Pixels(img), CollectionOrdering.Matching);
    }

    [Test]
    public async Task AsyncEntryPoints_RoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"picturesharp-async-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using Image img = ImageTests.Pattern(6, 3);
            string png = Path.Combine(dir, "a.png");
            string webp = Path.Combine(dir, "a.webp");
            await img.SaveAsync(png);
            await img.SaveAsWebPAsync(webp, new() { Lossless = true, Exact = true });
            await Assert.That(Image.DetectFormat(png)).IsEqualTo(ImageFormat.Png);
            ImageInfo info = await Image.IdentifyAsync(webp);
            await Assert.That((info.Width, info.Height)).IsEqualTo((6, 3));
            using Image back = await Image.LoadAsync(webp);
            await Assert.That(Pixels(back)).IsEquivalentTo(Pixels(img), CollectionOrdering.Matching);
            using Image limited = await Image.LoadAsync(png, new DecoderOptions { FirstFrameOnly = true });
            await Assert.That(Pixels(limited)).IsEquivalentTo(Pixels(img), CollectionOrdering.Matching);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
