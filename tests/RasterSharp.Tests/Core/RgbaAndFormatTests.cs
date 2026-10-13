using System.Drawing;
using RasterSharp.Formats;
using RasterSharp.Tests.Infrastructure;

namespace RasterSharp.Tests.Core;

public class RgbaAndFormatTests
{
    [Fact]
    public void Rgba32_ConvertsToAndFromColor()
    {
        var p = new Rgba32(10, 20, 30, 40);
        Color c = p;
        Assert.Equal(Color.FromArgb(40, 10, 20, 30).ToArgb(), c.ToArgb());
        Assert.Equal(p, (Rgba32)c);
        Assert.Equal(0x280A141Eu, p.ToArgb());
        Assert.Equal(p, Rgba32.FromArgb(p.ToArgb()));
        Assert.Equal(0x281E140Au, p.PackedValue);
    }

    [Theory]
    [InlineData("a.webp", "WebP")]
    [InlineData("A.PNG", "PNG")]
    [InlineData("x/y/photo.jpeg", "JPEG")]
    [InlineData(".jpg", "JPEG")]
    [InlineData("jfif", "JPEG")]
    public void ImageFormat_FromExtension(string input, string expected) =>
        Assert.Equal(expected, ImageFormat.FromExtension(input)?.Name);

    [Fact]
    public void ImageFormat_FromExtension_Unknown() => Assert.Null(ImageFormat.FromExtension("file.tiff"));

    public static TheoryData<string> AllFiles => Corpus.Files("");

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void FormatDetector_RecognizesCorpus(string file)
    {
        ImageFormat? format = Image.DetectFormat(Corpus.Read(file).AsSpan(0, FormatDetector.HeaderSize));
        Assert.NotNull(format);
        Assert.Equal(Corpus.References[file].Format, format.Name);
    }

    [Fact]
    public void FormatDetector_RejectsGarbage()
    {
        Assert.Null(FormatDetector.Detect("RIFF\0\0\0\0WAVE"u8));
        Assert.Null(FormatDetector.Detect([]));
        Assert.Throws<ImageFormatException>(() => Image.Load("not an image"u8));
    }

    public static TheoryData<string> AllFormats => [.. ImageFormat.All.Select(f => f.Name)];

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void CodecRegistry_CoversEveryFormat(string name)
    {
        ImageFormat format = ImageFormat.All.Single(f => f.Name == name);
        IImageFormatDetector? detector = CodecRegistry.GetDetector(format);
        Assert.NotNull(detector);
        Assert.True(detector.HeaderSize <= FormatDetector.HeaderSize);
        Assert.Equal(format, CodecRegistry.GetDecoder(format)?.Format);
        Assert.Equal(format, CodecRegistry.GetEncoder(format)?.Format);
        Assert.True(format.CanDecode);
        Assert.True(format.CanEncode);
        Assert.Equal(format, format.CreateDefaultEncoderOptions().Format);
    }

    [Fact]
    public void FormatDetector_HeaderSizeCoversWebP() => Assert.Equal(12, FormatDetector.HeaderSize);

    [Fact]
    public void Encoder_RejectsOptionsOfAnotherFormat()
    {
        IImageEncoder encoder = Formats.Png.PngEncoder.Instance;
        using Image image = Image.LoadPixelData([default(Rgba32)], 1, 1);
        Assert.Throws<ArgumentException>(() => encoder.Encode(image, Stream.Null, new BmpEncoderOptions()));
    }
}
