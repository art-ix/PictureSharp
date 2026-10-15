using System.Drawing;

namespace PictureSharp.Tests.Core;

public class ImageTests
{
    internal static Image Pattern(int w, int h)
    {
        var img = new Image(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                img[x, y] = new Rgba32((byte)(x * 7), (byte)(y * 13), (byte)(x ^ y), (byte)(255 - x - y));
            }
        }

        return img;
    }

    [Fact]
    public void NewImage_IsTransparent()
    {
        using var img = new Image(3, 2);
        Assert.Equal(new Size(3, 2), img.Size);
        Assert.True(img.HasAlpha);
        Assert.Equal(1, img.FrameCount);
        Assert.False(img.IsAnimated);
        Assert.Null(img.SourceFormat);
        Assert.Equal(new Rectangle(0, 0, 3, 2), img.Bounds);
        Assert.All(img.GetPixelSpan().ToArray(), p => Assert.Equal(Rgba32.Transparent, p));
        Assert.False(img.IsOpaque());
    }

    [Fact]
    public void BackgroundConstructor_FillsAndIsOpaque()
    {
        using var img = new Image(4, 4, Color.Red);
        Assert.Equal(Rgba32.FromColor(Color.Red), img[3, 3]);
        Assert.True(img.IsOpaque());
        Assert.False(img.HasAlpha);
    }

    [Fact]
    public void Indexer_AcceptsColor_AndChecksBounds()
    {
        using var img = new Image(2, 2);
        img[1, 1] = Color.FromArgb(1, 2, 3, 4);
        Assert.Equal(new Rgba32(2, 3, 4, 1), img[1, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => img[2, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => img[0, -1] = Color.Red);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-5, 3)]
    public void InvalidSize_Throws(int w, int h) => Assert.Throws<ArgumentOutOfRangeException>(() => new Image(w, h));

    [Theory]
    [InlineData(PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Bgra32)]
    [InlineData(PixelFormat.Argb32)]
    public void PixelArray_RoundTrip_Lossless(PixelFormat format)
    {
        using Image img = Pattern(5, 3);
        byte[] bytes = img.ToPixelData(format);
        Assert.Equal(5 * 3 * 4, bytes.Length);
        using Image back = Image.LoadPixelData(bytes, 5, 3, format);
        Assert.Equal(img.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(PixelFormat.Rgb24)]
    [InlineData(PixelFormat.Bgr24)]
    public void PixelArray_Rgb_DropsAlpha(PixelFormat format)
    {
        using Image img = Pattern(4, 4);
        using Image back = Image.LoadPixelData(img.ToPixelData(format), 4, 4, format);
        Assert.False(back.HasAlpha);
        for (int i = 0; i < 16; i++)
        {
            Rgba32 a = img.GetPixelSpan()[i];
            Rgba32 b = back.GetPixelSpan()[i];
            Assert.Equal((a.R, a.G, a.B, (byte)255), (b.R, b.G, b.B, b.A));
        }
    }

    [Fact]
    public void Bgra32_ByteOrder()
    {
        using var img = new Image(1, 1);
        img[0, 0] = new Rgba32(1, 2, 3, 4);
        Assert.Equal(new byte[] { 3, 2, 1, 4 }, img.ToPixelData(PixelFormat.Bgra32));
        Assert.Equal(new byte[] { 4, 1, 2, 3 }, img.ToPixelData(PixelFormat.Argb32));
        Assert.Equal(new byte[] { 3, 2, 1 }, img.ToPixelData(PixelFormat.Bgr24));
    }

    [Fact]
    public void Gray8_UsesBt601()
    {
        using var img = new Image(3, 1);
        img[0, 0] = new Rgba32(255, 0, 0);
        img[1, 0] = new Rgba32(0, 255, 0);
        img[2, 0] = new Rgba32(255, 255, 255);
        Assert.Equal(new byte[] { 76, 150, 255 }, img.ToPixelData(PixelFormat.Gray8));
    }

    [Theory]
    [InlineData(PixelFormat.Rgba32Premultiplied)]
    [InlineData(PixelFormat.Bgra32Premultiplied)]
    public void Premultiplied_RoundTrip_OpaqueExact_TransparentZero(PixelFormat format)
    {
        using var img = new Image(3, 1);
        img[0, 0] = new Rgba32(200, 100, 50, 255);
        img[1, 0] = new Rgba32(200, 100, 50, 0);
        img[2, 0] = new Rgba32(200, 100, 50, 128);
        byte[] bytes = img.ToPixelData(format);
        using Image back = Image.LoadPixelData(bytes, 3, 1, format);
        Assert.Equal(img[0, 0], back[0, 0]);
        Assert.Equal(Rgba32.Transparent, back[1, 0]);
        Rgba32 half = back[2, 0];
        Assert.InRange(half.R, 199, 201);
        Assert.InRange(half.G, 99, 101);
        Assert.Equal(128, half.A);
    }

    [Fact]
    public void LoadPixelData_WithStride()
    {
        byte[] src = [1, 2, 3, 9, 4, 5, 6, 9];  // 1 px per row RGB24, stride 4
        using Image img = Image.LoadPixelData(src, 1, 2, PixelFormat.Rgb24, stride: 4);
        Assert.Equal(new Rgba32(4, 5, 6), img[0, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.CopyPixelDataFrom(src, PixelFormat.Rgb24, stride: 2));
        Assert.Throws<ArgumentException>(() => img.CopyPixelDataFrom(src.AsSpan(0, 6), PixelFormat.Rgb24, stride: 4));
    }

    [Fact]
    public void LoadPixelData_Rgba32Span_And_CopyPixelDataTo()
    {
        Rgba32[] src = [new(1, 2, 3, 4), new(5, 6, 7, 8), new(9, 10, 11, 12), new(13, 14, 15, 16)];
        using Image img = Image.LoadPixelData(src, 2, 2);
        Assert.Equal(src[3], img[1, 1]);
        var copy = new Rgba32[4];
        img.CopyPixelDataTo(copy);
        Assert.Equal(src, copy);
        Assert.Throws<ArgumentException>(() => Image.LoadPixelData(src.AsSpan(0, 3), 2, 2));
    }

    [Fact]
    public void GetPixelMemory_SharesThePixels()
    {
        using Image img = Pattern(3, 2);
        Memory<Rgba32> memory = img.GetPixelMemory();
        Assert.Equal(6, memory.Length);
        memory.Span[4] = Rgba32.White;
        Assert.Equal(Rgba32.White, img[1, 1]);
    }

    [Fact]
    public void CopyPixelDataTo_WithStride_LeavesPadding()
    {
        using Image img = Pattern(2, 2);
        byte[] dst = new byte[8 + 6];
        dst.AsSpan().Fill(0xEE);
        img.CopyPixelDataTo(dst, PixelFormat.Bgr24, stride: 8);
        Assert.Equal(img[0, 1].B, dst[8]);
        Assert.Equal(0xEE, dst[7]);
        Assert.Throws<ArgumentException>(() => img.CopyPixelDataTo(dst.AsSpan(0, 13), PixelFormat.Bgr24, stride: 8));
    }

    [Fact]
    public void Clone_IsDeep()
    {
        using Image img = Pattern(4, 3);
        img.Metadata.Xmp = [1, 2, 3];
        img.Metadata.HorizontalResolution = 300;
        img.Metadata.VerticalResolution = 200;
        using Image copy = img.Clone();
        copy[0, 0] = Rgba32.White;
        copy.Metadata.Xmp![0] = 9;
        Assert.NotEqual(img[0, 0], copy[0, 0]);
        Assert.Equal(1, img.Metadata.Xmp[0]);
        Assert.Equal(300f, copy.Metadata.HorizontalResolution);
        Assert.Equal(200f, copy.Metadata.VerticalResolution);
    }

    [Fact]
    public void CloneCrop_And_MutateCrop()
    {
        using Image img = Pattern(10, 8);
        using Image part = img.Clone(x => x.Crop(new Rectangle(2, 3, 4, 2)));
        Assert.Equal(new Size(4, 2), part.Size);
        Assert.Equal(img[2, 3], part[0, 0]);
        Assert.Equal(img[5, 4], part[3, 1]);

        Rgba32 expected = img[7, 7];
        img.Mutate(x => x.Crop(5, 5, 5, 3));
        Assert.Equal(new Size(5, 3), img.Size);
        Assert.Equal(expected, img[2, 2]);
    }

    [Fact]
    public void Dispose_PreventsFurtherUse()
    {
        var img = new Image(2, 2);
        img.Dispose();
        img.Dispose();
        Assert.Throws<ObjectDisposedException>(() => img.Width);
        Assert.Throws<ObjectDisposedException>(() => img[0, 0]);
        Assert.Throws<ObjectDisposedException>(() => img.Mutate(x => x.Invert()));
    }

    [Fact]
    public void Save_UnknownExtension_Throws()
    {
        using var img = new Image(1, 1);
        Assert.Throws<NotSupportedException>(() => img.Save("x.tiff"));
    }
}
