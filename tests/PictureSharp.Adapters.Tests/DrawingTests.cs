using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PictureSharp.Drawing;
using Bitmap = System.Drawing.Bitmap;
using GdiColor = System.Drawing.Color;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PictureSharp.Adapters.Tests;

/// <summary>
/// Row copies are tested on every platform against native memory; the GDI+ round trips need Windows and report
/// as skipped elsewhere.
/// </summary>
public sealed class DrawingTests
{
    public static IEnumerable<(int Width, int Height)> Sizes() => TestImages.Sizes();

    [Test]
    [MethodDataSource(nameof(Sizes))]
    public async Task RowCopies_HonorPaddedAndNegativeStrides(int width, int height)
    {
        using Image image = TestImages.Create(width, height);
        foreach (PixelFormat format in new[] { PixelFormat.Bgra32, PixelFormat.Bgra32Premultiplied, PixelFormat.Bgr24 })
        {
            int rowBytes = width * format.BytesPerPixel();
            int padded = (rowBytes + 3 + 8) & ~3;
            foreach (bool bottomUp in new[] { false, true })
            {
                IntPtr memory = Marshal.AllocHGlobal(padded * height);
                try
                {
                    // A bottom-up bitmap starts at its last row and has a negative stride, as GDI+ reports it.
                    IntPtr scan0 = bottomUp ? memory + (padded * (height - 1)) : memory;
                    int stride = bottomUp ? -padded : padded;
                    BitmapPixels.Write(image.Frames.RootFrame, scan0, stride, format);
                    var row = new byte[rowBytes];
                    Marshal.Copy(scan0 + ((height - 1) * stride), row, 0, rowBytes);
                    byte[] expected = image.ToPixelData(format).AsSpan((height - 1) * rowBytes, rowBytes).ToArray();
                    await Assert.That(row).IsEquivalentTo(expected, CollectionOrdering.Matching);

                    using Image back = BitmapPixels.Read(scan0, stride, width, height, format);
                    Rgba32[] want = format switch
                    {
                        PixelFormat.Bgra32 => TestImages.Pixels(image),
                        PixelFormat.Bgra32Premultiplied => TestImages.ThroughPremultiplied(image),
                        _ => [.. TestImages.Pixels(image).Select(p => p with { A = 255 })],
                    };
                    await Assert.That(TestImages.Pixels(back)).IsEquivalentTo(want, CollectionOrdering.Matching);
                    await Assert.That(back.HasAlpha).IsEqualTo(format.HasAlpha());
                }
                finally
                {
                    Marshal.FreeHGlobal(memory);
                }
            }
        }
    }

    [Test]
    public async Task RowCopies_RejectAStrideShorterThanARow()
    {
        using Image image = TestImages.Create(3, 2);
        await Assert.That(() => BitmapPixels.Write(image.Frames.RootFrame, IntPtr.Zero, 11, PixelFormat.Bgra32)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BitmapPixels.Read(IntPtr.Zero, -8, 3, 2, PixelFormat.Bgr24)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BitmapPixels.Read(IntPtr.Zero, int.MinValue, 3, 2, PixelFormat.Bgr24)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [MethodDataSource(nameof(Sizes))]
    public async Task Bitmap_RoundTrip_KeepsStraightAlphaExactly(int width, int height)
    {
        Skip.Unless(OperatingSystem.IsWindows(), "GDI+ (System.Drawing.Common) is supported on Windows only.");
        if (OperatingSystem.IsWindows()) await RoundTrip(width, height);
    }

    [Test]
    public async Task Bitmap_FormatsAndResolution()
    {
        Skip.Unless(OperatingSystem.IsWindows(), "GDI+ (System.Drawing.Common) is supported on Windows only.");
        if (OperatingSystem.IsWindows()) await FormatsAndResolution();
    }

    [Test]
    public async Task Bitmap_CopiesAreIndependent_AndInvalidFormatsThrow()
    {
        Skip.Unless(OperatingSystem.IsWindows(), "GDI+ (System.Drawing.Common) is supported on Windows only.");
        if (OperatingSystem.IsWindows()) await Independence();
    }

    [SupportedOSPlatform("windows")]
    private static async Task RoundTrip(int width, int height)
    {
        using Image image = TestImages.Create(width, height);
        using Bitmap bitmap = image.ToBitmap();
        await Assert.That(bitmap.PixelFormat).IsEqualTo(GdiPixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            Rgba32 p = image[x, y];
            await Assert.That(bitmap.GetPixel(x, y).ToArgb()).IsEqualTo(GdiColor.FromArgb(p.A, p.R, p.G, p.B).ToArgb());
        }

        using Image back = Image.FromBitmap(bitmap);
        await Assert.That(TestImages.Pixels(back)).IsEquivalentTo(TestImages.Pixels(image), CollectionOrdering.Matching);
        using Image second = TestImages.Create(width, height, seed: 2);
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), image, second);
        using Bitmap frame = animation.Frames[1].ToBitmap();
        using Image fromFrame = Image.FromBitmap(frame);
        await Assert.That(TestImages.Pixels(fromFrame)).IsEquivalentTo(TestImages.Pixels(second), CollectionOrdering.Matching);
    }

