using System.Drawing;

namespace PictureSharp.Tests.Core;

public class MutateCloneTests
{
    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan().ToArray();

    private static Image Animation()
    {
        Image a = ImageTests.Pattern(8, 6);
        using Image b = a.Clone(x => x.Invert());
        a.Frames.Add(b, TimeSpan.FromMilliseconds(20));
        return a;
    }

    [Fact]
    public void Clone_LeavesSourceUntouched_ForEveryKindOfOperation()
    {
        using Image source = Animation();
        Rgba32[][] before = source.Frames.Select(f => f.GetPixelSpan().ToArray()).ToArray();
        using Image badge = ImageTests.Pattern(2, 2);
        Action<ImageProcessingContext>[] operations =
        [
            x => x.Grayscale(),
            x => x.Invert(),
            x => x.Fill(Color.Red),
            x => x.Flip(FlipMode.Horizontal),
            x => x.Rotate(RotateMode.Rotate180),
            x => x.Rotate(RotateMode.Rotate90),
            x => x.Resize(3, 2),
            x => x.Crop(1, 1, 3, 3),
            x => x.Pad(10, 10, Color.Blue),
            x => x.DrawImage(badge, new Point(1, 1)),
            x => x.Opacity(0.5f).BackgroundColor(Color.White),
            x => x.MakeTransparent(source[0, 0]),
            x => x.Apply(f => f.GetPixelSpan().Clear()),
            x => x.Resize(4, 3).Grayscale(),
            x => { },
        ];

        foreach (Action<ImageProcessingContext> operation in operations)
        {
            using Image clone = source.Clone(operation);
            Assert.Equal(2, clone.FrameCount);
            Assert.Equal(new Size(8, 6), source.Size);
            Assert.Equal(before, source.Frames.Select(f => f.GetPixelSpan().ToArray()).ToArray());
            Assert.NotSame(source.Frames[0], clone.Frames[0]);
        }
    }

