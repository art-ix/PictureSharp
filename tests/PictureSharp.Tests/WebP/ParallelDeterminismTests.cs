using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Lossless;
using PictureSharp.Infrastructure.Codecs.WebP.Lossy;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

/// <summary>Parallel encoding and decoding must give exactly the single-threaded output.</summary>
public class ParallelDeterminismTests
{
    private static Image Source(bool alpha = false)
    {
        // Large enough for the parallel paths (wavefront rows, filter threshold, several cost refreshes).
        using Image photo = Image.Load(Corpus.PathOf(alpha ? "src/photo_alpha.png" : "src/photo.png"));
        return photo.Clone(x => x.Resize(400, 336, ResamplingFilter.Bicubic));
    }

    public static TestCases<string> LossyCases() => new()
    {
        "q75", "q30m6", "q95m3", "autofilter", "passes", "targetsize", "segments1", "sharpyuv", "alpha", "partitions",
    };

    private static WebPEncoderOptions LossyOptions(string name) => name switch
    {
        "q75" => new(),
        "q30m6" => new() { Quality = 30, Method = 6 },
        "q95m3" => new() { Quality = 95, Method = 3 },
        "autofilter" => new() { AutoFilter = true },
        "passes" => new() { Passes = 3, TargetPsnr = 38 },
        "targetsize" => new() { TargetSize = 12000 },
        "segments1" => new() { Segments = 1, SnsStrength = 0 },
        "sharpyuv" => new() { UseSharpYuv = true },
        "alpha" => new() { AlphaQuality = 60 },
        "partitions" => new() { Partitions = 2, Method = 2 },
        _ => throw new ArgumentException(name),
    };

    [Test]
    [Property("Category", "Slow")]
    [MethodDataSource(nameof(LossyCases))]
    public async Task LossyEncoder_IsIndependentOfThreadCount(string name)
    {
        using Image image = Source(alpha: name == "alpha");
        WebPEncoderOptions options = LossyOptions(name);
        byte[] single = image.ToWebP(options with { MaxDegreeOfParallelism = 1 });
        await Assert.That(image.ToWebP(options with { MaxDegreeOfParallelism = -1 })).IsEquivalentTo(single, CollectionOrdering.Matching);
        await Assert.That(image.ToWebP(options with { MaxDegreeOfParallelism = 3 })).IsEquivalentTo(single, CollectionOrdering.Matching);
    }

    // The wavefront pass updates the per-segment maximum edge from several threads; a lost update changed the
    // filter strength (and the bytes) of about 1% of method 6 encodes of a photo in 0.7.4.
    [Test]
    public async Task MaxEdge_KeepsTheLargestValueUnderContention()
    {
        for (int round = 0; round < 20; round++)
        {
            var segment = new Vp8Segment();
            int[] values = Enumerable.Range(0, 20000).Select(i => (i * 7919 + round) % 100003).ToArray();
            Parallel.For(0, values.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => Vp8ModeDecision.StoreMaxEdge(segment, values[i]));
            await Assert.That(segment.MaxEdge).IsEqualTo(values.Max());
        }
    }

