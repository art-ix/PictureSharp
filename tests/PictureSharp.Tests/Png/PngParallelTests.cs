using PictureSharp.Algorithms.Compression;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Png;

/// <summary>Parallel deflate, PNG encoding and pipelined decoding give the sequential results.</summary>
public class PngParallelTests
{
    private static byte[] Input(string kind, int length)
    {
        var rng = new Random(length ^ kind.Length);
        var data = new byte[length];
        switch (kind)
        {
            case "zeros":
                break;
            case "period":  // long matches whose phase rarely meets a segment start
                for (int i = 0; i < length; i++)
                {
                    data[i] = (byte)(i % 259 == 0 ? 7 : 1);
                }

                break;
            case "random":
                rng.NextBytes(data);
                break;
            default:  // image-like rows with noise and repeats
                for (int i = 0; i < length; i++)
                {
                    data[i] = rng.Next(10) == 0 ? (byte)rng.Next(256) : (byte)((i / 3) + (i % 1500 < 20 ? rng.Next(3) : 0));
                }

                break;
        }

        return data;
    }

    public static TestCases<string, int> Inputs => new()
    {
        { "zeros", 300_000 }, { "period", 700_001 }, { "random", 400_000 }, { "image", 1_100_000 }, { "image", 262_144 },
    };

    [Test]
    [MethodDataSource(nameof(Inputs))]
    public async Task ParallelDeflate_EqualsSequential(string kind, int length)
    {
        byte[] input = Input(kind, length);
        foreach (int level in new[] { 1, 6, 9 })
        {
            byte[] sequential = Deflater.CompressZlib(input, level, 1);
            foreach (int threads in new[] { 2, 3, 16, -1 })
            {
                await Assert.That(Deflater.CompressZlib(input, level, threads)).IsEquivalentTo(sequential, CollectionOrdering.Matching);
            }

            await Assert.That(Inflater.InflateZlib(sequential, 0, sequential.Length, input.Length, input.Length)).IsEquivalentTo(input, CollectionOrdering.Matching);
        }
    }

    private static Image PhotoLike(int width, int height, bool alpha)
    {
        var rng = new Random(width * height);
        var pixels = new Rgba32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int n = rng.Next(9) - 4;
                pixels[(y * width) + x] = new Rgba32((byte)(x + n), (byte)(y + n), (byte)((x ^ y) + n), alpha ? (byte)(x * 3) : (byte)255);
            }
        }

        return Image.LoadPixelData(pixels, width, height);
    }

    [Test]
    [Property("Category", "Slow")]
    [Arguments(800, 500, false, false)]
    [Arguments(700, 450, true, false)]
    [Arguments(640, 480, true, true)]
    public async Task ParallelPngEncode_EqualsSequential_AndDecodesBack(int width, int height, bool alpha, bool interlace)
    {
        using Image image = PhotoLike(width, height, alpha);
        foreach (PngFilterStrategy filter in Enum.GetValues<PngFilterStrategy>())
        {
            var options = new PngEncoderOptions { FilterStrategy = filter, Interlace = interlace, MaxDegreeOfParallelism = 1 };
            byte[] sequential = image.ToPng(options);
            await Assert.That(image.ToPng(options with { MaxDegreeOfParallelism = 3 })).IsEquivalentTo(sequential, CollectionOrdering.Matching);
            await Assert.That(image.ToPng(options with { MaxDegreeOfParallelism = -1 })).IsEquivalentTo(sequential, CollectionOrdering.Matching);

            using Image piped = Image.Load(sequential);
            using Image serial = Image.Load(sequential, new DecoderOptions { MaxDegreeOfParallelism = 1 });
            await Assert.That(piped.Frames[0].GetPixelSpan().SequenceEqual(image.Frames[0].GetPixelSpan())).IsTrue();
            await Assert.That(serial.Frames[0].GetPixelSpan().SequenceEqual(image.Frames[0].GetPixelSpan())).IsTrue();
        }
    }

    [Test]
    public async Task PipelinedDecode_DamagedData_OnlyThrowsImageFormatException()
    {
        using Image image = PhotoLike(900, 400, alpha: true);
        byte[] png = image.ToPng();
        var rng = new Random(49);
        var outcomes = new List<string>();
        for (int trial = 0; trial < 60; trial++)
        {
            byte[] damaged = (byte[])png.Clone();
            if (trial % 3 == 0)
            {
                damaged = damaged.AsSpan(0, rng.Next(100, damaged.Length)).ToArray();
            }
            else
            {
                for (int k = 0; k < 1 + rng.Next(4); k++)
                {
                    damaged[rng.Next(60, damaged.Length - 20)] ^= (byte)(1 << rng.Next(8));
                }
            }

            try
            {
                using Image piped = Image.Load(damaged);
                using Image serial = Image.Load(damaged, new DecoderOptions { MaxDegreeOfParallelism = 1 });
                await Assert.That(piped.Frames[0].GetPixelSpan().SequenceEqual(serial.Frames[0].GetPixelSpan())).IsTrue();
                outcomes.Add("ok");
            }
            catch (ImageFormatException)
            {
                Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(damaged, new DecoderOptions { MaxDegreeOfParallelism = 1 }));
                outcomes.Add("error");
            }
        }

        await Assert.That(outcomes).Contains("error");
    }
}
