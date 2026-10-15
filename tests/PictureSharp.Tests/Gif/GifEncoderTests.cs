using System.Runtime.InteropServices;
using PictureSharp.Formats.Gif;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Gif;

public class GifEncoderTests
{
    public static TestCases<string> GifFiles => Corpus.Files("gif/");

    private static async Task AssertSameFrames(Image expected, Image actual)
    {
        await Assert.That(actual.Size).IsEqualTo(expected.Size);
        await Assert.That(actual.FrameCount).IsEqualTo(expected.FrameCount);
        for (int i = 0; i < expected.FrameCount; i++)
        {
            await Assert.That(expected.Frames[i].GetPixelSpan().SequenceEqual(actual.Frames[i].GetPixelSpan())).IsTrue().Because($"frame {i} differs");
            await Assert.That(actual.Frames[i].Duration).IsEqualTo(expected.Frames[i].Duration);
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

    [Test]
    [MethodDataSource(nameof(GifFiles))]
    public async Task RoundTrip_CorpusGif_IsLossless(string file)
    {
        using Image original = Image.Load(Corpus.PathOf(file));
        foreach (GifEncoderOptions options in new GifEncoderOptions[] { new(), new() { OptimizeFrames = false, Interlace = true } })
        {
            using Image back = Image.Load(original.ToGif(options));
            await AssertSameFrames(original, back);
            await Assert.That(back.LoopCount).IsEqualTo(original.LoopCount);
            await Sequence.AssertEqual(original.Metadata.Xmp, back.Metadata.Xmp);
            await Sequence.AssertEqual(original.Metadata.IccProfile, back.Metadata.IccProfile);
        }
    }

    // QuantizationTests covers every key against Pillow; through the encoder one key per mode is enough,
    // plus the rescaled hash of many_colors and a source whose 8 colors fit the palette.
    public static TestCases<string> QuantizeKeys() =>
        ["photo/mediancut/64", "photo/octree/64", "photo/dither/64", "many_colors/mediancut/256", "graphic/mediancut/16"];

    [Test]
    [MethodDataSource(nameof(QuantizeKeys))]
    public async Task Encode_StillImage_MatchesPillowQuantize(string key)
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
            await Assert.That(decoded.GetPixelSpan().ToArray()).IsEquivalentTo(pixels, CollectionOrdering.Matching);
            return;
        }

        await Assert.That(Corpus.Sha256(MemoryMarshal.AsBytes(decoded.GetPixelSpan()))).IsEqualTo(QuantizationTests.References.Value[key].RgbaSha256);
    }

    [Test]
    [Arguments("alpha_lossless")]
    [Arguments("alpha_mixed")]
    [Arguments("alpha_pillow")]
    public async Task WebPAnimation_ToGif_KeepsPixels(string name)
    {
        using Image webp = Image.Load(Corpus.PathOf($"webp/anim/{name}.webp"));
        using Image gif = Image.Load(webp.ToGif());
        await Assert.That(gif.FrameCount).IsEqualTo(webp.FrameCount);
        await Assert.That(gif.LoopCount).IsEqualTo(webp.LoopCount);
        for (int i = 0; i < webp.FrameCount; i++)
        {
            await Assert.That(gif.Frames[i].GetPixelSpan().ToArray()).IsEquivalentTo(Binarize(webp.Frames[i].GetPixelSpan()), CollectionOrdering.Matching);
            await Assert.That(gif.Frames[i].Duration).IsEqualTo(webp.Frames[i].Duration);
        }
    }

    [Test]
    [Arguments("opaque_lossless")]
    [Arguments("opaque_lossy")]
    [Arguments("alpha_lossy")]
    public async Task WebPAnimation_WithManyColors_IsQuantized(string name)
    {
        using Image webp = Image.Load(Corpus.PathOf($"webp/anim/{name}.webp"));
        using Image gif = Image.Load(webp.ToGif());
        await Assert.That(gif.FrameCount).IsEqualTo(webp.FrameCount);
        await Assert.That(gif.LoopCount).IsEqualTo(webp.LoopCount);
        double error = 0;
        for (int i = 0; i < webp.FrameCount; i++)
        {
            var colors = new HashSet<Rgba32>();
            Rgba32[] expected = Binarize(webp.Frames[i].GetPixelSpan());
            Rgba32[] actual = gif.Frames[i].GetPixelSpan().ToArray();
            for (int p = 0; p < expected.Length; p++)
            {
                await Assert.That(actual[p].A).IsEqualTo(expected[p].A);
                error += Math.Abs(expected[p].R - actual[p].R) + Math.Abs(expected[p].G - actual[p].G) + Math.Abs(expected[p].B - actual[p].B);
                colors.Add(actual[p]);
            }

            await Assert.That(colors.Count <= 256).IsTrue();
        }

        await Assert.That(error / (webp.FrameCount * webp.Width * webp.Height * 3) < 6).IsTrue().Because($"mean error {error / (webp.FrameCount * webp.Width * webp.Height * 3)}");
    }

