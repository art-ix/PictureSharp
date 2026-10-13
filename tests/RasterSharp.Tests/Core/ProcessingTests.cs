using System.Drawing;
using RasterSharp.Tests.Infrastructure;

namespace RasterSharp.Tests.Core;

public class ProcessingTests
{
    public static TheoryData<ResamplingFilter> AllFilters => [ResamplingFilter.Nearest, ResamplingFilter.Bilinear, ResamplingFilter.Bicubic, ResamplingFilter.Lanczos3];

    public static TheoryData<RotateFlipType> AllRotateFlips =>
    [
        RotateFlipType.RotateNoneFlipNone, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate270FlipNone,
        RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate180FlipX, RotateFlipType.Rotate270FlipX,
    ];

    public static TheoryData<PixelFormat> AllFormats => new(Enum.GetValues<PixelFormat>());

    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan().ToArray();

    // ------------------------------------------------------------ geometry

    [Theory]
    [MemberData(nameof(AllRotateFlips))]
    public void RotateFlip_MatchesNaiveRotationThenFlip(RotateFlipType type)
    {
        using Image img = ImageTests.Pattern(7, 4);
        Rgba32[,] expected = ToGrid(img);
        for (int i = 0; i < ((int)type & 3); i++)
        {
            expected = RotateClockwise(expected);
        }

        if ((int)type >= 4)
        {
            expected = FlipX(expected);
        }

        img.RotateFlip(type);
        Assert.Equal(expected, ToGrid(img));
    }