    [Fact]
    public void Clone_GivesTheSamePixelsAsMutateOnACopy()
    {
        using Image source = Animation();
        using Image badge = ImageTests.Pattern(3, 2);
        Action<ImageProcessingContext> pipeline = x => x
            .Resize(16, 0, ResamplingFilter.Bicubic)
            .Crop(12, 10, AnchorPosition.Bottom)
            .Rotate(RotateMode.Rotate270)
            .DrawImage(badge, AnchorPosition.BottomRight, 0.7f)
            .Grayscale();

        using Image cloned = source.Clone(pipeline);
        using Image mutated = source.Clone();
        mutated.Mutate(pipeline);
        Assert.Equal(mutated.Size, cloned.Size);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(mutated.Frames[i].GetPixelSpan().ToArray(), cloned.Frames[i].GetPixelSpan().ToArray());
        }
    }

    [Fact]
    public void Clone_WithoutInPlaceOperations_StillOwnsItsPixels()
    {
        using Image source = ImageTests.Pattern(4, 4);
        using Image clone = source.Clone(x => { });
        clone[0, 0] = Rgba32.White;
        Assert.NotEqual(Rgba32.White, source[0, 0]);
    }

    [Fact]
    public void Clone_CopiesMetadata_AndDisposesOnFailure()
    {
        using Image source = ImageTests.Pattern(4, 4);
        source.Metadata.Xmp = [1, 2];
        using Image clone = source.Clone(x => x.Invert());
        clone.Metadata.Xmp![0] = 9;
        Assert.Equal(1, source.Metadata.Xmp[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Clone(x => x.Crop(0, 0, 5, 5)));
    }

    [Fact]
    public void Context_ReportsCurrentSize_AndChainRunsInOrder()
    {
        using Image img = ImageTests.Pattern(10, 4);
        var sizes = new List<Size>();
        img.Mutate(x =>
        {
            sizes.Add(x.Size);
            x.Resize(5, 0);
            sizes.Add(x.Size);
            x.Rotate(RotateMode.Rotate90);
            sizes.Add(x.Size);
            Assert.Equal(1, x.FrameCount);
        });
        Assert.Equal([new Size(10, 4), new Size(5, 2), new Size(2, 5)], sizes);
    }

    [Fact]
    public void CustomOperation_CanBeAnExtensionMember()
    {
        using Image img = new(2, 2, Color.Black);
        img.Mutate(x => x.Posterize());
        Assert.Equal(new Rgba32(1, 1, 1), img[1, 1]);
    }

    [Fact]
    public void Resize_ZeroSide_KeepsAspectRatio()
    {
        using Image img = ProcessingOracleTests.Noise(120, 80, 1);
        using Image byWidth = img.Clone(x => x.Resize(60, 0));
        using Image byHeight = img.Clone(x => x.Resize(new Size(0, 20)));
        Assert.Equal(new Size(60, 40), byWidth.Size);
        Assert.Equal(new Size(30, 20), byHeight.Size);
    }

    [Theory]
    [InlineData(ResizeMode.Stretch, 200, 50, 100, 50)]
    [InlineData(ResizeMode.Max, 200, 200, 100, 50)]
    [InlineData(ResizeMode.Min, 300, 300, 100, 50)]
    [InlineData(ResizeMode.Crop, 300, 20, 100, 20)]
    [InlineData(ResizeMode.Pad, 300, 300, 300, 300)]
    [InlineData(ResizeMode.Max, 50, 50, 50, 25)]
    public void Resize_WithoutUpscale_NeverEnlarges(ResizeMode mode, int w, int h, int ew, int eh)
    {
        using Image img = ProcessingOracleTests.Noise(100, 50, 2);
        using Image result = img.Clone(x => x.Resize(new ResizeOptions { Size = new(w, h), Mode = mode, AllowUpscale = false }));
        Assert.Equal(new Size(ew, eh), result.Size);
    }

    [Fact]
    public void Resize_CropWithoutUpscale_CropsAtAnchor_WithoutResampling()
    {
        using Image img = ImageTests.Pattern(10, 4);
        using Image result = img.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new(6, 8), Mode = ResizeMode.Crop, Position = AnchorPosition.Right, AllowUpscale = false,
        }));
        Assert.Equal(new Size(6, 4), result.Size);
        Assert.Equal(img[4, 0], result[0, 0]);
        Assert.Equal(img[9, 3], result[5, 3]);
    }

    [Fact]
    public void Resize_PadWithoutUpscale_CentersOriginal()
    {
        using Image img = new(4, 2, Color.Red);
        using Image result = img.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new(8, 6), Mode = ResizeMode.Pad, PadColor = Color.White, AllowUpscale = false,
        }));
        Assert.Equal(new Size(8, 6), result.Size);
        Assert.Equal(Rgba32.White, result[1, 2]);
        Assert.Equal(new Rgba32(255, 0, 0), result[2, 2]);
        Assert.Equal(new Rgba32(255, 0, 0), result[5, 3]);
        Assert.Equal(Rgba32.White, result[6, 3]);
        Assert.False(result.HasAlpha);
    }

    [Theory]
    [InlineData(AnchorPosition.TopLeft, 0, 0)]
    [InlineData(AnchorPosition.Center, 3, 2)]
    [InlineData(AnchorPosition.BottomRight, 6, 4)]
    [InlineData(AnchorPosition.Right, 6, 2)]
    public void Crop_WithAnchor_TakesTheRightPart(AnchorPosition position, int x0, int y0)
    {
        using Image img = ImageTests.Pattern(10, 7);
        using Image part = img.Clone(x => x.Crop(4, 3, position));
        Assert.Equal(new Size(4, 3), part.Size);
        Assert.Equal(img[x0, y0], part[0, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Crop(11, 3)));
    }

    [Fact]
    public void Pad_ExtendsCanvas_AndCanCut()
    {
        using Image img = new(2, 2, Color.Red);
        img.Mutate(x => x.Pad(4, 3, Color.Transparent, AnchorPosition.TopLeft));
        Assert.Equal(new Size(4, 3), img.Size);
        Assert.True(img.HasAlpha);
        Assert.Equal(new Rgba32(255, 0, 0), img[1, 1]);
        Assert.Equal(Rgba32.FromColor(Color.Transparent), img[2, 0]);

        using Image smaller = ImageTests.Pattern(6, 6);
        using Image cut = smaller.Clone(x => x.Pad(2, 2));
        Assert.Equal(smaller[2, 2], cut[0, 0]);
    }

    [Fact]
    public void BackgroundColor_FlattensAlpha()
    {
        using Image img = new(2, 1);
        img[0, 0] = new Rgba32(255, 0, 0, 0);
        img[1, 0] = new Rgba32(0, 0, 255, 255);
        img.Mutate(x => x.BackgroundColor(Color.White));
        Assert.False(img.HasAlpha);
        Assert.Equal(Rgba32.White, img[0, 0]);
        Assert.Equal(new Rgba32(0, 0, 255), img[1, 0]);
        Assert.True(img.IsOpaque());
    }

    [Fact]
    public void Opacity_ScalesAlpha()
    {
        using Image img = new(1, 1, Color.Red);
        img.Mutate(x => x.Opacity(0.5f));
        Assert.Equal(new Rgba32(255, 0, 0, 128), img[0, 0]);
        Assert.True(img.HasAlpha);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Opacity(-0.1f)));
    }

    [Fact]
    public void DrawImage_AtAnchor_PlacesInCorner()
    {
        using Image dst = new(5, 4, Color.White);
        using Image badge = new(2, 1, Color.Red);
        dst.Mutate(x => x.DrawImage(badge, AnchorPosition.BottomRight));
        Assert.Equal(new Rgba32(255, 0, 0), dst[3, 3]);
        Assert.Equal(new Rgba32(255, 0, 0), dst[4, 3]);
        Assert.Equal(Rgba32.White, dst[2, 3]);
        Assert.Equal(Rgba32.White, dst[4, 2]);
    }

    [Fact]
    public void Rgba32_ParsesHexAndNames()
    {
        Assert.Equal(new Rgba32(0x11, 0x22, 0x33), Rgba32.Parse("#123"));
        Assert.Equal(new Rgba32(0x11, 0x22, 0x33, 0x44), Rgba32.Parse("1234"));
        Assert.Equal(new Rgba32(0xAB, 0xCD, 0xEF), Rgba32.Parse("#abcdef"));
        Assert.Equal(new Rgba32(1, 2, 3, 4), Rgba32.Parse(" #01020304 "));
        Assert.Equal(Rgba32.FromColor(Color.CornflowerBlue), Rgba32.Parse("cornflowerblue"));
        Assert.Equal("#01020304", new Rgba32(1, 2, 3, 4).ToHex());
        Assert.True(Rgba32.TryParse(new Rgba32(9, 8, 7, 6).ToHex(), out Rgba32 back));
        Assert.Equal(new Rgba32(9, 8, 7, 6), back);
        Assert.False(Rgba32.TryParse("#12345", out _));
        Assert.False(Rgba32.TryParse("#red", out _));
        Assert.False(Rgba32.TryParse("NoSuchColor", out _));
        Assert.False(Rgba32.TryParse((string?)null, out _));
        Assert.Throws<FormatException>(() => Rgba32.Parse("xyz"));
        (byte r, byte g, byte b, byte a) = new Rgba32(1, 2, 3, 4);
        Assert.Equal((1, 2, 3, 4), (r, g, b, a));
    }

    [Fact]
    public void Options_AreRecords_ValidatedWhenSet()
    {
        var options = new WebPEncoderOptions { Quality = 80 };
        WebPEncoderOptions lossless = options with { Lossless = true };
        Assert.Equal(80, lossless.Quality);
        Assert.True(lossless.Lossless);
        Assert.Equal(options, new WebPEncoderOptions { Quality = 80 });
        Assert.Throws<ArgumentOutOfRangeException>(() => options with { Quality = 101 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngEncoderOptions { CompressionLevel = 10 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BmpEncoderOptions { BitsPerPixel = 16 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxPixels = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageMetadata { HorizontalResolution = float.NaN });
    }

    [Fact]
    public void LoadAndSave_ThroughEveryEntryPoint()
    {
        using Image img = ImageTests.Pattern(5, 4);
        byte[] png = img.ToPng();
        using Image fromArray = Image.Load(png);
        using Image fromSpan = Image.Load(png.AsSpan());
        using Image fromStream = Image.Load(new MemoryStream(png));
        Assert.Equal(ImageFormat.Png, fromArray.SourceFormat);
        Assert.Equal(Pixels(fromArray), Pixels(fromSpan));
        Assert.Equal(Pixels(fromArray), Pixels(fromStream));
        Assert.Equal(ImageFormat.Png, Image.DetectFormat(png));

        using var stream = new MemoryStream();
        img.SaveAsWebP(stream, new() { Lossless = true, Exact = true });
        stream.Position = 0;
        Assert.Equal(ImageFormat.WebP, Image.DetectFormat(stream));
        Assert.Equal(0, stream.Position);
        using Image webp = Image.Load(stream);
        Assert.Equal(Pixels(img), Pixels(webp));
    }

    [Fact]
    public async Task AsyncEntryPoints_RoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"picturesharp-async-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using Image img = ImageTests.Pattern(6, 3);
            string png = Path.Combine(dir, "a.png");
            string webp = Path.Combine(dir, "a.webp");
            await img.SaveAsync(png);
            await img.SaveAsWebPAsync(webp, new() { Lossless = true, Exact = true });
            Assert.Equal(ImageFormat.Png, Image.DetectFormat(png));
            ImageInfo info = await Image.IdentifyAsync(webp);
            Assert.Equal((6, 3), (info.Width, info.Height));
            using Image back = await Image.LoadAsync(webp);
            Assert.Equal(Pixels(img), Pixels(back));
            using Image limited = await Image.LoadAsync(png, new DecoderOptions { FirstFrameOnly = true });
            Assert.Equal(Pixels(img), Pixels(limited));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

internal static class CustomOperations
{
    extension(ImageProcessingContext x)
    {
        public ImageProcessingContext Posterize() => x.Apply(frame =>
        {
            foreach (ref Rgba32 p in frame.GetPixelSpan())
            {
                p = new Rgba32((byte)(p.R | 1), (byte)(p.G | 1), (byte)(p.B | 1), p.A);
            }
        });
    }
}
