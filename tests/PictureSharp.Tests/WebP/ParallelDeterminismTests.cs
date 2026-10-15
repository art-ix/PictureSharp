using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Lossless;
using PictureSharp.IO;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

/// <summary>Parallel encoding and decoding must give exactly the single-threaded output.</summary>
public class ParallelDeterminismTests
{
    private static Image Source(bool alpha = false)
    {
        // Large enough for the parallel paths (wavefront rows, filter threshold, several cost refreshes).
        using Image photo = ImageReader.Load(Corpus.PathOf(alpha ? "src/photo_alpha.png" : "src/photo.png"));
        return photo.Clone(x => x.Resize(400, 336, ResamplingFilter.Bicubic));
    }

    public static TheoryData<string> LossyCases() => new()
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

    [Theory]
    [MemberData(nameof(LossyCases))]
    public void LossyEncoder_IsIndependentOfThreadCount(string name)
    {
        using Image image = Source(alpha: name == "alpha");
        WebPEncoderOptions options = LossyOptions(name);
        byte[] single = image.ToWebP(options with { MaxDegreeOfParallelism = 1 });
        Assert.Equal(single, image.ToWebP(options with { MaxDegreeOfParallelism = -1 }));
        Assert.Equal(single, image.ToWebP(options with { MaxDegreeOfParallelism = 3 }));
    }

    [Theory]
    [InlineData(4, 75)]
    [InlineData(5, 90)]
    [InlineData(6, 100)]
    public void LosslessEncoder_IsIndependentOfThreadCount(int method, int quality)
    {
        using Image image = Source(alpha: true);
        var options = new WebPEncoderOptions { Lossless = true, Method = method, Quality = quality };
        byte[] single = image.ToWebP(options with { MaxDegreeOfParallelism = 1 });
        Assert.Equal(single, image.ToWebP(options with { MaxDegreeOfParallelism = -1 }));
        Assert.Equal(single, image.ToWebP(options with { MaxDegreeOfParallelism = 2 }));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(60, 4, true)]
    [InlineData(100, 7, false)]
    public void LossyDecoder_IsIndependentOfThreadCount(int filterStrength, int sharpness, bool fancy)
    {
        using Image image = Source();
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { FilterStrength = filterStrength, FilterSharpness = sharpness });
        AssertSameDecode(encoded, fancy);
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void LossyDecoder_SimpleFilter_IsIndependentOfThreadCount()
    {
        if (!ReferenceTools.HasCwebp) return;
        using Image image = Source();
        string input = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.png");
        string output = input + ".webp";
        try
        {
            image.SaveAsPng(input);
            Assert.Equal(0, ReferenceTools.Run("cwebp", $"-quiet -q 70 -nostrong -f 80 \"{input}\" -o \"{output}\"", out _));
            AssertSameDecode(File.ReadAllBytes(output), fancy: true);
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    private static void AssertSameDecode(byte[] encoded, bool fancy)
    {
        var options = new DecoderOptions { WebP = new() { FancyUpsampling = fancy }};
        using Image single = ImageReader.Load(encoded, options with { MaxDegreeOfParallelism = 1 });
        using Image parallel = ImageReader.Load(encoded, options with { MaxDegreeOfParallelism = -1 });
        using Image three = ImageReader.Load(encoded, options with { MaxDegreeOfParallelism = 3 });
        Assert.Equal(single.ToPixelData(), parallel.ToPixelData());
        Assert.Equal(single.ToPixelData(), three.ToPixelData());
    }

    // The hash chain search runs in speculative segments merged in sequential order; matches must not depend on it.
    [Theory]
    [InlineData(75, false)]
    [InlineData(100, false)]
    [InlineData(25, false)]
    [InlineData(75, true)]
    public void HashChain_IsIndependentOfThreadCount(int quality, bool lowEffort)
    {
        const int Width = 700, Height = 420;
        uint[] pixels = HashChainPixels(Width, Height);
        using var single = new Vp8LBackwardRefs(pixels, Width, Height, quality, lowEffort);
        using Vp8LRefs expected = single.Greedy(false), expectedRle = single.Greedy(true);
        foreach (int threads in new[] { 2, 3, 5, 16, -1 })
        {
            using var parallel = new Vp8LBackwardRefs(pixels, Width, Height, quality, lowEffort, maxDegreeOfParallelism: threads);
            using Vp8LRefs actual = parallel.Greedy(false), actualRle = parallel.Greedy(true);
            Assert.True(expected.Tokens.SequenceEqual(actual.Tokens), $"{threads} threads");
            Assert.True(expectedRle.Tokens.SequenceEqual(actualRle.Tokens), $"{threads} threads, RLE");
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