    [Theory]
    [MemberData(nameof(AllRotateFlips))]
    public void RotateFlip_FourTimesQuarterTurnOrTwiceFlip_IsIdentity(RotateFlipType type)
    {
        using Image img = ImageTests.Pattern(6, 5);
        Rgba32[] before = Pixels(img);
        int repeat = (int)type >= 4 ? 2 : 4;   // every flip variant is an involution; rotations have order ≤ 4
        for (int i = 0; i < repeat; i++)
        {
            img.RotateFlip(type);
        }

        Assert.Equal(before, Pixels(img));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void AutoOrient_UndoesEachExifOrientation(int orientation)
    {
        // Store an upright pattern the way a camera with this orientation would, then check AutoOrient restores it.
        using Image upright = ImageTests.Pattern(5, 3);
        using Image stored = upright.Clone();
        stored.RotateFlip(Inverse((ExifOrientation)orientation));
        stored.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal((ExifOrientation)orientation, stored.Metadata.Orientation);
        stored.AutoOrient();
        Assert.Equal(Pixels(upright), Pixels(stored));
        Assert.Equal(upright.Size, stored.Size);
        Assert.Equal(ExifOrientation.TopLeft, stored.Metadata.Orientation);
    }

    private static RotateFlipType Inverse(ExifOrientation orientation) => orientation switch
    {
        ExifOrientation.RightTop => RotateFlipType.Rotate270FlipNone,
        ExifOrientation.LeftBottom => RotateFlipType.Rotate90FlipNone,
        ExifOrientation.TopRight => RotateFlipType.RotateNoneFlipX,
        ExifOrientation.BottomRight => RotateFlipType.Rotate180FlipNone,
        ExifOrientation.BottomLeft => RotateFlipType.RotateNoneFlipY,
        ExifOrientation.LeftTop => RotateFlipType.Rotate90FlipX,
        ExifOrientation.RightBottom => RotateFlipType.Rotate270FlipX,
        _ => RotateFlipType.RotateNoneFlipNone,
    };

    [Theory]
    [InlineData(0, 0, 7, 4)]
    [InlineData(6, 3, 1, 1)]
    [InlineData(2, 1, 3, 2)]
    public void Crop_TakesExactRegion_OnAllFrames(int x, int y, int w, int h)
    {
        using Image a = ImageTests.Pattern(7, 4);
        using Image b = a.Clone().Invert();
        using Image anim = Image.CreateAnimation([a, b], TimeSpan.FromMilliseconds(30));
        anim.Crop(new Rectangle(x, y, w, h));
        Assert.Equal(new Size(w, h), anim.Size);
        for (int f = 0; f < 2; f++)
        {
            Image source = f == 0 ? a : b;
            for (int yy = 0; yy < h; yy++)
            {
                for (int xx = 0; xx < w; xx++)
                {
                    Assert.Equal(source[x + xx, y + yy], anim.Frames[f][xx, yy]);
                }
            }
        }
    }

    [Theory]
    [InlineData(-1, 0, 2, 2)]
    [InlineData(0, -1, 2, 2)]
    [InlineData(6, 0, 2, 2)]
    [InlineData(0, 3, 2, 2)]
    [InlineData(0, 0, 0, 2)]
    [InlineData(0, 0, 2, 0)]
    [InlineData(0, 0, 8, 4)]
    [InlineData(int.MaxValue, 0, 2, 2)]
    public void Crop_OutsideBounds_Throws(int x, int y, int w, int h)
    {
        using Image img = ImageTests.Pattern(7, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Crop(new Rectangle(x, y, w, h)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Clone(new Rectangle(x, y, w, h)));
        Assert.Equal(new Size(7, 4), img.Size);
    }

    // ------------------------------------------------------------ resize

    [Theory]
    [InlineData(ResizeMode.Stretch, 40, 40, 40, 40)]
    [InlineData(ResizeMode.Fit, 40, 40, 40, 20)]
    [InlineData(ResizeMode.Fit, 300, 50, 100, 50)]
    [InlineData(ResizeMode.Fill, 40, 40, 40, 40)]
    [InlineData(ResizeMode.Fill, 10, 70, 10, 70)]
    [InlineData(ResizeMode.Pad, 40, 40, 40, 40)]
    public void Resize_ModesProduceExpectedSizes(ResizeMode mode, int w, int h, int ew, int eh)
    {
        using Image img = ProcessingOracleTests.Noise(100, 50, 3);
        img.Resize(w, h, mode);
        Assert.Equal(new Size(ew, eh), img.Size);
    }

    [Fact]
    public void Resize_Pad_CentersAndFillsBorders()
    {
        using var img = new Image(100, 50, Color.Red);
        img.Resize(40, 40, ResizeMode.Pad, ResamplingFilter.Bilinear);
        Assert.True(img.HasAlpha);
        Assert.Equal(Rgba32.Transparent, img[20, 0]);
        Assert.Equal(Rgba32.Transparent, img[20, 9]);
        Assert.Equal(new Rgba32(255, 0, 0), img[20, 10]);
        Assert.Equal(new Rgba32(255, 0, 0), img[20, 29]);
        Assert.Equal(Rgba32.Transparent, img[20, 30]);

        using var white = new Image(50, 100, Color.Red);
        white.Resize(41, 40, ResizeMode.Pad, ResamplingFilter.Bicubic, Color.White);
        Assert.False(white.HasAlpha);
        Assert.Equal((41, 40), (white.Width, white.Height));
        Assert.Equal(Rgba32.White, white[9, 20]);     // 20 px wide content; left border round(10.5) = 10 (half to even)
        Assert.Equal(new Rgba32(255, 0, 0), white[10, 20]);
        Assert.Equal(new Rgba32(255, 0, 0), white[29, 20]);
        Assert.Equal(Rgba32.White, white[30, 20]);
    }

    [Fact]
    public void Resize_FitThatRoundsToZero_KeepsOnePixel()
    {
        using Image img = ProcessingOracleTests.Noise(100, 3, 4);
        img.Resize(9, 40, ResizeMode.Fit);
        Assert.Equal(new Size(9, 1), img.Size);
    }

    [Theory]
    [MemberData(nameof(AllFilters))]
    public void Resize_UniformImage_StaysUniform(ResamplingFilter filter)
    {
        // Like Pillow, semi-transparent colors pass through 8-bit premultiplied alpha and a truncating
        // unpremultiply; opaque colors are kept exactly.
        foreach (Rgba32 color in new[] { new Rgba32(200, 100, 50), new Rgba32(200, 100, 50, 77) })
        {
            Rgba32 expected = filter == ResamplingFilter.Nearest ? color : PillowPremultiplyRoundTrip(color);
            using var img = new Image(37, 23, color.ToColor());
            foreach ((int w, int h) in new[] { (11, 5), (90, 61), (37, 9), (1, 1) })
            {
                using Image resized = img.Clone().Resize(w, h, ResizeMode.Stretch, filter);
                Assert.All(Pixels(resized), p => Assert.Equal(expected, p));
            }
        }
    }

    private static Rgba32 PillowPremultiplyRoundTrip(Rgba32 c)
    {
        static byte Channel(int v, int a)
        {
            int tmp = (v * a) + 128;
            int premultiplied = ((tmp >> 8) + tmp) >> 8;
            return a is 0 or 255 ? (byte)premultiplied : (byte)Math.Min(255, 255 * premultiplied / a);
        }

        return new Rgba32(Channel(c.R, c.A), Channel(c.G, c.A), Channel(c.B, c.A), c.A);
    }

    [Theory]
    [MemberData(nameof(AllFilters))]
    public void Resize_TransparentColor_DoesNotBleed(ResamplingFilter filter)
    {
        // Left half: invisible red; right half: opaque blue. Premultiplied resampling must not leak red.
        using var img = new Image(20, 10);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                img[x, y] = x < 10 ? new Rgba32(255, 0, 0, 0) : new Rgba32(0, 0, 255);
            }
        }

        img.Resize(7, 13, ResizeMode.Stretch, filter);
        Assert.All(Pixels(img), p => Assert.True(p.A == 0 || p.R == 0, p.ToString()));
        Assert.Contains(Pixels(img), p => p.A != 0);
    }

