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

    public static TheoryData<ImageFormat, EncoderOptions> Formats => new()
    {
        { ImageFormat.WebP, new WebPEncoderOptions() },
        { ImageFormat.Png, new PngEncoderOptions() },
        { ImageFormat.Jpeg, new JpegEncoderOptions() },
        { ImageFormat.Bmp, new BmpEncoderOptions() },
        { ImageFormat.Gif, new GifEncoderOptions() },
        { ImageFormat.Pcx, new PcxEncoderOptions() },
        { ImageFormat.Ico, new IcoEncoderOptions() },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Encode_UsesDefaultOptionsAndMatchesStreamSave(ImageFormat format, EncoderOptions options)
    {
        using Image image = Image.Load(Corpus.Read("png/tiny.png"));
        using var stream = new MemoryStream();
        image.Save(stream, options);
        Assert.Equal(stream.ToArray(), image.Encode(options));
        Assert.Equal(stream.ToArray(), image.Encode(format));
        Assert.Equal(format, Image.Identify(image.Encode(format)).Format);
    }

    [Theory]
    [InlineData("a.webp", "WebP")]
    [InlineData("a.png", "PNG")]
    [InlineData("a.apng", "PNG")]
    [InlineData("a.jpg", "JPEG")]
    [InlineData("a.bmp", "BMP")]
    [InlineData("a.DIB", "BMP")]
    [InlineData("a.gif", "GIF")]
    [InlineData("a.pcx", "PCX")]
    [InlineData("a.ico", "ICO")]
    public void SaveToFile_ChoosesFormatFromExtension_AndMatchesEncode(string file, string format)
    {
        using Image image = Image.Load(Corpus.Read("png/tiny.png"));
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, file);
            image.Save(path);
            ImageFormat expected = ImageFormat.All.Single(f => f.Name == format);
            Assert.Equal(image.Encode(expected), File.ReadAllBytes(path));
            using Image loaded = Image.Load(path);
            Assert.Same(expected, loaded.SourceFormat);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Encode_ValidatesNullAndDisposedImage()
    {
        using var image = new Image(1, 1);
        Assert.Equal("format", Assert.Throws<ArgumentNullException>(() => image.Encode((ImageFormat)null!)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => image.Encode((EncoderOptions)null!)).ParamName);
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => image.Encode(ImageFormat.Png));
        Assert.Throws<ObjectDisposedException>(() => image.Encode(new PngEncoderOptions()));
    }

    [Theory]
    [InlineData("png/exif.png")]
    [InlineData("jpeg/q90_444.jpg")]
    [InlineData("webp/anim/alpha_lossless.webp")]
    [InlineData("gif/pil_anim_alpha.gif")]
    public void Identify_ArrayMemoryAndSpanAgree_AndRespectSlices(string path)
    {
        byte[] data = Corpus.Read(path);
        byte[] padded = [99, 98, .. data, 97];
        ImageInfo expected = Image.Identify(data);
        Assert.Equal(expected, Image.Identify(padded.AsMemory(2, data.Length)));
        Assert.Equal(expected, Image.Identify(padded.AsSpan(2, data.Length)));
        Assert.Equal(expected, Image.Identify((ReadOnlyMemory<byte>)data));
        Assert.Equal(expected, Image.Identify((ReadOnlySpan<byte>)data));
        Assert.Throws<ImageFormatException>(() => Image.Identify(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Identify_MemoryDoesNotCopyLargeInput()
    {
        byte[] png = Corpus.Read("png/tiny.png");
        byte[] padded = new byte[2 * 1024 * 1024];
        png.CopyTo(padded, 0);
        ReadOnlyMemory<byte> memory = padded;
        ImageInfo expected = Image.Identify(memory);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ImageInfo actual = Image.Identify(memory);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        Assert.True(allocated < padded.Length / 2, $"Identify allocated {allocated} bytes for {padded.Length} input bytes.");
    }

    [Fact]
    public void LoadAndSave_ThroughEveryEntryPoint()
    {
        using Image img = ImageTests.Pattern(5, 4);
        byte[] png = img.ToPng();
        using Image fromArray = Image.Load(png);
        using Image fromSpan = Image.Load(png.AsSpan());
        using Image fromStream = Image.Load(new MemoryStream(png));
        Assert.Equal(ImageFormat.Png, fromArray.SourceFormat);
        Assert.Equal(Pixels(fromArray), Pixels(fromSpan));
        Assert.Equal(Pixels(fromArray), Pixels(fromStream));
        Assert.Equal(ImageFormat.Png, Image.DetectFormat(png));

        using var stream = new MemoryStream();
        img.SaveAsWebP(stream, new() { Lossless = true, Exact = true });
        stream.Position = 0;
        Assert.Equal(ImageFormat.WebP, Image.DetectFormat(stream));
        Assert.Equal(0, stream.Position);
        using Image webp = Image.Load(stream);
        Assert.Equal(Pixels(img), Pixels(webp));
    }

    [Fact]
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
            Assert.Equal(ImageFormat.Png, Image.DetectFormat(png));
            ImageInfo info = await Image.IdentifyAsync(webp);
            Assert.Equal((6, 3), (info.Width, info.Height));
            using Image back = await Image.LoadAsync(webp);
            Assert.Equal(Pixels(img), Pixels(back));
            using Image limited = await Image.LoadAsync(png, new DecoderOptions { FirstFrameOnly = true });
            Assert.Equal(Pixels(img), Pixels(limited));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