    [SupportedOSPlatform("windows")]
    private static async Task FormatsAndResolution()
    {
        using Image image = TestImages.Create(7, 5);
        image.Metadata.HorizontalResolution = 300;
        image.Metadata.VerticalResolution = 150;
        using (Bitmap premultiplied = image.ToBitmap(GdiPixelFormat.Format32bppPArgb))
        using (Image back = Image.FromBitmap(premultiplied))
        {
            await Assert.That(premultiplied.PixelFormat).IsEqualTo(GdiPixelFormat.Format32bppPArgb);
            await Assert.That(premultiplied.HorizontalResolution).IsEqualTo(300f);
            await Assert.That(TestImages.Pixels(back)).IsEquivalentTo(TestImages.ThroughPremultiplied(image), CollectionOrdering.Matching);
            await Assert.That(back.Metadata.HorizontalResolution).IsEqualTo(300f);
            await Assert.That(back.Metadata.VerticalResolution).IsEqualTo(150f);
        }

        Rgba32[] opaque = [.. TestImages.Pixels(image).Select(p => p with { A = 255 })];
        foreach (GdiPixelFormat format in new[] { GdiPixelFormat.Format24bppRgb, GdiPixelFormat.Format32bppRgb })
        {
            using Bitmap rgb = image.ToBitmap(format);
            using Image back = Image.FromBitmap(rgb);
            await Assert.That(TestImages.Pixels(back)).IsEquivalentTo(opaque, CollectionOrdering.Matching);
            await Assert.That(back.HasAlpha).IsFalse();
        }

        // Formats PictureSharp does not convert itself go through GDI+ as 32-bit ARGB.
        using var indexed = new Bitmap(2, 1, GdiPixelFormat.Format1bppIndexed);
        using Image fromIndexed = Image.FromBitmap(indexed);
        await Assert.That(fromIndexed[0, 0]).IsEqualTo(new Rgba32(0, 0, 0));
    }

    [SupportedOSPlatform("windows")]
    private static async Task Independence()
    {
        Image image = TestImages.Create(4, 3);
        Rgba32[] original = TestImages.Pixels(image);
        Bitmap bitmap = image.ToBitmap();
        image.Dispose();
        using (Image back = Image.FromBitmap(bitmap))
        {
            bitmap.SetPixel(0, 0, GdiColor.Red);
            await Assert.That(TestImages.Pixels(back)).IsEquivalentTo(original, CollectionOrdering.Matching);
        }

        bitmap.Dispose();
        await Assert.That(() => bitmap.Width).Throws<ArgumentException>();
        using Image other = TestImages.Create(2, 2);
        await Assert.That(() => other.ToBitmap(GdiPixelFormat.Format8bppIndexed)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Image.FromBitmap(null!)).Throws<ArgumentNullException>();
    }
}
