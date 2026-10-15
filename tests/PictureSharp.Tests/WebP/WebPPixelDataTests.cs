using PictureSharp.IO;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class WebPPixelDataTests
{
    [Fact]
    public void Load_FirstFrame_ExportsRgbaAndBgra()
    {
        byte[] data = Corpus.Read("webp/alpha/photo_alpha_a_default.webp");
        using Image first = ImageReader.Load(data, new DecoderOptions { FirstFrameOnly = true });
        byte[] rgba = first.ToPixelData();
        (int w, int h) = (first.Width, first.Height);
        using Image image = ImageReader.Load(data);
        Assert.Equal((image.Width, image.Height), (w, h));
        Assert.Equal(image.ToPixelData(), rgba);
        byte[] bgra = first.ToPixelData(PixelFormat.Bgra32);
        Assert.Equal(rgba[2], bgra[0]);
        Assert.Equal(rgba[0], bgra[2]);
    }

    [Fact]
    public void CopyPixelDataTo_WithStride()
    {
        byte[] data = Corpus.Read("webp/lossless/graphic_ll_z6.webp");
        ImageInfo info = ImageReader.Identify(data);
        int stride = (info.Width * 3) + 5;
        var buffer = new byte[stride * info.Height];
        using Image first = ImageReader.Load(data, new DecoderOptions { FirstFrameOnly = true });
        first.CopyPixelDataTo(buffer, PixelFormat.Rgb24, stride);
        System.Drawing.Size size = first.Size;
        Assert.Equal(new System.Drawing.Size(info.Width, info.Height), size);
        using Image image = ImageReader.Load(data);
        Assert.Equal(image[1, 1].G, buffer[stride + 4]);
    }

    [Fact]
    public void Identify_DetectsFormatAndRejectsCorruptWebP()
    {
        Assert.Equal(ImageFormat.Png, ImageReader.Identify(Corpus.Read("png/rgb8.png")).Format);
        Assert.Throws<ImageFormatException>(() => ImageReader.Identify("RIFF\x10\0\0\0WEBPVP8 "u8));
    }

    [Fact]
    public void Load_Animation_FirstFrameOnlyMatchesReference()
    {
        byte[] data = Corpus.Read("webp/anim/alpha_lossless.webp");
        using Image first = ImageReader.Load(data, new DecoderOptions { FirstFrameOnly = true });
        Assert.Single(first.Frames);
        byte[] rgba = first.ToPixelData();
        Assert.Equal(Corpus.References["webp/anim/alpha_lossless.webp"].Frames[0].Sha256, Corpus.Sha256(rgba));
    }
}
