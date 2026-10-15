using System.Drawing;
using PictureSharp.Application.Abstractions;
using PictureSharp.Composition;
using PictureSharp.Formats.Bmp;
using PictureSharp.Infrastructure.Codecs.Png;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class RgbaAndFormatTests
{
    [Test]
    public async Task Rgba32_ConvertsToAndFromColor()
    {
        var p = new Rgba32(10, 20, 30, 40);
        Color c = p;
        await Assert.That(c.ToArgb()).IsEqualTo(Color.FromArgb(40, 10, 20, 30).ToArgb());
        await Assert.That((Rgba32)c).IsEqualTo(p);
        await Assert.That(p.ToArgb()).IsEqualTo(0x280A141Eu);
        await Assert.That(Rgba32.FromArgb(p.ToArgb())).IsEqualTo(p);
        await Assert.That(p.PackedValue).IsEqualTo(0x281E140Au);
    }

    [Test]
    [Arguments("a.webp", "WebP")]
    [Arguments("A.PNG", "PNG")]
    [Arguments("x/y/photo.jpeg", "JPEG")]
    [Arguments(".jpg", "JPEG")]
    [Arguments("jfif", "JPEG")]
    public async Task ImageFormat_FromExtension(string input, string expected) =>
        await Assert.That(ImageFormat.FromExtension(input)?.Name).IsEqualTo(expected);

    [Test]
    public void ImageFormat_FromExtension_Unknown() => Assert.Null(ImageFormat.FromExtension("file.tiff"));

    public static TestCases<string> AllFiles => Corpus.Files("");

    [Test]
    [MethodDataSource(nameof(AllFiles))]
    public async Task FormatDetector_RecognizesCorpus(string file)
    {
        ImageFormat? format = Image.DetectFormat(Corpus.Read(file).AsSpan(0, LibraryServices.Codecs.HeaderSize));
        Assert.NotNull(format);
        await Assert.That(format.Name).IsEqualTo(Corpus.References[file].Format);
    }

    [Test]
    public void FormatDetector_RejectsGarbage()
    {
        Assert.Null(Image.DetectFormat("RIFF\0\0\0\0WAVE"u8));
        Assert.Null(Image.DetectFormat([]));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load("not an image"u8));
    }

    public static TestCases<string> AllFormats => [.. ImageFormat.All.Select(f => f.Name)];

    [Test]
    [MethodDataSource(nameof(AllFormats))]
    public async Task CodecRegistry_CoversEveryFormat(string name)
    {
        ImageFormat format = ImageFormat.All.Single(f => f.Name == name);
        IImageFormatDetector? detector = LibraryServices.Codecs.GetDetector(format);
        Assert.NotNull(detector);
        await Assert.That(detector.HeaderSize <= LibraryServices.Codecs.HeaderSize).IsTrue();
        await Assert.That(LibraryServices.Codecs.GetDecoder(format)?.Format).IsEqualTo(format);
        await Assert.That(LibraryServices.Codecs.GetEncoder(format)?.Format).IsEqualTo(format);
        await Assert.That(format.CanDecode).IsTrue();
        await Assert.That(format.CanEncode).IsTrue();
        await Assert.That(LibraryServices.Codecs.GetEncoder(format)!.CreateDefaultOptions().Format).IsEqualTo(format);
    }

    [Test]
    public async Task FormatDetector_HeaderSizeCoversWebP() => await Assert.That(LibraryServices.Codecs.HeaderSize).IsEqualTo(12);

    [Test]
    public void Encoder_RejectsOptionsOfAnotherFormat()
    {
        IImageEncoder encoder = PictureSharp.Infrastructure.Codecs.Png.PngEncoder.Instance;
        using Image image = Image.LoadPixelData([default(Rgba32)], 1, 1);
        Assert.ThrowsExactly<ArgumentException>(() => encoder.Encode(image, Stream.Null, new BmpEncoderOptions(), CancellationToken.None));
    }

    [Test]
    public async Task Rgba32_ParsesHexAndNames()
    {
        await Assert.That(Rgba32.Parse("#123")).IsEqualTo(new Rgba32(0x11, 0x22, 0x33));
        await Assert.That(Rgba32.Parse("1234")).IsEqualTo(new Rgba32(0x11, 0x22, 0x33, 0x44));
        await Assert.That(Rgba32.Parse("#abcdef")).IsEqualTo(new Rgba32(0xAB, 0xCD, 0xEF));
        await Assert.That(Rgba32.Parse(" #01020304 ")).IsEqualTo(new Rgba32(1, 2, 3, 4));
        await Assert.That(Rgba32.Parse("cornflowerblue")).IsEqualTo(Rgba32.FromColor(Color.CornflowerBlue));
        await Assert.That(new Rgba32(1, 2, 3, 4).ToHex()).IsEqualTo("#01020304");
        await Assert.That(Rgba32.TryParse(new Rgba32(9, 8, 7, 6).ToHex(), out Rgba32 back)).IsTrue();
        await Assert.That(back).IsEqualTo(new Rgba32(9, 8, 7, 6));
        await Assert.That(Rgba32.TryParse("#12345", out _)).IsFalse();
        await Assert.That(Rgba32.TryParse("#red", out _)).IsFalse();
        await Assert.That(Rgba32.TryParse("NoSuchColor", out _)).IsFalse();
        await Assert.That(Rgba32.TryParse((string?)null, out _)).IsFalse();
        Assert.ThrowsExactly<FormatException>(() => Rgba32.Parse("xyz"));
        (byte r, byte g, byte b, byte a) = new Rgba32(1, 2, 3, 4);
        await Assert.That(((int, int, int, int))((r, g, b, a))).IsEqualTo((1, 2, 3, 4));
    }
}
