using System.Drawing;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

public class MutateCloneTests
{
    private static Rgba32[] Pixels(Image img) => img.GetPixelSpan<Rgba32>().ToArray();

    private static Image Animation()
    {
        Image a = ImageTests.Pattern(8, 6);
        using Image b = a.Clone(x => x.Invert());
        a.Frames.Add(b, TimeSpan.FromMilliseconds(20));
        return a;
    }

    [Test]
    public async Task Clone_LeavesSourceUntouched_ForEveryKindOfOperation()
    {
        using Image source = Animation();
        Rgba32[][] before = source.Frames.Select(f => f.GetPixelSpan<Rgba32>().ToArray()).ToArray();
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
            x => x.Apply(f => f.GetPixelSpan<Rgba32>().Clear()),
            x => x.Resize(4, 3).Grayscale(),
            x => x.ApplyProcessor(new Extensibility.TransposeProcessor()),
            x => { },
        ];

        foreach (Action<ImageProcessingContext> operation in operations)
        {
            using Image clone = source.Clone(operation);
            await Assert.That(clone.FrameCount).IsEqualTo(2);
            await Assert.That(source.Size).IsEqualTo(new Size(8, 6));
            await Assert.That(source.FrameCount).IsEqualTo(before.Length);
            for (int i = 0; i < before.Length; i++)
            {
                await Assert.That(source.Frames[i].GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(before[i], CollectionOrdering.Matching);
            }

            await Assert.That(clone.Frames[0]).IsNotSameReferenceAs(source.Frames[0]);
        }
    }

    [Test]
    public async Task Clone_GivesTheSamePixelsAsMutateOnACopy()
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
        await Assert.That(cloned.Size).IsEqualTo(mutated.Size);
        for (int i = 0; i < 2; i++)
        {
            await Assert.That(cloned.Frames[i].GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(mutated.Frames[i].GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Clone_WithoutInPlaceOperations_StillOwnsItsPixels()
    {
        using Image source = ImageTests.Pattern(4, 4);
        using Image clone = source.Clone(x => { });
        clone[0, 0] = Rgba32.White;
        await Assert.That(source[0, 0]).IsNotEqualTo(Rgba32.White);
    }

    [Test]
    public async Task Clone_CopiesMetadata_AndDisposesOnFailure()
    {
        using Image source = ImageTests.Pattern(4, 4);
        source.Metadata.Xmp = [1, 2];
        source.Metadata.HorizontalResolution = 300;
        using Image clone = source.Clone(x => x.Resize(3, 2));
        clone.Metadata.Xmp![0] = 9;
        await Assert.That((int)(source.Metadata.Xmp[0])).IsEqualTo(1);
        await Assert.That(clone.Metadata.HorizontalResolution).IsEqualTo(300f);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => source.Clone(x => x.Crop(0, 0, 5, 5)));
    }

    [Test]
    public async Task Context_ReportsCurrentSize_AndChainRunsInOrder()
    {
        using Image img = ImageTests.Pattern(10, 4);
        var sizes = new List<Size>();
        int frames = 0;
        img.Mutate(x =>
        {
            sizes.Add(x.Size);
            x.Resize(5, 0);
            sizes.Add(x.Size);
            x.Rotate(RotateMode.Rotate90);
            sizes.Add(x.Size);
            frames = x.FrameCount;
        });
        await Assert.That(frames).IsEqualTo(1);
        await Assert.That(sizes).IsEquivalentTo([new Size(10, 4), new Size(5, 2), new Size(2, 5)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CustomOperation_CanBeAnExtensionMember()
    {
        using Image img = new(2, 2, Color.Black);
        img.Mutate(x => x.Posterize());
        await Assert.That(img[1, 1]).IsEqualTo(new Rgba32(1, 1, 1));
    }

    [Test]
    [Arguments(ResizeMode.Stretch, 200, 50, 100, 50)]
    [Arguments(ResizeMode.Max, 200, 200, 100, 50)]
    [Arguments(ResizeMode.Min, 300, 300, 100, 50)]
    [Arguments(ResizeMode.Crop, 300, 20, 100, 20)]
    [Arguments(ResizeMode.Pad, 300, 300, 300, 300)]
    [Arguments(ResizeMode.Max, 50, 50, 50, 25)]
    public async Task Resize_WithoutUpscale_NeverEnlarges(ResizeMode mode, int w, int h, int ew, int eh)
    {
        using Image img = ProcessingOracleTests.Noise(100, 50, 2);
        using Image result = img.Clone(x => x.Resize(new ResizeOptions { Size = new(w, h), Mode = mode, AllowUpscale = false }));
        await Assert.That(result.Size).IsEqualTo(new Size(ew, eh));
    }

    [Test]
    [Arguments(1, 60000, 40000, 40000)]
    [Arguments(60000, 1, 40000, 40000)]
    [Arguments(1, 2, 40000, 40000)]
    public async Task Resize_MinCoveringTooLargeCanvas_ThrowsArgumentOutOfRange(int srcWidth, int srcHeight, int w, int h)
    {
        using var img = new Image(srcWidth, srcHeight);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Resize(new ResizeOptions { Size = new(w, h), Mode = ResizeMode.Min })));
        await Assert.That(img.Size).IsEqualTo(new Size(srcWidth, srcHeight));
    }

    [Test]
    [Arguments(1, 60000, 40000, 40000)]
    [Arguments(60000, 1, 40000, 40000)]
    public async Task Resize_MinCoveringTooLargeCanvas_WithoutUpscale_LeavesImageUnchanged(int srcWidth, int srcHeight, int w, int h)
    {
        using var img = new Image(srcWidth, srcHeight);
        img.Mutate(x => x.Resize(new ResizeOptions { Size = new(w, h), Mode = ResizeMode.Min, AllowUpscale = false }));
        await Assert.That(img.Size).IsEqualTo(new Size(srcWidth, srcHeight));
    }

    [Test]
    public async Task Resize_CropWithoutUpscale_CropsAtAnchor_WithoutResampling()
    {
        using Image img = ImageTests.Pattern(10, 4);
        using Image result = img.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new(6, 8),
            Mode = ResizeMode.Crop,
            Position = AnchorPosition.Right,
            AllowUpscale = false,
        }));
        await Assert.That(result.Size).IsEqualTo(new Size(6, 4));
        await Assert.That(result[0, 0]).IsEqualTo(img[4, 0]);
        await Assert.That(result[5, 3]).IsEqualTo(img[9, 3]);
    }

