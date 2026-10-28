using System.Drawing;
using PictureSharp.Processing;

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

    [Test]
    public async Task NewImage_IsTransparent()
    {
        using var img = new Image(3, 2);
        await Assert.That(img.Size).IsEqualTo(new Size(3, 2));
        await Assert.That(img.HasAlpha).IsTrue();
        await Assert.That(img.FrameCount).IsEqualTo(1);
        await Assert.That(img.IsAnimated).IsFalse();
        Assert.Null(img.SourceFormat);
        await Assert.That(img.Bounds).IsEqualTo(new Rectangle(0, 0, 3, 2));
        foreach (var p in img.GetPixelSpan<Rgba32>().ToArray())
        {
            await Assert.That(p).IsEqualTo(Rgba32.Transparent);
        }

        await Assert.That(img.IsOpaque()).IsFalse();
    }

    [Test]
    public async Task BackgroundConstructor_FillsAndIsOpaque()
    {
        using var img = new Image(4, 4, Color.Red);
        await Assert.That(img[3, 3]).IsEqualTo(Rgba32.FromColor(Color.Red));
        await Assert.That(img.IsOpaque()).IsTrue();
        await Assert.That(img.HasAlpha).IsFalse();
    }

    [Test]
    public async Task Indexer_AcceptsColor_AndChecksBounds()
    {
        using var img = new Image(2, 2);
        img[1, 1] = Color.FromArgb(1, 2, 3, 4);
        await Assert.That(img[1, 1]).IsEqualTo(new Rgba32(2, 3, 4, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = img[2, 0]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img[0, -1] = Color.Red);
    }

    [Test]
    public async Task Gray8_UsesBt601()
    {
        using var img = new Image(3, 1);
        img[0, 0] = new Rgba32(255, 0, 0);
        img[1, 0] = new Rgba32(0, 255, 0);
        img[2, 0] = new Rgba32(255, 255, 255);
        await Assert.That(img.ToPixelData(PixelFormat.Gray8)).IsEquivalentTo(new byte[] { 76, 150, 255 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(PixelFormat.Rgba32Premultiplied)]
    [Arguments(PixelFormat.Bgra32Premultiplied)]
    public async Task Premultiplied_RoundTrip_OpaqueExact_TransparentZero(PixelFormat format)
    {
        using var img = new Image(3, 1);
        img[0, 0] = new Rgba32(200, 100, 50, 255);
        img[1, 0] = new Rgba32(200, 100, 50, 0);
        img[2, 0] = new Rgba32(200, 100, 50, 128);
        byte[] bytes = img.ToPixelData(format);
        using Image back = Image.LoadPixelData(bytes, 3, 1, format);
        await Assert.That(back[0, 0]).IsEqualTo(img[0, 0]);
        await Assert.That(back[1, 0]).IsEqualTo(Rgba32.Transparent);
        Rgba32 half = back[2, 0];
        await Assert.That((int)half.R).IsBetween(199, 201);
        await Assert.That((int)half.G).IsBetween(99, 101);
        await Assert.That((int)half.A).IsEqualTo(128);
    }

    [Test]
    public async Task LoadPixelData_WithStride()
    {
        byte[] src = [1, 2, 3, 9, 4, 5, 6, 9];  // 1 px per row RGB24, stride 4
        using Image img = Image.LoadPixelData(src, 1, 2, PixelFormat.Rgb24, stride: 4);
        await Assert.That(img[0, 1]).IsEqualTo(new Rgba32(4, 5, 6));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.CopyPixelDataFrom(src, PixelFormat.Rgb24, stride: 2));
        Assert.ThrowsExactly<ArgumentException>(() => img.CopyPixelDataFrom(src.AsSpan(0, 6), PixelFormat.Rgb24, stride: 4));
    }

    [Test]
    public async Task LoadPixelData_Rgba32Span_And_CopyPixelDataTo()
    {
        Rgba32[] src = [new(1, 2, 3, 4), new(5, 6, 7, 8), new(9, 10, 11, 12), new(13, 14, 15, 16)];
        using Image img = Image.LoadPixelData(src, 2, 2);
        await Assert.That(img[1, 1]).IsEqualTo(src[3]);
        var copy = new Rgba32[4];
        img.CopyPixelDataTo(copy);
        await Assert.That(copy).IsEquivalentTo(src, CollectionOrdering.Matching);
        Assert.ThrowsExactly<ArgumentException>(() => Image.LoadPixelData(src.AsSpan(0, 3), 2, 2));
    }

    [Test]
    public async Task GetPixelMemory_SharesThePixels()
    {
        using Image img = Pattern(3, 2);
        Memory<Rgba32> memory = img.GetPixelMemory<Rgba32>();
        await Assert.That(memory.Length).IsEqualTo(6);
        memory.Span[4] = Rgba32.White;
        await Assert.That(img[1, 1]).IsEqualTo(Rgba32.White);
    }

    [Test]
    public async Task Clone_IsDeep()
    {
        using Image img = Pattern(4, 3);
        img.Metadata.Xmp = [1, 2, 3];
        img.Metadata.HorizontalResolution = 300;
        img.Metadata.VerticalResolution = 200;
        using Image copy = img.Clone();
        copy[0, 0] = Rgba32.White;
        copy.Metadata.Xmp![0] = 9;
        await Assert.That(copy[0, 0]).IsNotEqualTo(img[0, 0]);
        await Assert.That((int)(img.Metadata.Xmp[0])).IsEqualTo(1);
        await Assert.That(copy.Metadata.HorizontalResolution).IsEqualTo(300f);
        await Assert.That(copy.Metadata.VerticalResolution).IsEqualTo(200f);
    }

    [Test]
    public void Save_UnknownExtension_Throws()
    {
        using var img = new Image(1, 1);
        Assert.ThrowsExactly<NotSupportedException>(() => img.Save("x.tiff"));
    }
}
