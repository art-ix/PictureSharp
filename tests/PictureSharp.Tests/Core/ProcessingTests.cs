using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class ProcessingTests
{
    public static TestCases<ResamplingFilter> AllFilters => [ResamplingFilter.Nearest, ResamplingFilter.Bilinear, ResamplingFilter.Bicubic, ResamplingFilter.Lanczos3];

    public static TestCases<RotateMode, FlipMode> AllRotateFlips()
    {
        var data = new TestCases<RotateMode, FlipMode>();
        foreach (RotateMode rotate in Enum.GetValues<RotateMode>())
        {
            foreach (FlipMode flip in Enum.GetValues<FlipMode>())
            {
                data.Add(rotate, flip);
            }
        }

        return data;
    }

    public static TestCases<PixelFormat> AllFormats => new(Enum.GetValues<PixelFormat>());

    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan<Rgba32>().ToArray();

    // ------------------------------------------------------------ geometry

    [Test]
    [MethodDataSource(nameof(AllRotateFlips))]
    public async Task RotateFlip_MatchesNaiveRotationThenFlip(RotateMode rotate, FlipMode flip)
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
        await Assert.That(ToGrid(img).Cast<Rgba32>()).IsEquivalentTo(expected.Cast<Rgba32>(), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(AllRotateFlips))]
    public async Task RotateFlip_FourTimesQuarterTurnOrTwiceFlip_IsIdentity(RotateMode rotate, FlipMode flip)
    {
        using Image img = ImageTests.Pattern(6, 5);
        Rgba32[] before = Pixels(img);
        int repeat = flip != FlipMode.None ? 2 : 4;   // every flip variant is an involution; rotations have order ≤ 4
        for (int i = 0; i < repeat; i++)
        {
            img.Mutate(x => x.RotateFlip(rotate, flip));
        }

        await Assert.That(Pixels(img)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    public async Task AutoOrient_UndoesEachExifOrientation(int orientation)
    {
        // Store an upright pattern the way a camera with this orientation would, then check AutoOrient restores it.
        using Image upright = ImageTests.Pattern(5, 3);
        (RotateMode rotate, FlipMode flip) = Inverse((ExifOrientation)orientation);
        using Image stored = upright.Clone(x => x.RotateFlip(rotate, flip));
        stored.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        await Assert.That(stored.Metadata.Orientation).IsEqualTo((ExifOrientation)orientation);
        stored.Mutate(x => x.AutoOrient());
        await Assert.That(Pixels(stored)).IsEquivalentTo(Pixels(upright), CollectionOrdering.Matching);
        await Assert.That(stored.Size).IsEqualTo(upright.Size);
        await Assert.That(stored.Metadata.Orientation).IsEqualTo(ExifOrientation.TopLeft);
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

    [Test]
    public async Task RotateFlip_AppliesToAllFrames_AndSwapsDpi()
    {
        using Image a = ImageTests.Pattern(4, 2);
        using Image b = ImageTests.Pattern(4, 2);
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), a, b);
        anim.Metadata.HorizontalResolution = 300;
        anim.Metadata.VerticalResolution = 72;
        anim.Mutate(x => x.Rotate(RotateMode.Rotate270));
        foreach (var f in anim.Frames)
        {
            await Assert.That((f.Width, f.Height)).IsEqualTo((2, 4));
        }

        await Assert.That(anim.Metadata.HorizontalResolution).IsEqualTo(72f);
        await Assert.That(anim.Metadata.VerticalResolution).IsEqualTo(300f);
    }

    [Test]
    public void RotateFlip_UnknownModes_Throw()
    {
        using Image img = ImageTests.Pattern(2, 2);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Rotate((RotateMode)45)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Flip((FlipMode)7)));
    }

    [Test]
    [Arguments(0, 0, 7, 4)]
    [Arguments(6, 3, 1, 1)]
    [Arguments(2, 1, 3, 2)]
    public async Task Crop_TakesExactRegion_OnAllFrames(int x, int y, int w, int h)
    {
        using Image a = ImageTests.Pattern(7, 4);
        using Image b = a.Clone(c => c.Invert());
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(30), a, b);
        anim.Mutate(c => c.Crop(new Rectangle(x, y, w, h)));
        await Assert.That(anim.Size).IsEqualTo(new Size(w, h));
        for (int f = 0; f < 2; f++)
        {
            Image source = f == 0 ? a : b;
            for (int yy = 0; yy < h; yy++)
            {
                for (int xx = 0; xx < w; xx++)
                {
                    await Assert.That(anim.Frames[f][xx, yy]).IsEqualTo(source[x + xx, y + yy]);
                }
            }
        }
    }

    [Test]
    [Arguments(-1, 0, 2, 2)]
    [Arguments(0, -1, 2, 2)]
    [Arguments(6, 0, 2, 2)]
    [Arguments(0, 3, 2, 2)]
    [Arguments(0, 0, 0, 2)]
    [Arguments(0, 0, 2, 0)]
    [Arguments(0, 0, 8, 4)]
    [Arguments(int.MaxValue, 0, 2, 2)]
    public async Task Crop_OutsideBounds_Throws(int x, int y, int w, int h)
    {
        using Image img = ImageTests.Pattern(7, 4);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(c => c.Crop(new Rectangle(x, y, w, h))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Clone(c => c.Crop(x, y, w, h)));
        await Assert.That(img.Size).IsEqualTo(new Size(7, 4));
    }

    // ------------------------------------------------------------ resize

    [Test]
    [Arguments(ResizeMode.Stretch, 40, 40, 40, 40)]
    [Arguments(ResizeMode.Max, 40, 40, 40, 20)]
    [Arguments(ResizeMode.Max, 300, 50, 100, 50)]
    [Arguments(ResizeMode.Crop, 40, 40, 40, 40)]
    [Arguments(ResizeMode.Crop, 10, 70, 10, 70)]
    [Arguments(ResizeMode.Pad, 40, 40, 40, 40)]
    [Arguments(ResizeMode.Min, 40, 40, 80, 40)]
    [Arguments(ResizeMode.Min, 300, 50, 300, 150)]
    [Arguments(ResizeMode.Stretch, 30, 0, 30, 15)]
    [Arguments(ResizeMode.Stretch, 0, 25, 50, 25)]
    [Arguments(ResizeMode.Crop, 0, 20, 40, 20)]
    public async Task Resize_ModesProduceExpectedSizes(ResizeMode mode, int w, int h, int ew, int eh)
    {
        using Image img = ProcessingOracleTests.Noise(100, 50, 3);
        img.Mutate(x => x.Resize(w, h, mode));
        await Assert.That(img.Size).IsEqualTo(new Size(ew, eh));
    }

    [Test]
    public async Task Resize_Pad_CentersAndFillsBorders()
    {
        using var img = new Image(100, 50, Color.Red);
        img.Mutate(x => x.Resize(40, 40, ResizeMode.Pad, ResamplingFilter.Bilinear));
        await Assert.That(img.HasAlpha).IsTrue();
        await Assert.That(img[20, 0]).IsEqualTo(Rgba32.Transparent);
        await Assert.That(img[20, 9]).IsEqualTo(Rgba32.Transparent);
        await Assert.That(img[20, 10]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(img[20, 29]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(img[20, 30]).IsEqualTo(Rgba32.Transparent);

        using var white = new Image(50, 100, Color.Red);
        white.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new(41, 40),
            Mode = ResizeMode.Pad,
            Filter = ResamplingFilter.Bicubic,
            PadColor = Color.White,
        }));
        await Assert.That(white.HasAlpha).IsFalse();
        await Assert.That((white.Width, white.Height)).IsEqualTo((41, 40));
        await Assert.That(white[9, 20]).IsEqualTo(Rgba32.White);     // 20 px wide content; left border round(10.5) = 10 (half to even)
        await Assert.That(white[10, 20]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(white[29, 20]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(white[30, 20]).IsEqualTo(Rgba32.White);
    }

    [Test]
    public async Task Resize_FitThatRoundsToZero_KeepsOnePixel()
    {
        using Image img = ProcessingOracleTests.Noise(100, 3, 4);
        img.Mutate(x => x.Resize(9, 40, ResizeMode.Max));
        await Assert.That(img.Size).IsEqualTo(new Size(9, 1));
    }

    [Test]
    [MethodDataSource(nameof(AllFilters))]
    public async Task Resize_UniformImage_StaysUniform(ResamplingFilter filter)
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
                foreach (var p in Pixels(resized))
                {
                    await Assert.That(p).IsEqualTo(expected);
                }
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

    [Test]
    [MethodDataSource(nameof(AllFilters))]
    public async Task Resize_TransparentColor_DoesNotBleed(ResamplingFilter filter)
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
        foreach (var p in Pixels(img))
        {
            await Assert.That(p.A == 0 || p.R == 0).IsTrue().Because(p.ToString());
        }

        await Assert.That(Pixels(img)).Contains(p => p.A != 0);
    }

    [Test]
    public async Task Resize_SameSize_IsIdentity_AndNearestDoublesPixels()
    {
        using Image img = ImageTests.Pattern(9, 6);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Resize(9, 6));
        await Assert.That(Pixels(img)).IsEquivalentTo(before, CollectionOrdering.Matching);

        using Image big = img.Clone(x => x.Resize(18, 12, ResamplingFilter.Nearest));
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 18; x++)
            {
                await Assert.That(big[x, y]).IsEqualTo(img[x / 2, y / 2]);
            }
        }
    }

    [Test]
    public async Task Resize_AppliesToAllFrames_AndKeepsDurations()
    {
        using Image a = ImageTests.Pattern(8, 6);
        using Image b = new(8, 6, Color.Green);
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(70), a, b);
        anim.LoopCount = 3;
        anim.Mutate(x => x.Resize(4, 3, ResamplingFilter.Bilinear));
        foreach (var f in anim.Frames)
        {
            await Assert.That((f.Width, f.Height, f.Duration)).IsEqualTo((4, 3, TimeSpan.FromMilliseconds(70)));
        }

        await Assert.That(anim.Frames[1][2, 1]).IsEqualTo(new Rgba32(0, 128, 0));
        await Assert.That(anim.LoopCount).IsEqualTo(3);
    }

    [Test]
    public async Task Thumbnail_ShrinksKeepingAspect_AndNeverEnlarges()
    {
        using Image img = ProcessingOracleTests.Noise(120, 80, 6);
        img.Mutate(x => x.Thumbnail(60, 60));
        await Assert.That(img.Size).IsEqualTo(new Size(60, 40));
        img.Mutate(x => x.Thumbnail(1000, 1000));
        await Assert.That(img.Size).IsEqualTo(new Size(60, 40));
        img.Mutate(x => x.Thumbnail(30, 0));
        await Assert.That(img.Size).IsEqualTo(new Size(30, 20));
    }

    [Test]
    public async Task Resize_InvalidArguments_Throw()
    {
        using Image img = ImageTests.Pattern(4, 4);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Resize(0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Resize(3, -1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Resize(3, 3, (ResizeMode)9)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Resize(3, 3, ResizeMode.Max, (ResamplingFilter)9)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Thumbnail(0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ResizeOptions { Size = new(-1, 3) });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ResizeOptions { Size = new(3, 3), Position = (AnchorPosition)99 });
        await Assert.That(img.Size).IsEqualTo(new Size(4, 4));
    }

    // ------------------------------------------------------------ color operations

    [Test]
    public async Task Grayscale_UsesBt601_AndKeepsAlpha()
    {
        using Image img = ImageTests.Pattern(6, 4);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Grayscale());
        Rgba32[] after = Pixels(img);
        for (int i = 0; i < before.Length; i++)
        {
            byte l = (byte)(((19595 * before[i].R) + (38470 * before[i].G) + (7471 * before[i].B) + 32768) >> 16);
            await Assert.That(after[i]).IsEqualTo(new Rgba32(l, l, l, before[i].A));
        }
    }

    [Test]
    public async Task Invert_TwiceIsIdentity_AndKeepsAlpha()
    {
        using Image img = ImageTests.Pattern(6, 4);
        Rgba32[] before = Pixels(img);
        img.Mutate(x => x.Invert());
        await Assert.That(img.GetPixelSpan<Rgba32>()[5]).IsEqualTo(new Rgba32((byte)(255 - before[5].R), (byte)(255 - before[5].G), (byte)(255 - before[5].B), before[5].A));
        img.Mutate(x => x.Invert());
        await Assert.That(Pixels(img)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Fill_ClipsToBounds_AndOverwrites()
    {
        using var img = new Image(5, 4, Color.Black);
        img.Mutate(x => x.Fill(Color.FromArgb(128, 255, 0, 0), new Rectangle(3, -2, 10, 4)));
        await Assert.That(img.HasAlpha).IsTrue();
        await Assert.That(img[3, 0]).IsEqualTo(new Rgba32(255, 0, 0, 128));
        await Assert.That(img[4, 1]).IsEqualTo(new Rgba32(255, 0, 0, 128));
        await Assert.That(img[2, 0]).IsEqualTo(Rgba32.Black);
        await Assert.That(img[4, 2]).IsEqualTo(Rgba32.Black);
        img.Mutate(x => x.Fill(Color.White, new Rectangle(10, 10, 3, 3)));   // fully outside: no-op
        img.Mutate(x => x.Fill(Color.Blue, new Rectangle(0, 0, 5, 4)));
        foreach (var p in Pixels(img))
        {
            await Assert.That(p).IsEqualTo(new Rgba32(0, 0, 255));
        }
    }

    [Test]
    public async Task Fill_UpdatesHasAlpha()
    {
        using var img = new Image(2, 2);
        img.Mutate(x => x.Fill(Color.White));
        await Assert.That(img.HasAlpha).IsFalse();
        img.Mutate(x => x.Fill(Color.Transparent));
        await Assert.That(img.HasAlpha).IsTrue();
    }

    [Test]
    public async Task MakeTransparent_ClearsMatchingPixelsOnAllFrames()
    {
        using Image a = new(3, 2, Color.White);
        a[1, 0] = Rgba32.Black;
        using Image b = a.Clone();
        using Image anim = Image.CreateAnimation(TimeSpan.FromMilliseconds(10), a, b);
        anim.HasAlpha = false;
        anim.Mutate(x => x.MakeTransparent(Color.White));
        await Assert.That(anim.HasAlpha).IsTrue();
        foreach (ImageFrame frame in anim.Frames)
        {
            await Assert.That(frame[0, 0]).IsEqualTo(new Rgba32(255, 255, 255, 0));
            await Assert.That(frame[1, 0]).IsEqualTo(Rgba32.Black);
        }
    }

    // ------------------------------------------------------------ drawing

    [Test]
    public async Task DrawImage_OpaqueSourceReplaces_ClippedAtEdges()
    {
        using var dst = new Image(6, 5, Color.White);
        using var src = new Image(3, 3, Color.Red);
        dst.Mutate(x => x.DrawImage(src, new Point(-1, 3)));
        await Assert.That(dst[0, 3]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(dst[1, 4]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(dst[2, 3]).IsEqualTo(Rgba32.White);
        await Assert.That(dst[0, 2]).IsEqualTo(Rgba32.White);
        dst.Mutate(x => x.DrawImage(src, new Point(100, 100)).DrawImage(src, new Point(int.MinValue, int.MaxValue)));   // fully outside
        await Assert.That(dst[5, 0]).IsEqualTo(Rgba32.White);
    }

    [Test]
    public async Task DrawImage_OpacityBlends_AndZeroIsNoOp()
    {
        using var dst = new Image(2, 2, Color.Black);
        using var src = new Image(2, 2, Color.White);
        dst.Mutate(x => x.DrawImage(src, Point.Empty, 0f));
        await Assert.That(dst[0, 0]).IsEqualTo(Rgba32.Black);
        dst.Mutate(x => x.DrawImage(src, Point.Empty, 0.5f));
        await Assert.That(dst[1, 1]).IsEqualTo(new Rgba32(128, 128, 128));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => dst.Mutate(x => x.DrawImage(src, Point.Empty, 1.5f)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => dst.Mutate(x => x.DrawImage(src, Point.Empty, float.NaN)));
    }

    [Test]
    public async Task DrawImage_OverTransparent_KeepsSourceColor_AndSelfDrawWorks()
    {
        using var dst = new Image(3, 3);
        using var src = new Image(3, 3, Color.FromArgb(100, 10, 20, 30));
        dst.Mutate(x => x.DrawImage(src, Point.Empty));
        await Assert.That(dst[1, 1]).IsEqualTo(new Rgba32(10, 20, 30, 100));

        using Image img = ImageTests.Pattern(4, 4);
        using Image expected = img.Clone();
        using Image copy = img.Clone();
        expected.Mutate(x => x.DrawImage(copy, new Point(1, 1)));
        img.Mutate(x => x.DrawImage(img, new Point(1, 1)));
        await Assert.That(Pixels(img)).IsEquivalentTo(Pixels(expected), CollectionOrdering.Matching);
    }

    // ------------------------------------------------------------ pixel formats

    [Test]
    [MethodDataSource(nameof(AllFormats))]
    public async Task CopyPixelData_RoundTripIsStable(PixelFormat format)
    {
        using Image img = ProcessingOracleTests.Noise(13, 7, 7);
        int bpp = format.BytesPerPixel();
        int stride = (13 * bpp) + 5;
        var first = new byte[stride * 7];
        img.CopyPixelDataTo(first, format, stride);

        // A 16-bit layout round-trips through 16-bit storage; 8-bit storage would round it.
        using var copy = new Image(13, 7, format.BitsPerChannel() == 16 ? PixelType.Rgba64 : PixelType.Rgba32);
        copy.CopyPixelDataFrom(first, format, stride);
        var second = new byte[stride * 7];
        copy.CopyPixelDataTo(second, format, stride);
        for (int y = 0; y < 7; y++)
        {
            await Assert.That(second.AsSpan(y * stride, 13 * bpp).ToArray()).IsEquivalentTo(first.AsSpan(y * stride, 13 * bpp).ToArray(), CollectionOrdering.Matching);
        }

        if (format is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Argb32)
        {
            await Assert.That(Pixels(copy)).IsEquivalentTo(Pixels(img), CollectionOrdering.Matching);
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

    [Test]
    public async Task ParameterizedPixelOperations_AllocateNoMoreThanStatelessOperation()
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
        await Assert.That(Measure(static x => x.Brightness(1.1f))).IsBetween(0L, baseline);
        await Assert.That(Measure(static x => x.Contrast(0.9f))).IsBetween(0L, baseline);
        await Assert.That(Measure(static x => x.Saturate(1.2f))).IsBetween(0L, baseline);
        await Assert.That(Measure(static x => x.Opacity(0.9f))).IsBetween(0L, baseline);
        await Assert.That(Measure(static x => x.Fill(new Rgba32(1, 2, 3, 255)))).IsBetween(0L, baseline);
        await Assert.That(Measure(static x => x.MakeTransparent(new Rgba32(1, 2, 3, 4)))).IsBetween(0L, baseline);
        await Assert.That(Measure(static x => x.BackgroundColor(new Rgba32(5, 6, 7, 255)))).IsBetween(0L, baseline);
    }
}