    [Test]
    [Property("Category", "Slow")]
    [Arguments(4, 75)]
    [Arguments(5, 90)]
    [Arguments(6, 100)]
    public async Task LosslessEncoder_IsIndependentOfThreadCount(int method, int quality)
    {
        using Image image = Source(alpha: true);
        var options = new WebPEncoderOptions { Lossless = true, Method = method, Quality = quality };
        byte[] single = image.ToWebP(options with { MaxDegreeOfParallelism = 1 });
        await Assert.That(image.ToWebP(options with { MaxDegreeOfParallelism = -1 })).IsEquivalentTo(single, CollectionOrdering.Matching);
        await Assert.That(image.ToWebP(options with { MaxDegreeOfParallelism = 2 })).IsEquivalentTo(single, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0, 0, true)]
    [Arguments(60, 4, true)]
    [Arguments(100, 7, false)]
    public async Task LossyDecoder_IsIndependentOfThreadCount(int filterStrength, int sharpness, bool fancy)
    {
        using Image image = Source();
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { FilterStrength = filterStrength, FilterSharpness = sharpness });
        await AssertSameDecode(encoded, fancy);
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task LossyDecoder_SimpleFilter_IsIndependentOfThreadCount()
    {
        Skip.Unless(ReferenceTools.HasCwebp, "cwebp 1.6.0 is not available.");
        using Image image = Source();
        string input = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.png");
        string output = input + ".webp";
        try
        {
            image.SaveAsPng(input);
            await Assert.That(ReferenceTools.Run("cwebp", $"-quiet -q 70 -nostrong -f 80 \"{input}\" -o \"{output}\"", out _)).IsEqualTo(0);
            await AssertSameDecode(File.ReadAllBytes(output), fancy: true);
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    private static async Task AssertSameDecode(byte[] encoded, bool fancy)
    {
        var options = new DecoderOptions { WebP = new() { FancyUpsampling = fancy }};
        using Image single = Image.Load(encoded, options with { MaxDegreeOfParallelism = 1 });
        using Image parallel = Image.Load(encoded, options with { MaxDegreeOfParallelism = -1 });
        using Image three = Image.Load(encoded, options with { MaxDegreeOfParallelism = 3 });
        await Assert.That(parallel.ToPixelData()).IsEquivalentTo(single.ToPixelData(), CollectionOrdering.Matching);
        await Assert.That(three.ToPixelData()).IsEquivalentTo(single.ToPixelData(), CollectionOrdering.Matching);
    }

    // The hash chain search runs in speculative segments merged in sequential order; matches must not depend on it.
    [Test]
    [Arguments(75, false)]
    [Arguments(100, false)]
    [Arguments(25, false)]
    [Arguments(75, true)]
    public async Task HashChain_IsIndependentOfThreadCount(int quality, bool lowEffort)
    {
        const int Width = 700, Height = 420;
        uint[] pixels = HashChainPixels(Width, Height);
        using var single = new Vp8LBackwardRefs(pixels, Width, Height, quality, lowEffort);
        using Vp8LRefs expected = single.Greedy(false), expectedRle = single.Greedy(true);
        foreach (int threads in new[] { 2, 3, 5, 16, -1 })
        {
            using var parallel = new Vp8LBackwardRefs(pixels, Width, Height, quality, lowEffort, maxDegreeOfParallelism: threads);
            using Vp8LRefs actual = parallel.Greedy(false), actualRle = parallel.Greedy(true);
            await Assert.That(expected.Tokens.SequenceEqual(actual.Tokens)).IsTrue().Because($"{threads} threads");
            await Assert.That(expectedRle.Tokens.SequenceEqual(actualRle.Tokens)).IsTrue().Because($"{threads} threads, RLE");
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(-1)]
    public async Task HashChain_StopsOnCanceledToken(int threads)
    {
        const int Width = 700, Height = 420;
        uint[] pixels = HashChainPixels(Width, Height);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        Assert.Throws<OperationCanceledException>(() =>
            new Vp8LBackwardRefs(pixels, Width, Height, 75, false, maxDegreeOfParallelism: threads, cancellationToken: canceled.Token).Dispose());
    }

    // TargetSize resizes every frame directly (not through Mutate) so that the decoder's thread limit applies.
    // A nearest 3× upscale keeps the exact GIF palette and puts the Lanczos pass above the parallel threshold;
    // two frames are enough and keep the test fast.
    [Test]
    public async Task TargetSize_AnimatedGif_IsIndependentOfThreadCount_AndEqualsLoadAndResize()
    {
        using Image source = Image.Load(Corpus.PathOf("gif/pil_anim_alpha.gif"));
        while (source.Frames.Count > 2) source.Frames.RemoveAt(2);
        source.Mutate(x => x.Resize(288, 192, ResamplingFilter.Nearest));
        using var file = new MemoryStream();
        source.Save(file, ImageFormat.Gif);
        using Image expected = source.Clone(x => x.Thumbnail(24, 24, ResamplingFilter.Lanczos3));
        foreach (int threads in new[] { 1, -1 })
        {
            using Image image = Image.Load(file.ToArray(), new DecoderOptions
            {
                TargetSize = new(24, 24), TargetResampling = ResamplingFilter.Lanczos3, MaxDegreeOfParallelism = threads,
            });
            await Assert.That(image.Frames.Count).IsEqualTo(expected.Frames.Count).Because($"{threads} threads");
            for (int i = 0; i < image.Frames.Count; i++)
            {
                await Assert.That(image.Frames[i].ToPixelData()).IsEquivalentTo(expected.Frames[i].ToPixelData(), CollectionOrdering.Matching)
                    .Because($"{threads} threads, frame {i}");
            }
        }
    }

    // Noise, constant runs, short periodic patterns and long copies, in blocks that straddle segment boundaries.
    private static uint[] HashChainPixels(int width, int height)
    {
        var rng = new Random(31);
        uint[] pixels = new uint[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = ((i / 6007) % 4) switch
            {
                0 => (uint)rng.Next() & 0xff0f0f0fu,
                1 => 0xff204060u,
                2 => 0xff000000u | (uint)(i % 37 * 0x010203),
                _ => i >= 70000 ? pixels[i - 70000] : (uint)rng.Next(),
            };
        }

        return pixels;
    }
}
