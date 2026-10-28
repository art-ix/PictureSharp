using System.Runtime.InteropServices;
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
        image.GetPixelSpan<Rgba32>().Fill(Rgba32.White);
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

    [Test]
    [MethodDataSource(nameof(Sizes))]
    public async Task Rgba16161616_Unpremultiplied_CopiesSixteenBitSamplesExactly(int width, int height)
    {
        using Image image = TestImages.CreateWide(width, height);
        byte[] expected = image.ToPixelData(PixelFormat.Rgba64);
        await Assert.That(expected).IsEquivalentTo(image.GetPixelByteSpan().ToArray(), CollectionOrdering.Matching);

        using SKBitmap bitmap = image.ToSKBitmap(SKColorType.Rgba16161616);
        await Assert.That(bitmap.ColorType).IsEqualTo(SKColorType.Rgba16161616);
        await Assert.That(bitmap.AlphaType).IsEqualTo(SKAlphaType.Unpremul);
        await Assert.That(Rows(bitmap.GetPixelSpan(), bitmap.RowBytes, width * 8, height)).IsEquivalentTo(expected, CollectionOrdering.Matching);

        using SKImage skImage = image.ToSKImage(SKColorType.Rgba16161616);
        using SKPixmap pixmap = skImage.PeekPixels();
        await Assert.That(Rows(pixmap.GetPixelSpan(), pixmap.RowBytes, width * 8, height)).IsEquivalentTo(expected, CollectionOrdering.Matching);

        // RGB under zero alpha survives in both directions, as with 8-bit Unpremul.
        foreach (Image back in new[] { Image.FromSKBitmap(bitmap, PixelType.Rgba64), Image.FromSKImage(skImage, PixelType.Rgba64), Image.FromSKPixmap(pixmap, PixelType.Rgba64) })
        {
            using (back)
            {
                await Assert.That(back.PixelType).IsEqualTo(PixelType.Rgba64);
                await Assert.That(TestImages.WidePixels(back)).IsEquivalentTo(TestImages.WidePixels(image), CollectionOrdering.Matching);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Sizes))]
    public async Task Rgba16161616_Premultiplied_WritesPictureSharpBytes_AndReadsBackWithinOneCode(int width, int height)
    {
        using Image image = TestImages.CreateWide(width, height);
        using SKBitmap bitmap = image.ToSKBitmap(SKColorType.Rgba16161616, SKAlphaType.Premul);
        byte[] actual = Rows(bitmap.GetPixelSpan(), bitmap.RowBytes, width * 8, height);
        await Assert.That(actual).IsEquivalentTo(image.ToPixelData(PixelFormat.Rgba64Premultiplied), CollectionOrdering.Matching);
        using SKImage skImage = image.ToSKImage(SKColorType.Rgba16161616, SKAlphaType.Premul);
        using (SKPixmap pixmap = skImage.PeekPixels())
        {
            await Assert.That(Rows(pixmap.GetPixelSpan(), pixmap.RowBytes, width * 8, height)).IsEquivalentTo(actual, CollectionOrdering.Matching);
        }

        // Skia un-premultiplies at 16 bits: alpha is exact, alpha 0 gives transparent black and color stays within
        // one 16-bit code of PictureSharp's own premultiplied round trip.
        using Image back = Image.FromSKBitmap(bitmap, PixelType.Rgba64);
        using Image reference = Image.LoadPixelData(actual, width, height, PixelFormat.Rgba64Premultiplied, pixelType: PixelType.Rgba64);
        Rgba64[] pixels = TestImages.WidePixels(back), expected = TestImages.WidePixels(reference), original = TestImages.WidePixels(image);
        for (int i = 0; i < pixels.Length; i++)
        {
            await Assert.That(pixels[i].A).IsEqualTo(original[i].A);
            if (original[i].A == 0) await Assert.That(pixels[i]).IsEqualTo(Rgba64.Transparent);
            else if (original[i].A == ushort.MaxValue) await Assert.That(pixels[i]).IsEqualTo(original[i]);
            else await Assert.That(MaxDifference(pixels[i], expected[i])).IsLessThanOrEqualTo(1);
        }
    }

    [Test]
    [MethodDataSource(nameof(Sizes))]
    public async Task EightBitImage_WidensExactly_AndDefaultImportRoundsOnce(int width, int height)
    {
        using Image image = TestImages.Create(width, height);
        Rgba64[] widened = [.. TestImages.Pixels(image).Select(Rgba64.FromRgba32)];
        using SKBitmap bitmap = image.ToSKBitmap(SKColorType.Rgba16161616);
        using (Image wide = Image.FromSKBitmap(bitmap, PixelType.Rgba64))
        {
            await Assert.That(TestImages.WidePixels(wide)).IsEquivalentTo(widened, CollectionOrdering.Matching);
        }

        // The default keeps 8-bit storage; an exactly widened 16-bit source comes back unchanged.
        using (Image narrow = Image.FromSKBitmap(bitmap))
        {
            await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);
            await Assert.That(TestImages.Pixels(narrow)).IsEquivalentTo(TestImages.Pixels(image), CollectionOrdering.Matching);
        }

        // An 8-bit source read as Rgba64 widens exactly through Skia as well.
        using SKBitmap eight = image.ToSKBitmap();
        using Image fromEight = Image.FromSKBitmap(eight, PixelType.Rgba64);
        await Assert.That(TestImages.WidePixels(fromEight)).IsEquivalentTo(widened, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Sizes))]
    public async Task SixteenBitImage_ToEightBitColorTypes_RoundsLikePictureSharp(int width, int height)
    {
        using Image image = TestImages.CreateWide(width, height);
        using Image rounded = image.Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        foreach (SKColorType colorType in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        {
            PixelFormat format = colorType == SKColorType.Rgba8888 ? PixelFormat.Rgba32 : PixelFormat.Bgra32;
            using SKBitmap bitmap = image.ToSKBitmap(colorType);
            await Assert.That(Rows(bitmap.GetPixelSpan(), bitmap.RowBytes, width * 4, height)).IsEquivalentTo(rounded.ToPixelData(format), CollectionOrdering.Matching);
            using SKImage skImage = image.ToSKImage(colorType);
            using SKPixmap pixmap = skImage.PeekPixels();
            await Assert.That(Rows(pixmap.GetPixelSpan(), pixmap.RowBytes, width * 4, height)).IsEquivalentTo(rounded.ToPixelData(format), CollectionOrdering.Matching);
        }

        await Assert.That(TestImages.Pixels(rounded)).IsEquivalentTo(TestImages.Rounded(image), CollectionOrdering.Matching);
    }

    [Test]
    public async Task SixteenBitSources_ReadWithinOneCode_AndOpaqueGivesFullAlpha()
    {
        // A 16-bit Skia source read with the default rounds to 8 bits within one level of PictureSharp's rounding.
        using Image image = TestImages.CreateWide(13, 4);
        using SKBitmap bitmap = image.ToSKBitmap(SKColorType.Rgba16161616);
        using (Image narrow = Image.FromSKBitmap(bitmap))
        {
            Rgba32[] pixels = TestImages.Pixels(narrow), expected = TestImages.Rounded(image);
            for (int i = 0; i < pixels.Length; i++) await Assert.That(MaxDifference(pixels[i], expected[i])).IsLessThanOrEqualTo(1);
        }

        var info = new SKImageInfo(2, 1, SKColorType.Rgba16161616, SKAlphaType.Opaque);
        using var opaque = new SKBitmap(info);
        ((ReadOnlySpan<ushort>)[1000, 2000, 3000, 7, 40000, 50000, 60000, 0]).CopyTo(MemoryMarshal.Cast<byte, ushort>(opaque.GetPixelSpan()));
        using Image fromOpaque = Image.FromSKBitmap(opaque, PixelType.Rgba64);
        await Assert.That(TestImages.WidePixels(fromOpaque)).IsEquivalentTo([new Rgba64(1000, 2000, 3000), new Rgba64(40000, 50000, 60000)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SixteenBitConversions_Copy_SoEitherSideCanBeChangedIndependently()
    {
        Image image = TestImages.CreateWide(4, 4);
        Rgba64[] original = TestImages.WidePixels(image);
        SKBitmap bitmap = image.ToSKBitmap(SKColorType.Rgba16161616);
        SKImage skImage = image.ToSKImage(SKColorType.Rgba16161616);
        image.GetPixelSpan<Rgba64>().Fill(Rgba64.White);
        image.Dispose();
        using (Image back = Image.FromSKBitmap(bitmap, PixelType.Rgba64))
        using (Image backFromImage = Image.FromSKImage(skImage, PixelType.Rgba64))
        {
            await Assert.That(TestImages.WidePixels(back)).IsEquivalentTo(original, CollectionOrdering.Matching);
            await Assert.That(TestImages.WidePixels(backFromImage)).IsEquivalentTo(original, CollectionOrdering.Matching);
            bitmap.Erase(SKColors.Red);
            await Assert.That(TestImages.WidePixels(back)).IsEquivalentTo(original, CollectionOrdering.Matching);
        }

        bitmap.Dispose();
        skImage.Dispose();
        using var source = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        await Assert.That(() => Image.FromSKBitmap(source, (PixelType)7)).Throws<ArgumentOutOfRangeException>();
        using Image wide = TestImages.CreateWide(2, 2);
        await Assert.That(() => wide.ToSKBitmap(SKColorType.RgbaF16)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => wide.ToSKImage(SKColorType.Rgba16161616, SKAlphaType.Opaque)).Throws<ArgumentOutOfRangeException>();
    }

    private static int MaxDifference(Rgba64 a, Rgba64 b) =>
        Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));

    private static byte[] Rows(ReadOnlySpan<byte> pixels, int rowBytes, int packedRowBytes, int height)
    {
        var rows = new byte[packedRowBytes * height];
        for (int y = 0; y < height; y++) pixels.Slice(y * rowBytes, packedRowBytes).CopyTo(rows.AsSpan(y * packedRowBytes));
        return rows;
    }

    private static int MaxDifference(Rgba32 a, Rgba32 b) =>
        Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));
}