    [Test]
    public async Task Resize_PadWithoutUpscale_CentersOriginal()
    {
        using Image img = new(4, 2, Color.Red);
        using Image result = img.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new(8, 6),
            Mode = ResizeMode.Pad,
            PadColor = Color.White,
            AllowUpscale = false,
        }));
        await Assert.That(result.Size).IsEqualTo(new Size(8, 6));
        await Assert.That(result[1, 2]).IsEqualTo(Rgba32.White);
        await Assert.That(result[2, 2]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(result[5, 3]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(result[6, 3]).IsEqualTo(Rgba32.White);
        await Assert.That(result.HasAlpha).IsFalse();
    }

    [Test]
    [Arguments(AnchorPosition.TopLeft, 0, 0)]
    [Arguments(AnchorPosition.Center, 3, 2)]
    [Arguments(AnchorPosition.BottomRight, 6, 4)]
    [Arguments(AnchorPosition.Right, 6, 2)]
    public async Task Crop_WithAnchor_TakesTheRightPart(AnchorPosition position, int x0, int y0)
    {
        using Image img = ImageTests.Pattern(10, 7);
        using Image part = img.Clone(x => x.Crop(4, 3, position));
        await Assert.That(part.Size).IsEqualTo(new Size(4, 3));
        await Assert.That(part[0, 0]).IsEqualTo(img[x0, y0]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Crop(11, 3)));
    }

    [Test]
    public async Task Pad_ExtendsCanvas_AndCanCut()
    {
        using Image img = new(2, 2, Color.Red);
        img.Mutate(x => x.Pad(4, 3, Color.Transparent, AnchorPosition.TopLeft));
        await Assert.That(img.Size).IsEqualTo(new Size(4, 3));
        await Assert.That(img.HasAlpha).IsTrue();
        await Assert.That(img[1, 1]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(img[2, 0]).IsEqualTo(Rgba32.FromColor(Color.Transparent));

        using Image smaller = ImageTests.Pattern(6, 6);
        using Image cut = smaller.Clone(x => x.Pad(2, 2));
        await Assert.That(cut[0, 0]).IsEqualTo(smaller[2, 2]);
    }

    [Test]
    public async Task BackgroundColor_FlattensAlpha()
    {
        using Image img = new(2, 1);
        img[0, 0] = new Rgba32(255, 0, 0, 0);
        img[1, 0] = new Rgba32(0, 0, 255, 255);
        img.Mutate(x => x.BackgroundColor(Color.White));
        await Assert.That(img.HasAlpha).IsFalse();
        await Assert.That(img[0, 0]).IsEqualTo(Rgba32.White);
        await Assert.That(img[1, 0]).IsEqualTo(new Rgba32(0, 0, 255));
        await Assert.That(img.IsOpaque()).IsTrue();
    }

    [Test]
    public async Task Opacity_ScalesAlpha()
    {
        using Image img = new(1, 1, Color.Red);
        img.Mutate(x => x.Opacity(0.5f));
        await Assert.That(img[0, 0]).IsEqualTo(new Rgba32(255, 0, 0, 128));
        await Assert.That(img.HasAlpha).IsTrue();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => img.Mutate(x => x.Opacity(-0.1f)));
    }

    [Test]
    public async Task DrawImage_AtAnchor_PlacesInCorner()
    {
        using Image dst = new(5, 4, Color.White);
        using Image badge = new(2, 1, Color.Red);
        dst.Mutate(x => x.DrawImage(badge, AnchorPosition.BottomRight));
        await Assert.That(dst[3, 3]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(dst[4, 3]).IsEqualTo(new Rgba32(255, 0, 0));
        await Assert.That(dst[2, 3]).IsEqualTo(Rgba32.White);
        await Assert.That(dst[4, 2]).IsEqualTo(Rgba32.White);
    }

    [Test]
    public async Task Options_AreRecords_ValidatedWhenSet()
    {
        var options = new WebPEncoderOptions { Quality = 80 };
        WebPEncoderOptions lossless = options with { Lossless = true };
        await Assert.That(lossless.Quality).IsEqualTo(80);
        await Assert.That(lossless.Lossless).IsTrue();
        await Assert.That(new WebPEncoderOptions { Quality = 80 }).IsEqualTo(options);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = options with { Quality = 101 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PngEncoderOptions { CompressionLevel = 10 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BmpEncoderOptions { BitsPerPixel = 16 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxPixels = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ImageMetadata { HorizontalResolution = float.NaN });
    }
}

internal static class CustomOperations
{
    extension(ImageProcessingContext x)
    {
        public ImageProcessingContext Posterize() => x.Apply(frame =>
        {
            foreach (ref Rgba32 p in frame.GetPixelSpan<Rgba32>())
            {
                p = new Rgba32((byte)(p.R | 1), (byte)(p.G | 1), (byte)(p.B | 1), p.A);
            }
        });
    }
}