    [Test]
    public async Task RandomAnimations_WithTransparency_RoundTrip()
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
                await Assert.That(back.LoopCount).IsEqualTo(animation.LoopCount);

                // Identical consecutive frames are merged into one longer frame when optimizing.
                int expectedFrame = 0;
                TimeSpan elapsed = TimeSpan.Zero;
                for (int i = 0; i < animation.FrameCount; i++)
                {
                    if (i > 0 && optimize && animation.Frames[i].GetPixelSpan().SequenceEqual(animation.Frames[i - 1].GetPixelSpan()))
                    {
                        continue;
                    }

                    await Assert.That(animation.Frames[i].GetPixelSpan().SequenceEqual(back.Frames[expectedFrame].GetPixelSpan())).IsTrue().Because($"round {round}, frame {i}");
                    expectedFrame++;
                }

                await Assert.That(back.FrameCount).IsEqualTo(expectedFrame);
                await Assert.That(back.Frames.Aggregate(elapsed, (t, f) => t + f.Duration)).IsEqualTo(TimeSpan.FromMilliseconds(30 * animation.FrameCount));
            }
        }
    }

    [Test]
    public async Task IdenticalFrames_AreMergedWhenOptimizing()
    {
        using var a = new Image(4, 4, new Rgba32(1, 2, 3));
        using var b = new Image(4, 4, new Rgba32(4, 5, 6));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(10), a, a, b, b, b);
        animation.Frames[1].Duration = TimeSpan.FromMilliseconds(20);
        using Image merged = Image.Load(animation.ToGif());
        await Assert.That(merged.Frames.Select(f => f.Duration.TotalMilliseconds)).IsEquivalentTo(new[] { 30.0, 30.0 }, CollectionOrdering.Matching);
        using Image kept = Image.Load(animation.ToGif(new GifEncoderOptions { OptimizeFrames = false }));
        await Assert.That(kept.FrameCount).IsEqualTo(5);
        using Image still = Image.Load(animation.ToGif(new GifEncoderOptions { SaveAnimation = false }));
        await Assert.That(still.FrameCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(4, 0)]
    [Arguments(5, 10)]
    [Arguments(15, 20)]
    [Arguments(1234, 1230)]
    [Arguments(10_000_000, 655_350)]
    public async Task FrameDurations_AreRoundedToCentiseconds(int milliseconds, int expected)
    {
        using var a = new Image(2, 2, new Rgba32(1, 2, 3));
        using var b = new Image(2, 2, new Rgba32(4, 5, 6));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(milliseconds), a, b);
        using Image back = Image.Load(animation.ToGif());
        foreach (var f in back.Frames)
        {
            await Assert.That(f.Duration.TotalMilliseconds).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments(0, false, 0)]
    [Arguments(1, true, 1)]
    [Arguments(2, false, 2)]
    [Arguments(65535, false, 65535)]
    public async Task LoopCount_RoundTrips(int loops, bool noExtension, int expected)
    {
        using var a = new Image(2, 2, new Rgba32(1, 2, 3));
        using var b = new Image(2, 2, new Rgba32(4, 5, 6));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), a, b);
        animation.LoopCount = loops;
        byte[] gif = animation.ToGif();
        await Assert.That(gif.AsSpan().IndexOf("NETSCAPE2.0"u8) < 0).IsEqualTo(noExtension);
        using Image back = Image.Load(gif);
        await Assert.That(back.LoopCount).IsEqualTo(expected);
    }

    [Test]
    [Arguments(128, 100, true)]
    [Arguments(100, 100, false)]
    [Arguments(0, 0, false)]
    [Arguments(255, 254, true)]
    public async Task TransparencyThreshold_DecidesTransparentPixels(int threshold, byte alpha, bool transparent)
    {
        using var image = new Image(3, 1, new Rgba32(9, 8, 7, alpha));
        image[0, 0] = new Rgba32(1, 2, 3);
        using Image back = Image.Load(image.ToGif(new GifEncoderOptions { TransparencyThreshold = threshold }));
        await Assert.That(back[2, 0]).IsEqualTo(transparent ? default : new Rgba32(9, 8, 7));
        await Assert.That(back[0, 0]).IsEqualTo(new Rgba32(1, 2, 3));
        await Assert.That(back.HasAlpha).IsEqualTo(transparent);
    }

    [Test]
    [Arguments(2)]
    [Arguments(5)]
    [Arguments(17)]
    [Arguments(128)]
    public async Task ColorCount_LimitsThePalette(int count)
    {
        using Image photo = Image.Load(Corpus.PathOf("src/photo_alpha.png"));
        foreach (GifQuantizer quantizer in Enum.GetValues<GifQuantizer>())
        {
            using Image back = Image.Load(photo.ToGif(new GifEncoderOptions { ColorCount = count, Quantizer = quantizer, Dither = GifDither.FloydSteinberg }));
            await Assert.That(back.GetPixelSpan().ToArray().Distinct().Count() <= count).IsTrue();
            await Assert.That(back.GetPixelSpan().ToArray().Select(p => p.A)).IsEquivalentTo(Binarize(photo.GetPixelSpan()).Select(p => p.A), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Header_Version_And_Interlace()
    {
        using var plain = new Image(20, 20, new Rgba32(1, 2, 3));
        await Assert.That(plain.ToGif().AsSpan().StartsWith("GIF87a"u8)).IsTrue();
        using var transparent = new Image(20, 20);
        await Assert.That(transparent.ToGif().AsSpan().StartsWith("GIF89a"u8)).IsTrue();

        for (int height = 1; height <= 17; height++)
        {
            using var image = new Image(3, height);
            for (int y = 0; y < height; y++)
            {
                image.GetRowSpan(y).Fill(new Rgba32((byte)(y * 15), 0, 0));
            }

            using Image back = Image.Load(image.ToGif(new GifEncoderOptions { Interlace = true }));
            await Assert.That(back.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Metadata_IsWrittenAsApplicationExtensions()
    {
        using Image image = new(4, 4, new Rgba32(1, 2, 3));
        image.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        image.Metadata.IccProfile = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        image.Metadata.Exif = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0];
        using Image back = Image.Load(image.ToGif());
        await Assert.That(back.Metadata.Xmp).IsEquivalentTo(image.Metadata.Xmp, CollectionOrdering.Matching);
        await Assert.That(back.Metadata.IccProfile).IsEquivalentTo(image.Metadata.IccProfile, CollectionOrdering.Matching);
        Assert.Null(back.Metadata.Exif);

        using Image none = Image.Load(image.ToGif(new GifEncoderOptions { Metadata = MetadataKinds.None }));
        await Assert.That(none.Metadata.IsEmpty).IsTrue();
    }

    [Test]
    public async Task AnimationBackgroundColor_RoundTrips()
    {
        using var a = new Image(2, 2, new Rgba32(1, 2, 3));
        using var b = new Image(2, 2, new Rgba32(4, 5, 6));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), a, b);
        animation.AnimationBackgroundColor = new Rgba32(4, 5, 6);
        using Image back = Image.Load(animation.ToGif());
        await Assert.That(back.AnimationBackgroundColor).IsEqualTo(new Rgba32(4, 5, 6));

        using var c = new Image(2, 2);
        using Image transparent = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), a, c);
        using Image back2 = Image.Load(transparent.ToGif());
        await Assert.That(back2.AnimationBackgroundColor).IsEqualTo(default);
    }

    [Test]
    public async Task SaveApis_WriteGif()
    {
        using var image = new Image(5, 5, new Rgba32(10, 20, 30));
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.gif");
        try
        {
            image.Save(path);
            await Assert.That(Image.DetectFormat(path)).IsEqualTo(ImageFormat.Gif);
            await image.SaveAsGifAsync(path, new GifEncoderOptions { Interlace = true });
            using Image loaded = await Image.LoadAsync(path);
            await Assert.That(loaded[4, 4]).IsEqualTo(new Rgba32(10, 20, 30));
        }
        finally
        {
            File.Delete(path);
        }

        using var stream = new NonSeekableStream();
        image.SaveAsGif(stream);
        image.Save(stream, ImageFormat.Gif);
        await Assert.That(stream.Length > 0).IsTrue();
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    [Test]
    public async Task Options_AreValidated()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GifEncoderOptions { ColorCount = 1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GifEncoderOptions { ColorCount = 257 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GifEncoderOptions { TransparencyThreshold = 256 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GifEncoderOptions { Quantizer = (GifQuantizer)9 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GifEncoderOptions { Dither = (GifDither)9 });
        await Assert.That(new GifEncoderOptions().Format).IsEqualTo(ImageFormat.Gif);
        using var tooWide = new Image(65536, 1);
        Assert.ThrowsExactly<NotSupportedException>(() => tooWide.ToGif());
    }
}
