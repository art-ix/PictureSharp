using BenchmarkDotNet.Attributes;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>
/// PNG encoding and decoding of large images at deflate levels 1, 6 and 9, on one thread and on all of them.
/// The images are built from corpus sources during setup, so every build sees the same pixels.
/// </summary>
[MemoryDiagnoser]
public class PngCompressionBenchmarks
{
    private Image _image = null!;
    private byte[] _png = null!;

    [Params("photo-4000x3000", "noisy-2000x1500", "graphic-2000x1500")]
    public string Source { get; set; } = null!;

    [Params(1, 6, 9)]
    public int Level { get; set; }

    [Params(1, -1)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _image = Create(Source);
        _png = _image.ToPng(new PngEncoderOptions { CompressionLevel = Level });
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public int Encode() => _image.ToPng(new PngEncoderOptions { CompressionLevel = Level, MaxDegreeOfParallelism = Threads }).Length;

    [Benchmark]
    public int Decode()
    {
        using Image image = Image.Load(_png, new DecoderOptions { MaxDegreeOfParallelism = Threads });
        return image.Width;
    }

    private static Image Create(string source)
    {
        string corpus = Path.Combine(FindRoot(), "tests", "assets", "corpus", "src");
        switch (source)
        {
            case "photo-4000x3000":
                {
                    using Image photo = Image.Load(Path.Combine(corpus, "photo.png"));
                    return photo.Clone(x => x.Resize(4000, 3000, ResamplingFilter.Bicubic));
                }

            case "graphic-2000x1500":
                {
                    using Image graphic = Image.Load(Path.Combine(corpus, "graphic.png"));
                    return graphic.Clone(x => x.Resize(2000, 1500, ResamplingFilter.Nearest));
                }

            default:
                {
                    // Smooth gradients with sensor-like noise: hard for the match finder, typical for camera images.
                    const int width = 2000, height = 1500;
                    var rng = new Random(42);
                    var pixels = new Rgba32[width * height];
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int n = rng.Next(-6, 7);
                            pixels[(y * width) + x] = new Rgba32(
                                (byte)Math.Clamp((x * 255 / width) + n, 0, 255),
                                (byte)Math.Clamp((y * 255 / height) + n, 0, 255),
                                (byte)Math.Clamp(128 + (int)(60 * Math.Sin((x + y) / 90.0)) + n, 0, 255),
                                255);
                        }
                    }

                    return Image.LoadPixelData(pixels, width, height);
                }
        }
    }
}
