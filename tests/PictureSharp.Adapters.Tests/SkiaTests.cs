using PictureSharp.Skia;
using SkiaSharp;

namespace PictureSharp.Adapters.Tests;

public sealed class SkiaTests
{
    public static IEnumerable<(int Width, int Height)> Sizes() => TestImages.Sizes();

    [Test]
    [MethodDataSource(nameof(Sizes))]
    public async Task ToSKBitmap_Default_CopiesStraightRgbaRowByRow(int width, int height)
    {
        using Image image = TestImages.Create(width, height);
        using SKBitmap bitmap = image.ToSKBitmap();
        await Assert.That(bitmap.ColorType).IsEqualTo(SKColorType.Rgba8888);
        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Unpremul);
        await Assert.That(bitmap.Width).IsEqualTo(width);
        await Assert.That(bitmap.Height).IsEqualTo(height);
        byte[] expected = image.ToPixelData();
        byte[] actual = Rows(bitmap.GetPixelSpan(), bitmap.RowBytes, width * 4, height);
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Sizes))]
    public async Task UnpremultipliedRoundTrip_IsExact_ForBothChannelOrders(int width, int height)
    {
        using Image image = TestImages.Create(width, height);
        foreach (SKColorType colorType in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        {
            using SKBitmap bitmap = image.ToSKBitmap(colorType);
            using Image fromBitmap = Image.FromSKBitmap(bitmap);
            await Assert.That(TestImages.Pixels(fromBitmap)).IsEquivalentTo(TestImages.Pixels(image), CollectionOrdering.Matching);

            using SKImage skImage = image.ToSKImage(colorType);
            using Image fromImage = Image.FromSKImage(skImage);
            await Assert.That(TestImages.Pixels(fromImage)).IsEquivalentTo(TestImages.Pixels(image), CollectionOrdering.Matching);
        }
    }

    [Test]
    [MethodDataSource(nameof(Sizes))]
    public async Task Premultiplied_WritesPremultipliedBytes_AndReadsBackWithinOneLevel(int width, int height)
    {
        using Image image = TestImages.Create(width, height);
        using SKBitmap bitmap = image.ToSKBitmap(SKColorType.Bgra8888, SKAlphaType.Premul);
        byte[] actual = Rows(bitmap.GetPixelSpan(), bitmap.RowBytes, width * 4, height);
        await Assert.That(actual).IsEquivalentTo(image.ToPixelData(PixelFormat.Bgra32Premultiplied), CollectionOrdering.Matching);

        // Skia un-premultiplies: alpha 0 gives transparent black, alpha 255 is exact, others match PictureSharp within 1.
        using Image back = Image.FromSKBitmap(bitmap);
        Rgba32[] reference = TestImages.ThroughPremultiplied(image);
        Rgba32[] pixels = TestImages.Pixels(back);
        Rgba32[] original = TestImages.Pixels(image);
        for (int i = 0; i < pixels.Length; i++)
        {
            await Assert.That(pixels[i].A).IsEqualTo(original[i].A);
            if (original[i].A == 0) await Assert.That(pixels[i]).IsEqualTo(Rgba32.Transparent);
            else if (original[i].A == 255) await Assert.That(pixels[i]).IsEqualTo(original[i]);
            else await Assert.That(MaxDifference(pixels[i], reference[i])).IsLessThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task FromSKBitmap_HonorsPaddedRowBytes()
    {
        using Image image = TestImages.Create(7, 3);
        var info = new SKImageInfo(7, 3, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info, info.RowBytes + 12);
        await Assert.That(bitmap.RowBytes).IsEqualTo(info.RowBytes + 12);
        image.CopyPixelDataTo(bitmap.GetPixelSpan(), PixelFormat.Rgba32, bitmap.RowBytes);
        using Image back = Image.FromSKBitmap(bitmap);
        await Assert.That(TestImages.Pixels(back)).IsEquivalentTo(TestImages.Pixels(image), CollectionOrdering.Matching);
        using SKPixmap pixmap = bitmap.PeekPixels();
        using Image fromPixmap = Image.FromSKPixmap(pixmap);
        await Assert.That(TestImages.Pixels(fromPixmap)).IsEquivalentTo(TestImages.Pixels(image), CollectionOrdering.Matching);
    }

    [Test]
    public async Task FromSKBitmap_ConvertsOtherColorTypes()
    {
        using var gray = new SKBitmap(new SKImageInfo(3, 1, SKColorType.Gray8, SKAlphaType.Opaque));
        gray.GetPixelSpan()[0] = 0;
        gray.GetPixelSpan()[1] = 100;
        gray.GetPixelSpan()[2] = 255;
        using Image fromGray = Image.FromSKBitmap(gray);
        await Assert.That(TestImages.Pixels(fromGray)).IsEquivalentTo([new Rgba32(0, 0, 0), new Rgba32(100, 100, 100), new Rgba32(255, 255, 255)], CollectionOrdering.Matching);

        using var wide = new SKBitmap(new SKImageInfo(2, 1, SKColorType.RgbaF16, SKAlphaType.Unpremul));
        wide.SetPixel(0, 0, new SKColor(10, 20, 30, 255));
        wide.SetPixel(1, 0, new SKColor(200, 100, 50, 255));
        using Image fromWide = Image.FromSKBitmap(wide);
        await Assert.That(TestImages.Pixels(fromWide)).IsEquivalentTo([new Rgba32(10, 20, 30), new Rgba32(200, 100, 50)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FromSKBitmap_OpaqueAlphaType_GivesOpaquePixels()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Opaque));
        byte[] raw = [10, 20, 30, 7, 40, 50, 60, 0];
        raw.CopyTo(bitmap.GetPixelSpan());
        using Image image = Image.FromSKBitmap(bitmap);
        await Assert.That(TestImages.Pixels(image)).IsEquivalentTo([new Rgba32(10, 20, 30), new Rgba32(40, 50, 60)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FrameOverloads_CopyTheRequestedFrame()
    {
        using Image first = TestImages.Create(5, 3, seed: 1);
        using Image second = TestImages.Create(5, 3, seed: 2);
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), first, second);
        using SKBitmap bitmap = animation.Frames[1].ToSKBitmap();
        using SKImage skImage = animation.Frames[1].ToSKImage(SKColorType.Bgra8888, SKAlphaType.Premul);
        using Image fromBitmap = Image.FromSKBitmap(bitmap);
        await Assert.That(TestImages.Pixels(fromBitmap)).IsEquivalentTo(TestImages.Pixels(second), CollectionOrdering.Matching);
        using SKBitmap root = animation.ToSKBitmap();
        using Image fromRoot = Image.FromSKBitmap(root);
        await Assert.That(TestImages.Pixels(fromRoot)).IsEquivalentTo(TestImages.Pixels(first), CollectionOrdering.Matching);
        await Assert.That(skImage.AlphaType).IsEqualTo(SKAlphaType.Premul);
    }

    [Test]
    public async Task Conversions_Copy_SoEitherSideCanBeChangedOrDisposedIndependently()
    {
        Image image = TestImages.Create(4, 4);
        Rgba32[] original = TestImages.Pixels(image);
        SKBitmap bitmap = image.ToSKBitmap();
        SKImage skImage = image.ToSKImage();
        image.GetPixelSpan().Fill(Rgba32.White);
        image.Dispose();
        using (Image back = Image.FromSKBitmap(bitmap))
        using (Image backFromImage = Image.FromSKImage(skImage))
        {
            await Assert.That(TestImages.Pixels(back)).IsEquivalentTo(original, CollectionOrdering.Matching);
            await Assert.That(TestImages.Pixels(backFromImage)).IsEquivalentTo(original, CollectionOrdering.Matching);
            bitmap.Erase(SKColors.Red);
            await Assert.That(TestImages.Pixels(back)).IsEquivalentTo(original, CollectionOrdering.Matching);
        }

        bitmap.Dispose();
        skImage.Dispose();
        await Assert.That(bitmap.Handle).IsEqualTo(IntPtr.Zero);
        await Assert.That(skImage.Handle).IsEqualTo(IntPtr.Zero);
    }

    [Test]
    public async Task InvalidArguments_Throw()
    {
        using Image image = TestImages.Create(2, 2);
        await Assert.That(() => image.ToSKBitmap(SKColorType.Rgb565)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => image.ToSKImage(SKColorType.Rgba8888, SKAlphaType.Opaque)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => image.ToSKBitmap(SKColorType.Bgra8888, SKAlphaType.Unknown)).Throws<ArgumentOutOfRangeException>();
        using var empty = new SKBitmap();
        await Assert.That(() => Image.FromSKBitmap(empty)).Throws<ArgumentException>();
        await Assert.That(() => Image.FromSKBitmap(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => Image.FromSKImage(null!)).Throws<ArgumentNullException>();
    }

    private static byte[] Rows(ReadOnlySpan<byte> pixels, int rowBytes, int packedRowBytes, int height)
    {
        var rows = new byte[packedRowBytes * height];
        for (int y = 0; y < height; y++) pixels.Slice(y * rowBytes, packedRowBytes).CopyTo(rows.AsSpan(y * packedRowBytes));
        return rows;
    }

    private static int MaxDifference(Rgba32 a, Rgba32 b) =>
        Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));
}
