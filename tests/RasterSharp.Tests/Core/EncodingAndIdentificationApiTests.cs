using RasterSharp.Tests.Infrastructure;

namespace RasterSharp.Tests.Core;

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
        using Image image = Image.Load(Corpus.Read("png/tiny.png"));
        using var stream = new MemoryStream();
        image.Save(stream, options);
        Assert.Equal(stream.ToArray(), image.Encode(options));
        Assert.Equal(stream.ToArray(), image.Encode(format));
        Assert.Equal(format, Image.Identify(image.Encode(format)).Format);
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
}
