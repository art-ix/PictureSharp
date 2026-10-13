using System.Runtime.InteropServices;
using RasterSharp.Tests.Infrastructure;

namespace RasterSharp.Tests.Gif;

public class GifEncoderTests
{
    public static TheoryData<string> GifFiles => Corpus.Files("gif/");

    private static void AssertSameFrames(Image expected, Image actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.FrameCount, actual.FrameCount);
        for (int i = 0; i < expected.FrameCount; i++)
        {
            Assert.True(expected.Frames[i].GetPixelSpan().SequenceEqual(actual.Frames[i].GetPixelSpan()), $"frame {i} differs");
            Assert.Equal(expected.Frames[i].Duration, actual.Frames[i].Duration);
        }
    }

    /// <summary>What a GIF can store: alpha below the threshold becomes (0, 0, 0, 0), everything else opaque.</summary>
    internal static Rgba32[] Binarize(ReadOnlySpan<Rgba32> pixels, int threshold = 128)
    {
        var result = new Rgba32[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            result[i] = pixels[i].A < threshold ? default : pixels[i] with { A = 255 };
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(GifFiles))]
    public void RoundTrip_CorpusGif_IsLossless(string file)
    {
        using Image original = Image.Load(Corpus.PathOf(file));
        foreach (GifEncoderOptions options in new GifEncoderOptions[] { new(), new() { OptimizeFrames = false, Interlace = true } })
        {
            using Image back = Image.Load(original.ToGif(options));
            AssertSameFrames(original, back);
            Assert.Equal(original.LoopCount, back.LoopCount);
            Assert.Equal(original.Metadata.Xmp, back.Metadata.Xmp);
            Assert.Equal(original.Metadata.IccProfile, back.Metadata.IccProfile);
        }
    }

    public static TheoryData<string> QuantizeKeys() => QuantizationTests.Keys();

    [Theory]
    [MemberData(nameof(QuantizeKeys))]
    public void Encode_StillImage_MatchesPillowQuantize(string key)
    {
        string[] parts = key.Split('/');
        int colors = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        QuantizationTests.LoadOpaque(parts[0], out Rgba32[] pixels, out int width, out int height);
        using Image image = Image.LoadPixelData(pixels, width, height);
        var options = new GifEncoderOptions
        {
            ColorCount = colors,
            Quantizer = parts[1] == "octree" ? GifQuantizer.Octree : GifQuantizer.MedianCut,
            Dither = parts[1] == "dither" ? GifDither.FloydSteinberg : GifDither.None,
        };
        using Image decoded = Image.Load(image.ToGif(options));
        if (pixels.Distinct().Count() <= colors)
        {
            // Unlike Image.quantize, the encoder keeps the exact colors when they fit.
            Assert.Equal(pixels, decoded.GetPixelSpan().ToArray());
            return;
        }

        Assert.Equal(QuantizationTests.References.Value[key].RgbaSha256, Corpus.Sha256(MemoryMarshal.AsBytes(decoded.GetPixelSpan())));
    }

    [Theory]
    [InlineData("alpha_lossless")]
    [InlineData("alpha_mixed")]
    [InlineData("alpha_pillow")]
    public void WebPAnimation_ToGif_KeepsPixels(string name)
    {
        using Image webp = Image.Load(Corpus.PathOf($"webp/anim/{name}.webp"));
        using Image gif = Image.Load(webp.ToGif());
        Assert.Equal(webp.FrameCount, gif.FrameCount);
        Assert.Equal(webp.LoopCount, gif.LoopCount);
        for (int i = 0; i < webp.FrameCount; i++)
        {
            Assert.Equal(Binarize(webp.Frames[i].GetPixelSpan()), gif.Frames[i].GetPixelSpan().ToArray());
            Assert.Equal(webp.Frames[i].Duration, gif.Frames[i].Duration);
        }
    }

    [Theory]
    [InlineData("opaque_lossless")]
    [InlineData("opaque_lossy")]
    [InlineData("alpha_lossy")]
    public void WebPAnimation_WithManyColors_IsQuantized(string name)
    {
        using Image webp = Image.Load(Corpus.PathOf($"webp/anim/{name}.webp"));
        using Image gif = Image.Load(webp.ToGif());
        Assert.Equal(webp.FrameCount, gif.FrameCount);
        Assert.Equal(webp.LoopCount, gif.LoopCount);
        double error = 0;
        for (int i = 0; i < webp.FrameCount; i++)
        {
            var colors = new HashSet<Rgba32>();
            Rgba32[] expected = Binarize(webp.Frames[i].GetPixelSpan());
            ReadOnlySpan<Rgba32> actual = gif.Frames[i].GetPixelSpan();
            for (int p = 0; p < expected.Length; p++)
            {
                Assert.Equal(expected[p].A, actual[p].A);
                error += Math.Abs(expected[p].R - actual[p].R) + Math.Abs(expected[p].G - actual[p].G) + Math.Abs(expected[p].B - actual[p].B);
                colors.Add(actual[p]);
            }

            Assert.True(colors.Count <= 256);
        }

        Assert.True(error / (webp.FrameCount * webp.Width * webp.Height * 3) < 6, $"mean error {error / (webp.FrameCount * webp.Width * webp.Height * 3)}");
    }

    [Fact]
    public void RandomAnimations_WithTransparency_RoundTrip()
    {
        var random = new Random(42);
        Rgba32[] colors = [default, new(10, 20, 30), new(200, 0, 0), new(0, 200, 0), new(0, 0, 200), new(255, 255, 255)];
        for (int round = 0; round < 40; round++)
        {
            int width = random.Next(1, 30), height = random.Next(1, 30);
            var frames = new List<Image>();
            using var current = new Image(width, height);
            for (int f = 0; f < random.Next(2, 8); f++)
            {
                // Random rectangles of random colors, including transparent ones, on top of the previous frame.
                for (int r = random.Next(0, 4); r > 0; r--)
                {
                    int x0 = random.Next(width), y0 = random.Next(height);
                    int x1 = random.Next(x0, width) + 1, y1 = random.Next(y0, height) + 1;
                    Rgba32 color = colors[random.Next(colors.Length)];
                    for (int y = y0; y < y1; y++)
                    {
                        current.GetRowSpan(y)[x0..x1].Fill(color);
                    }
                }

                frames.Add(current.Clone());
            }

            using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(30), frames);
            frames.ForEach(f => f.Dispose());
            animation.LoopCount = random.Next(3);
            foreach (bool optimize in new[] { true, false })
            {
                using Image back = Image.Load(animation.ToGif(new GifEncoderOptions { OptimizeFrames = optimize, Interlace = round % 2 == 0 }));
                Assert.Equal(animation.LoopCount, back.LoopCount);

                // Identical consecutive frames are merged into one longer frame when optimizing.
                int expectedFrame = 0;
                TimeSpan elapsed = TimeSpan.Zero;
                for (int i = 0; i < animation.FrameCount; i++)
                {
                    if (i > 0 && optimize && animation.Frames[i].GetPixelSpan().SequenceEqual(animation.Frames[i - 1].GetPixelSpan()))
                    {
                        continue;
                    }

                    Assert.True(animation.Frames[i].GetPixelSpan().SequenceEqual(back.Frames[expectedFrame].GetPixelSpan()), $"round {round}, frame {i}");
                    expectedFrame++;
                }

                Assert.Equal(expectedFrame, back.FrameCount);
                Assert.Equal(TimeSpan.FromMilliseconds(30 * animation.FrameCount), back.Frames.Aggregate(elapsed, (t, f) => t + f.Duration));
            }
        }
    }

    [Fact]
    public void IdenticalFrames_AreMergedWhenOptimizing()
    {
        using var a = new Image(4, 4, new Rgba32(1, 2, 3));
        using var b = new Image(4, 4, new Rgba32(4, 5, 6));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(10), a, a, b, b, b);
        animation.Frames[1].Duration = TimeSpan.FromMilliseconds(20);
        using Image merged = Image.Load(animation.ToGif());
        Assert.Equal(new[] { 30.0, 30.0 }, merged.Frames.Select(f => f.Duration.TotalMilliseconds));
        using Image kept = Image.Load(animation.ToGif(new GifEncoderOptions { OptimizeFrames = false }));
        Assert.Equal(5, kept.FrameCount);
        using Image still = Image.Load(animation.ToGif(new GifEncoderOptions { SaveAnimation = false }));
        Assert.Equal(1, still.FrameCount);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 10)]
    [InlineData(15, 20)]
    [InlineData(1234, 1230)]
    [InlineData(10_000_000, 655_350)]
    public void FrameDurations_AreRoundedToCentiseconds(int milliseconds, int expected)
    {
        using var a = new Image(2, 2, new Rgba32(1, 2, 3));
        using var b = new Image(2, 2, new Rgba32(4, 5, 6));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(milliseconds), a, b);
        using Image back = Image.Load(animation.ToGif());
        Assert.All(back.Frames, f => Assert.Equal(expected, f.Duration.TotalMilliseconds));
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, true, 1)]
    [InlineData(2, false, 2)]
    [InlineData(65535, false, 65535)]
    public void LoopCount_RoundTrips(int loops, bool noExtension, int expected)
    {
        using var a = new Image(2, 2, new Rgba32(1, 2, 3));
        using var b = new Image(2, 2, new Rgba32(4, 5, 6));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), a, b);
        animation.LoopCount = loops;
        byte[] gif = animation.ToGif();
        Assert.Equal(noExtension, gif.AsSpan().IndexOf("NETSCAPE2.0"u8) < 0);
        using Image back = Image.Load(gif);
        Assert.Equal(expected, back.LoopCount);
    }

    [Theory]
    [InlineData(128, 100, true)]
    [InlineData(100, 100, false)]
    [InlineData(0, 0, false)]
    [InlineData(255, 254, true)]
    public void TransparencyThreshold_DecidesTransparentPixels(int threshold, byte alpha, bool transparent)
    {
        using var image = new Image(3, 1, new Rgba32(9, 8, 7, alpha));
        image[0, 0] = new Rgba32(1, 2, 3);
        using Image back = Image.Load(image.ToGif(new GifEncoderOptions { TransparencyThreshold = threshold }));
        Assert.Equal(transparent ? default : new Rgba32(9, 8, 7), back[2, 0]);
        Assert.Equal(new Rgba32(1, 2, 3), back[0, 0]);
        Assert.Equal(transparent, back.HasAlpha);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(128)]
    public void ColorCount_LimitsThePalette(int count)
    {
        using Image photo = Image.Load(Corpus.PathOf("src/photo_alpha.png"));
        foreach (GifQuantizer quantizer in Enum.GetValues<GifQuantizer>())
        {
            using Image back = Image.Load(photo.ToGif(new GifEncoderOptions { ColorCount = count, Quantizer = quantizer, Dither = GifDither.FloydSteinberg }));
            Assert.True(back.GetPixelSpan().ToArray().Distinct().Count() <= count);
            Assert.Equal(Binarize(photo.GetPixelSpan()).Select(p => p.A), back.GetPixelSpan().ToArray().Select(p => p.A));
        }
    }

    [Fact]
    public void Header_Version_And_Interlace()
    {
        using var plain = new Image(20, 20, new Rgba32(1, 2, 3));
        Assert.True(plain.ToGif().AsSpan().StartsWith("GIF87a"u8));
        using var transparent = new Image(20, 20);
        Assert.True(transparent.ToGif().AsSpan().StartsWith("GIF89a"u8));

        for (int height = 1; height <= 17; height++)
        {
            using var image = new Image(3, height);
            for (int y = 0; y < height; y++)
            {
                image.GetRowSpan(y).Fill(new Rgba32((byte)(y * 15), 0, 0));
            }

            using Image back = Image.Load(image.ToGif(new GifEncoderOptions { Interlace = true }));
            Assert.Equal(image.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
        }
    }

    [Fact]
    public void Metadata_IsWrittenAsApplicationExtensions()
    {
        using Image image = new(4, 4, new Rgba32(1, 2, 3));
        image.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        image.Metadata.IccProfile = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        image.Metadata.Exif = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0];
        using Image back = Image.Load(image.ToGif());
        Assert.Equal(image.Metadata.Xmp, back.Metadata.Xmp);
        Assert.Equal(image.Metadata.IccProfile, back.Metadata.IccProfile);
        Assert.Null(back.Metadata.Exif);

        using Image none = Image.Load(image.ToGif(new GifEncoderOptions { Metadata = MetadataKinds.None }));
        Assert.True(none.Metadata.IsEmpty);
    }

    [Fact]
    public void AnimationBackgroundColor_RoundTrips()
    {
        using var a = new Image(2, 2, new Rgba32(1, 2, 3));
        using var b = new Image(2, 2, new Rgba32(4, 5, 6));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), a, b);
        animation.AnimationBackgroundColor = new Rgba32(4, 5, 6);
        using Image back = Image.Load(animation.ToGif());
        Assert.Equal(new Rgba32(4, 5, 6), back.AnimationBackgroundColor);

        using var c = new Image(2, 2);
        using Image transparent = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), a, c);
        using Image back2 = Image.Load(transparent.ToGif());
        Assert.Equal(default, back2.AnimationBackgroundColor);
    }

    [Fact]
    public async Task SaveApis_WriteGif()
    {
        using var image = new Image(5, 5, new Rgba32(10, 20, 30));
        string path = Path.Combine(Path.GetTempPath(), $"rastersharp-{Guid.NewGuid():N}.gif");
        try
        {
            image.Save(path);
            Assert.Equal(ImageFormat.Gif, Image.DetectFormat(path));
            await image.SaveAsGifAsync(path, new GifEncoderOptions { Interlace = true });
            using Image loaded = await Image.LoadAsync(path);
            Assert.Equal(new Rgba32(10, 20, 30), loaded[4, 4]);
        }
        finally
        {
            File.Delete(path);
        }

        using var stream = new NonSeekableStream();
        image.SaveAsGif(stream);
        image.Save(stream, ImageFormat.Gif);
        Assert.True(stream.Length > 0);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public void Options_AreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoderOptions { ColorCount = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoderOptions { ColorCount = 257 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoderOptions { TransparencyThreshold = 256 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoderOptions { Quantizer = (GifQuantizer)9 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoderOptions { Dither = (GifDither)9 });
        Assert.Equal(ImageFormat.Gif, new GifEncoderOptions().Format);
        using var tooWide = new Image(65536, 1);
        Assert.Throws<NotSupportedException>(() => tooWide.ToGif());
    }
}
