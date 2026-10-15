using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.IO;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class EncodingAndIdentificationApiTests
{
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
        using Image image = ImageReader.Load(Corpus.Read("png/tiny.png"));
        using var stream = new MemoryStream();
        ImageWriter.Save(image, stream, options);
        Assert.Equal(stream.ToArray(), ImageWriter.Encode(image, options));
        Assert.Equal(stream.ToArray(), ImageWriter.Encode(image, format));
        Assert.Equal(format, ImageReader.Identify(ImageWriter.Encode(image, format)).Format);
    }

    [Fact]
    public void Encode_ValidatesNullAndDisposedImage()
    {
        using var image = new Image(1, 1);
        Assert.Equal("format", Assert.Throws<ArgumentNullException>(() => ImageWriter.Encode(image, (ImageFormat)null!)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => ImageWriter.Encode(image, (EncoderOptions)null!)).ParamName);
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ImageWriter.Encode(image, ImageFormat.Png));
        Assert.Throws<ObjectDisposedException>(() => ImageWriter.Encode(image, new PngEncoderOptions()));
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
        ImageInfo expected = ImageReader.Identify(data);
        Assert.Equal(expected, ImageReader.Identify(padded.AsMemory(2, data.Length)));
        Assert.Equal(expected, ImageReader.Identify(padded.AsSpan(2, data.Length)));
        Assert.Equal(expected, ImageReader.Identify((ReadOnlyMemory<byte>)data));
        Assert.Equal(expected, ImageReader.Identify((ReadOnlySpan<byte>)data));
        Assert.Throws<ImageFormatException>(() => ImageReader.Identify(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Identify_MemoryDoesNotCopyLargeInput()
    {
        byte[] png = Corpus.Read("png/tiny.png");
        byte[] padded = new byte[2 * 1024 * 1024];
        png.CopyTo(padded, 0);
        ReadOnlyMemory<byte> memory = padded;
        ImageInfo expected = ImageReader.Identify(memory);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ImageInfo actual = ImageReader.Identify(memory);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        Assert.True(allocated < padded.Length / 2, $"Identify allocated {allocated} bytes for {padded.Length} input bytes.");
    }
}
