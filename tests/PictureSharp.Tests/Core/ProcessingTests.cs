using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class ProcessingTests
{
    public static TheoryData<ResamplingFilter> AllFilters => [ResamplingFilter.Nearest, ResamplingFilter.Bilinear, ResamplingFilter.Bicubic, ResamplingFilter.Lanczos3];

    public static TheoryData<RotateMode, FlipMode> AllRotateFlips()
    {
        var data = new TheoryData<RotateMode, FlipMode>();
        foreach (RotateMode rotate in Enum.GetValues<RotateMode>())
        {
            foreach (FlipMode flip in Enum.GetValues<FlipMode>())
            {
                data.Add(rotate, flip);
            }
        }

        return data;
    }

    public static TheoryData<PixelFormat> AllFormats => new(Enum.GetValues<PixelFormat>());

    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan().ToArray();

    // ------------------------------------------------------------ geometry

    [Theory]
    [MemberData(nameof(AllRotateFlips))]
    public void RotateFlip_MatchesNaiveRotationThenFlip(RotateMode rotate, FlipMode flip)
    {
        using Image img = ImageTests.Pattern(7, 4);
        Rgba32[,] expected = ToGrid(img);
        for (int i = 0; i < (int)rotate / 90; i++)
        {
            expected = RotateClockwise(expected);
        }

        if (flip == FlipMode.Horizontal)
        {
            expected = FlipX(expected);
        }
        else if (flip == FlipMode.Vertical)
        {
            expected = FlipY(expected);
        }

        img.Mutate(x => x.RotateFlip(rotate, flip));
        Assert.Equal(expected, ToGrid(img));
    }

    [Theory]
    [MemberData(nameof(AllRotateFlips))]
    public void RotateFlip_FourTimesQuarterTurnOrTwiceFlip_IsIdentity(RotateMode rotate, FlipMode flip)
    {
        using Image img = ImageTests.Pattern(6, 5);
        Rgba32[] before = Pixels(img);
        int repeat = flip != FlipMode.None ? 2 : 4;   // every flip variant is an involution; rotations have order ≤ 4
        for (int i = 0; i < repeat; i++)
        {
            img.Mutate(x => x.RotateFlip(rotate, flip));
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
        (RotateMode rotate, FlipMode flip) = Inverse((ExifOrientation)orientation);
        using Image stored = upright.Clone(x => x.RotateFlip(rotate, flip));
        stored.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal((ExifOrientation)orientation, stored.Metadata.Orientation);
        stored.Mutate(x => x.AutoOrient());
        Assert.Equal(Pixels(upright), Pixels(stored));
        Assert.Equal(upright.Size, stored.Size);
        Assert.Equal(ExifOrientation.TopLeft, stored.Metadata.Orientation);
    }

    private static (RotateMode, FlipMode) Inverse(ExifOrientation orientation) => orientation switch
    {
        ExifOrientation.RightTop => (RotateMode.Rotate270, FlipMode.None),
        ExifOrientation.LeftBottom => (RotateMode.Rotate90, FlipMode.None),
        ExifOrientation.TopRight => (RotateMode.None, FlipMode.Horizontal),
        ExifOrientation.BottomRight => (RotateMode.Rotate180, FlipMode.None),
        ExifOrientation.BottomLeft => (RotateMode.None, FlipMode.Vertical),
        ExifOrientation.LeftTop => (RotateMode.Rotate90, FlipMode.Horizontal),
        ExifOrientation.RightBottom => (RotateMode.Rotate270, FlipMode.Horizontal),
        _ => (RotateMode.None, FlipMode.None),
    };

    [Theory]
    [InlineData(0, 0, 7, 4)]
    [InlineData(6, 3, 1, 1)]
    [InlineData(2, 1, 3, 2)]
    public void Crop_TakesExactRegion_OnAllFrames(int x, int y, int w, int h)
    {
        using Image a = ImageTests.Pattern(7, 4);
        using Image b = a.Clone(c => c.Invert());
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(30), a, b);
        anim.Mutate(c => c.Crop(new Rectangle(x, y, w, h)));
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
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(c => c.Crop(new Rectangle(x, y, w, h))));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Clone(c => c.Crop(x, y, w, h)));
        Assert.Equal(new Size(7, 4), img.Size);
    }

    // ------------------------------------------------------------ resize

    [Theory]
    [InlineData(ResizeMode.Stretch, 40, 40, 40, 40)]
    [InlineData(ResizeMode.Max, 40, 40, 40, 20)]
    [InlineData(ResizeMode.Max, 300, 50, 100, 50)]
    [InlineData(ResizeMode.Crop, 40, 40, 40, 40)]
    [InlineData(ResizeMode.Crop, 10, 70, 10, 70)]
    [InlineData(ResizeMode.Pad, 40, 40, 40, 40)]
    [InlineData(ResizeMode.Min, 40, 40, 80, 40)]
    [InlineData(ResizeMode.Min, 300, 50, 300, 150)]
    [InlineData(ResizeMode.Stretch, 30, 0, 30, 15)]
    [InlineData(ResizeMode.Stretch, 0, 25, 50, 25)]
    [InlineData(ResizeMode.Crop, 0, 20, 40, 20)]
    public void Resize_ModesProduceExpectedSizes(ResizeMode mode, int w, int h, int ew, int eh)
    {
        using Image img = ProcessingOracleTests.Noise(100, 50, 3);
        img.Mutate(x => x.Resize(w, h, mode));
        Assert.Equal(new Size(ew, eh), img.Size);
    }

    [Fact]
    public void Resize_Pad_CentersAndFillsBorders()
    {
        using var img = new Image(100, 50, Color.Red);
        img.Mutate(x => x.Resize(40, 40, ResizeMode.Pad, ResamplingFilter.Bilinear));
        Assert.True(img.HasAlpha);
        Assert.Equal(Rgba32.Transparent, img[20, 0]);
        Assert.Equal(Rgba32.Transparent, img[20, 9]);
        Assert.Equal(new Rgba32(255, 0, 0), img[20, 10]);
        Assert.Equal(new Rgba32(255, 0, 0), img[20, 29]);
        Assert.Equal(Rgba32.Transparent, img[20, 30]);

        using var white = new Image(50, 100, Color.Red);
        white.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new(41, 40),
            Mode = ResizeMode.Pad,
            Filter = ResamplingFilter.Bicubic,
            PadColor = Color.White,
        }));
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
        img.Mutate(x => x.Resize(9, 40, ResizeMode.Max));
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
            using var img = new Image(37, 23, color);
            foreach ((int w, int h) in new[] { (11, 5), (90, 61), (37, 9), (1, 1) })
            {
                using Image resized = img.Clone(x => x.Resize(w, h, filter));
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

        img.Mutate(x => x.Resize(7, 13, filter));
        Assert.All(Pixels(img), p => Assert.True(p.A == 0 || p.R == 0, p.ToString()));
        Assert.Contains(Pixels(img), p => p.A != 0);
    }

    [Fact]
    public void Resize_SameSize_IsIdentity_AndNearestDoublesPixels()
    {
        using Image img = ImageTests.Pattern(9, 6);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Resize(9, 6));
        Assert.Equal(before, Pixels(img));

        using Image big = img.Clone(x => x.Resize(18, 12, ResamplingFilter.Nearest));
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
        using Image a = img.Clone(x => x.Resize(1001, 333));
        using Image b = img.Clone(x => x.Resize(1001, 333));
        Assert.Equal(Pixels(a), Pixels(b));
        using Image down = img.Clone(x => x.Resize(7, 5));
        Assert.Equal(new Size(7, 5), down.Size);
    }

    [Fact]
    public void Resize_AppliesToAllFrames_AndKeepsDurations()
    {
        using Image a = ImageTests.Pattern(8, 6);
        using Image b = new(8, 6, Color.Green);
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(70), a, b);
        anim.LoopCount = 3;
        anim.Mutate(x => x.Resize(4, 3, ResamplingFilter.Bilinear));
        Assert.All(anim.Frames, f => Assert.Equal((4, 3, TimeSpan.FromMilliseconds(70)), (f.Width, f.Height, f.Duration)));
        Assert.Equal(new Rgba32(0, 128, 0), anim.Frames[1][2, 1]);
        Assert.Equal(3, anim.LoopCount);
    }

    [Fact]
    public void Thumbnail_ShrinksKeepingAspect_AndNeverEnlarges()
    {
        using Image img = ProcessingOracleTests.Noise(120, 80, 6);
        img.Mutate(x => x.Thumbnail(60, 60));
        Assert.Equal(new Size(60, 40), img.Size);
        img.Mutate(x => x.Thumbnail(1000, 1000));
        Assert.Equal(new Size(60, 40), img.Size);
        img.Mutate(x => x.Thumbnail(30, 0));
        Assert.Equal(new Size(30, 20), img.Size);
    }

    [Fact]
    public void CloneResize_ReturnsNewImage_AndLeavesSourceUntouched()
    {
        using Image img = ImageTests.Pattern(10, 10);
        img.Metadata.HorizontalResolution = 300;
        Rgba32[] before = Pixels(img);
        using Image thumb = img.Clone(x => x.Resize(3, 4));
        Assert.Equal(new Size(3, 4), thumb.Size);
        Assert.Equal(300f, thumb.Metadata.HorizontalResolution);
        Assert.Equal(before, Pixels(img));
        Assert.Equal(new Size(10, 10), img.Size);
    }

    [Fact]
    public void Resize_InvalidArguments_Throw()
    {
        using Image img = ImageTests.Pattern(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Resize(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Resize(3, -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Resize(3, 3, (ResizeMode)9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Resize(3, 3, ResizeMode.Max, (ResamplingFilter)9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Thumbnail(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResizeOptions { Size = new(-1, 3) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResizeOptions { Size = new(3, 3), Position = (AnchorPosition)99 });
        Assert.Equal(new Size(4, 4), img.Size);
    }

    // ------------------------------------------------------------ color operations

    [Fact]
    public void Grayscale_UsesBt601_AndKeepsAlpha()
    {
        using Image img = ImageTests.Pattern(6, 4);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Grayscale());
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
        img.Mutate(x => x.Invert());
        Assert.Equal(new Rgba32((byte)(255 - before[5].R), (byte)(255 - before[5].G), (byte)(255 - before[5].B), before[5].A), img.GetPixelSpan()[5]);
        img.Mutate(x => x.Invert());
        Assert.Equal(before, Pixels(img));
    }

    [Fact]
    public void Fill_ClipsToBounds_AndOverwrites()
    {
        using var img = new Image(5, 4, Color.Black);
        img.Mutate(x => x.Fill(Color.FromArgb(128, 255, 0, 0), new Rectangle(3, -2, 10, 4)));
        Assert.True(img.HasAlpha);
        Assert.Equal(new Rgba32(255, 0, 0, 128), img[3, 0]);
        Assert.Equal(new Rgba32(255, 0, 0, 128), img[4, 1]);
        Assert.Equal(Rgba32.Black, img[2, 0]);
        Assert.Equal(Rgba32.Black, img[4, 2]);
        img.Mutate(x => x.Fill(Color.White, new Rectangle(10, 10, 3, 3)));   // fully outside: no-op
        img.Mutate(x => x.Fill(Color.Blue, new Rectangle(0, 0, 5, 4)));
        Assert.All(Pixels(img), p => Assert.Equal(new Rgba32(0, 0, 255), p));
    }

    [Fact]
    public void Fill_UpdatesHasAlpha()
    {
        using var img = new Image(2, 2);
        img.Mutate(x => x.Fill(Color.White));
        Assert.False(img.HasAlpha);
        img.Mutate(x => x.Fill(Color.Transparent));
        Assert.True(img.HasAlpha);
    }

    [Fact]
    public void MakeTransparent_ClearsMatchingPixelsOnAllFrames()
    {
        using Image a = new(3, 2, Color.White);
        a[1, 0] = Rgba32.Black;
        using Image b = a.Clone();
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(10), a, b);
        anim.HasAlpha = false;
        anim.Mutate(x => x.MakeTransparent(Color.White));
        Assert.True(anim.HasAlpha);
        foreach (ImageFrame frame in anim.Frames)
        {
            Assert.Equal(new Rgba32(255, 255, 255, 0), frame[0, 0]);
            Assert.Equal(Rgba32.Black, frame[1, 0]);
        }
    }

    // ------------------------------------------------------------ drawing

    [Fact]
    public void DrawImage_OpaqueSourceReplaces_ClippedAtEdges()
    {
        using var dst = new Image(6, 5, Color.White);
        using var src = new Image(3, 3, Color.Red);
        dst.Mutate(x => x.DrawImage(src, new Point(-1, 3)));
        Assert.Equal(new Rgba32(255, 0, 0), dst[0, 3]);
        Assert.Equal(new Rgba32(255, 0, 0), dst[1, 4]);
        Assert.Equal(Rgba32.White, dst[2, 3]);
        Assert.Equal(Rgba32.White, dst[0, 2]);
        dst.Mutate(x => x.DrawImage(src, new Point(100, 100)).DrawImage(src, new Point(int.MinValue, int.MaxValue)));   // fully outside
        Assert.Equal(Rgba32.White, dst[5, 0]);
    }

    [Fact]
    public void DrawImage_OpacityBlends_AndZeroIsNoOp()
    {
        using var dst = new Image(2, 2, Color.Black);
        using var src = new Image(2, 2, Color.White);
        dst.Mutate(x => x.DrawImage(src, Point.Empty, 0f));
        Assert.Equal(Rgba32.Black, dst[0, 0]);
        dst.Mutate(x => x.DrawImage(src, Point.Empty, 0.5f));
        Assert.Equal(new Rgba32(128, 128, 128), dst[1, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => dst.Mutate(x => x.DrawImage(src, Point.Empty, 1.5f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => dst.Mutate(x => x.DrawImage(src, Point.Empty, float.NaN)));
    }

    [Fact]
    public void DrawImage_OverTransparent_KeepsSourceColor_AndSelfDrawWorks()
    {
        using var dst = new Image(3, 3);
        using var src = new Image(3, 3, Color.FromArgb(100, 10, 20, 30));
        dst.Mutate(x => x.DrawImage(src, Point.Empty));
        Assert.Equal(new Rgba32(10, 20, 30, 100), dst[1, 1]);

        using Image img = ImageTests.Pattern(4, 4);
        using Image expected = img.Clone();
        using Image copy = img.Clone();
        expected.Mutate(x => x.DrawImage(copy, new Point(1, 1)));
        img.Mutate(x => x.DrawImage(img, new Point(1, 1)));
        Assert.Equal(Pixels(expected), Pixels(img));
    }

    // ------------------------------------------------------------ pixel formats

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void CopyPixelData_RoundTripIsStable(PixelFormat format)
    {
        using Image img = ProcessingOracleTests.Noise(13, 7, 7);
        int bpp = format.BytesPerPixel();
        int stride = (13 * bpp) + 5;
        var first = new byte[stride * 7];
        img.CopyPixelDataTo(first, format, stride);

        using var copy = new Image(13, 7);
        copy.CopyPixelDataFrom(first, format, stride);
        var second = new byte[stride * 7];
        copy.CopyPixelDataTo(second, format, stride);
        for (int y = 0; y < 7; y++)
        {
            Assert.Equal(first.AsSpan(y * stride, 13 * bpp).ToArray(), second.AsSpan(y * stride, 13 * bpp).ToArray());
        }

        if (format is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Argb32)
        {
            Assert.Equal(Pixels(img), Pixels(copy));
        }
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

    private static Rgba32[,] FlipY(Rgba32[,] g)
    {
        int h = g.GetLength(0), w = g.GetLength(1);
        var r = new Rgba32[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                r[h - 1 - y, x] = g[y, x];
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

    [Fact]
    public void ParameterizedPixelOperations_AllocateNoMoreThanStatelessOperation()
    {
        using var image = new Image(64, 64, new Rgba32(10, 20, 30, 255));
        long Measure(Action<ImageProcessingContext> operation)
        {
            image.Mutate(operation);
            // The minimum of several runs ignores one-off runtime allocations (e.g. per-thread pool buckets)
            // that can land in a measurement while other tests run in parallel.
            long best = long.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                image.Mutate(operation);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return best;
        }

        long baseline = Measure(static x => x.Invert());
        // A captured parameter would add a closure and a delegate per call on top of the stateless baseline.
        Assert.InRange(Measure(static x => x.Brightness(1.1f)), 0, baseline);
        Assert.InRange(Measure(static x => x.Contrast(0.9f)), 0, baseline);
        Assert.InRange(Measure(static x => x.Saturate(1.2f)), 0, baseline);
        Assert.InRange(Measure(static x => x.Opacity(0.9f)), 0, baseline);
        Assert.InRange(Measure(static x => x.Fill(new Rgba32(1, 2, 3, 255))), 0, baseline);
        Assert.InRange(Measure(static x => x.MakeTransparent(new Rgba32(1, 2, 3, 4))), 0, baseline);
        Assert.InRange(Measure(static x => x.BackgroundColor(new Rgba32(5, 6, 7, 255))), 0, baseline);
    }
}