    [Fact]
    public void Resize_SameSize_IsIdentity_AndNearestDoublesPixels()
    {
        using Image img = ImageTests.Pattern(9, 6);
        Rgba32[] before = Pixels(img);
        img.Resize(9, 6);
        Assert.Equal(before, Pixels(img));

        using Image big = img.Clone().Resize(18, 12, ResizeMode.Stretch, ResamplingFilter.Nearest);
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 18; x++)
            {
                Assert.Equal(img[x / 2, y / 2], big[x, y]);
            }
        }
    }

    [Fact]
    public void Resize_LargeImage_ParallelIsDeterministic()
    {
        using Image img = ProcessingOracleTests.Noise(640, 480, 5);
        using Image a = img.Clone().Resize(1001, 333);
        using Image b = img.Clone().Resize(1001, 333);
        Assert.Equal(Pixels(a), Pixels(b));
        using Image down = img.Clone().Resize(7, 5);
        Assert.Equal(new Size(7, 5), down.Size);
    }

    [Fact]
    public void Resize_AppliesToAllFrames_AndKeepsDurations()
    {
        using Image a = ImageTests.Pattern(8, 6);
        using Image b = new(8, 6, Color.Green);
        using Image anim = Image.CreateAnimation([a, b], TimeSpan.FromMilliseconds(70), loopCount: 3);
        anim.Resize(4, 3, ResizeMode.Stretch, ResamplingFilter.Bilinear);
        Assert.All(anim.Frames, f => Assert.Equal((4, 3, TimeSpan.FromMilliseconds(70)), (f.Width, f.Height, f.Duration)));
        Assert.Equal(new Rgba32(0, 128, 0), anim.Frames[1][2, 1]);
        Assert.Equal(3, anim.LoopCount);
    }

    [Fact]
    public void Thumbnail_ShrinksKeepingAspect_AndNeverEnlarges()
    {
        using Image img = ProcessingOracleTests.Noise(120, 80, 6);
        img.Thumbnail(60, 60);
        Assert.Equal(new Size(60, 40), img.Size);
        img.Thumbnail(1000, 1000);
        Assert.Equal(new Size(60, 40), img.Size);
    }

    [Fact]
    public void GetThumbnailImage_ReturnsNewImage()
    {
        using Image img = ImageTests.Pattern(10, 10);
        img.SetResolution(300, 300);
        Rgba32[] before = Pixels(img);
        using Image thumb = img.GetThumbnailImage(3, 4);
        Assert.Equal(new Size(3, 4), thumb.Size);
        Assert.Equal(300f, thumb.HorizontalResolution);
        Assert.Equal(before, Pixels(img));
    }

    [Fact]
    public void Resize_InvalidArguments_Throw()
    {
        using Image img = ImageTests.Pattern(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Resize(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Resize(3, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Resize(3, 3, (ResizeMode)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Resize(3, 3, ResizeMode.Fit, (ResamplingFilter)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Thumbnail(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.GetThumbnailImage(3, 0));
        Assert.Equal(new Size(4, 4), img.Size);
    }

    // ------------------------------------------------------------ color operations

    [Fact]
    public void Grayscale_UsesBt601_AndKeepsAlpha()
    {
        using Image img = ImageTests.Pattern(6, 4);
        Rgba32[] before = Pixels(img);
        img.Grayscale();
        Rgba32[] after = Pixels(img);
        for (int i = 0; i < before.Length; i++)
        {
            byte l = (byte)(((19595 * before[i].R) + (38470 * before[i].G) + (7471 * before[i].B) + 32768) >> 16);
            Assert.Equal(new Rgba32(l, l, l, before[i].A), after[i]);
        }
    }

    [Fact]
    public void Invert_TwiceIsIdentity_AndKeepsAlpha()
    {
        using Image img = ImageTests.Pattern(6, 4);
        Rgba32[] before = Pixels(img);
        img.Invert();
        Assert.Equal(new Rgba32((byte)(255 - before[5].R), (byte)(255 - before[5].G), (byte)(255 - before[5].B), before[5].A), img.GetPixelSpan()[5]);
        img.Invert();
        Assert.Equal(before, Pixels(img));
    }

    [Fact]
    public void Fill_ClipsToBounds_AndOverwrites()
    {
        using var img = new Image(5, 4, Color.Black);
        img.Fill(new Rectangle(3, -2, 10, 4), Color.FromArgb(128, 255, 0, 0));
        Assert.True(img.HasAlpha);
        Assert.Equal(new Rgba32(255, 0, 0, 128), img[3, 0]);
        Assert.Equal(new Rgba32(255, 0, 0, 128), img[4, 1]);
        Assert.Equal(Rgba32.Black, img[2, 0]);
        Assert.Equal(Rgba32.Black, img[4, 2]);
        img.Fill(new Rectangle(10, 10, 3, 3), Color.White);   // fully outside: no-op
        img.Fill(new Rectangle(0, 0, 5, 4), Color.Blue);
        Assert.All(Pixels(img), p => Assert.Equal(new Rgba32(0, 0, 255), p));
    }

    [Fact]
    public void Clear_UpdatesHasAlpha()
    {
        using var img = new Image(2, 2);
        img.Clear(Color.White);
        Assert.False(img.HasAlpha);
        img.Clear(Color.Transparent);
        Assert.True(img.HasAlpha);
    }

    [Fact]
    public void MakeTransparent_ClearsMatchingPixelsOnAllFrames()
    {
        using Image a = new(3, 2, Color.White);
        a[1, 0] = Rgba32.Black;
        using Image b = a.Clone();
        using Image anim = Image.CreateAnimation([a, b], TimeSpan.FromMilliseconds(10));
        anim.HasAlpha = false;
        anim.MakeTransparent(Color.White);
        Assert.True(anim.HasAlpha);
        foreach (ImageFrame frame in anim.Frames)
        {
            Assert.Equal(new Rgba32(255, 255, 255, 0), frame[0, 0]);
            Assert.Equal(Rgba32.Black, frame[1, 0]);
        }

        using var c = new Image(3, 2, Color.Green);
        c[0, 1] = Rgba32.Black;   // bottom-left decides the key color
        c.MakeTransparent();
        Assert.Equal(0, c[0, 1].A);
        Assert.Equal(255, c[0, 0].A);
    }

    // ------------------------------------------------------------ drawing

    [Fact]
    public void DrawImage_OpaqueSourceReplaces_ClippedAtEdges()
    {
        using var dst = new Image(6, 5, Color.White);
        using var src = new Image(3, 3, Color.Red);
        dst.DrawImage(src, new Point(-1, 3));
        Assert.Equal(new Rgba32(255, 0, 0), dst[0, 3]);
        Assert.Equal(new Rgba32(255, 0, 0), dst[1, 4]);
        Assert.Equal(Rgba32.White, dst[2, 3]);
        Assert.Equal(Rgba32.White, dst[0, 2]);
        dst.DrawImage(src, new Point(100, 100));   // fully outside
        dst.DrawImage(src, new Point(int.MinValue, int.MaxValue));
        Assert.Equal(Rgba32.White, dst[5, 0]);
    }

    [Fact]
    public void DrawImage_OpacityBlends_AndZeroIsNoOp()
    {
        using var dst = new Image(2, 2, Color.Black);
        using var src = new Image(2, 2, Color.White);
        dst.DrawImage(src, Point.Empty, 0f);
        Assert.Equal(Rgba32.Black, dst[0, 0]);
        dst.DrawImage(src, Point.Empty, 0.5f);
        Assert.Equal(new Rgba32(128, 128, 128), dst[1, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => dst.DrawImage(src, Point.Empty, 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => dst.DrawImage(src, Point.Empty, float.NaN));
    }

    [Fact]
    public void DrawImage_OverTransparent_KeepsSourceColor_AndSelfDrawWorks()
    {
        using var dst = new Image(3, 3);
        using var src = new Image(3, 3, Color.FromArgb(100, 10, 20, 30));
        dst.DrawImage(src, Point.Empty);
        Assert.Equal(new Rgba32(10, 20, 30, 100), dst[1, 1]);

        using Image img = ImageTests.Pattern(4, 4);
        using Image expected = img.Clone();
        using Image copy = img.Clone();
        expected.DrawImage(copy, new Point(1, 1));
        img.DrawImage(img, new Point(1, 1));
        Assert.Equal(Pixels(expected), Pixels(img));
    }

    // ------------------------------------------------------------ pixel formats

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void CopyPixels_LoadPixels_RoundTripIsStable(PixelFormat format)
    {
        using Image img = ProcessingOracleTests.Noise(13, 7, 7);
        int bpp = format.BytesPerPixel();
        int stride = (13 * bpp) + 5;
        var first = new byte[stride * 7];
        img.CopyPixels(first, format, stride);

        using var copy = new Image(13, 7);
        copy.LoadPixels(first, format, stride);
        var second = new byte[stride * 7];
        copy.CopyPixels(second, format, stride);
        for (int y = 0; y < 7; y++)
        {
            Assert.Equal(first.AsSpan(y * stride, 13 * bpp).ToArray(), second.AsSpan(y * stride, 13 * bpp).ToArray());
        }

        if (format is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Argb32)
        {
            Assert.Equal(Pixels(img), Pixels(copy));
        }
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void LockBits_WriteThenRead_RoundTripsInEveryFormat(PixelFormat format)
    {
        using Image source = ProcessingOracleTests.Noise(5, 4, 8);
        byte[] expected = source.ToPixelArray(format);
        var rect = new Rectangle(2, 1, 5, 4);

        using var img = new Image(9, 7, Color.Blue);
        using (BitmapData data = img.LockBits(rect, ImageLockMode.WriteOnly, format))
        {
            Assert.Equal(format, data.PixelFormat);
            Assert.Equal((5, 4), (data.Width, data.Height));
            for (int y = 0; y < 4; y++)
            {
                expected.AsSpan(y * 5 * format.BytesPerPixel(), 5 * format.BytesPerPixel()).CopyTo(data.Span[(y * data.Stride)..]);
            }
        }

        BitmapData read = img.LockBits(rect, ImageLockMode.ReadOnly, format);
        try
        {
            for (int y = 0; y < 4; y++)
            {
                Assert.Equal(
                    expected.AsSpan(y * 5 * format.BytesPerPixel(), 5 * format.BytesPerPixel()).ToArray(),
                    read.Span.Slice(y * read.Stride, 5 * format.BytesPerPixel()).ToArray());
            }
        }
        finally
        {
            img.UnlockBits(read);
        }

        Assert.Equal(new Rgba32(0, 0, 255), img[1, 1]);   // outside the locked rectangle
        Assert.Equal(new Rgba32(0, 0, 255), img[7, 5]);
        using Image region = img.Clone(rect);
        Assert.Equal(expected, region.ToPixelArray(format));
    }

    // ------------------------------------------------------------ helpers

    private static Rgba32[,] ToGrid(Image img)
    {
        var grid = new Rgba32[img.Height, img.Width];
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                grid[y, x] = img[x, y];
            }
        }

        return grid;
    }

    private static Rgba32[,] RotateClockwise(Rgba32[,] g)
    {
        int h = g.GetLength(0), w = g.GetLength(1);
        var r = new Rgba32[w, h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                r[x, h - 1 - y] = g[y, x];
            }
        }

        return r;
    }

    private static Rgba32[,] FlipX(Rgba32[,] g)
    {
        int h = g.GetLength(0), w = g.GetLength(1);
        var r = new Rgba32[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                r[y, w - 1 - x] = g[y, x];
            }
        }

        return r;
    }
}
