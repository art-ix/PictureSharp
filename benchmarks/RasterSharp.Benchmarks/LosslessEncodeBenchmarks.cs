using BenchmarkDotNet.Attributes;
using static RasterSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace RasterSharp.Benchmarks;

/// <summary>Complete lossless WebP encoding of committed corpus sources and optional local images.</summary>
[MemoryDiagnoser]
public class LosslessEncodeBenchmarks
{
    private Image _image = null!;
    private WebPEncoderOptions _options = null!;

    public IEnumerable<InputFile> Files()
    {
        string root = FindRoot();
        foreach (string name in new[] { "photo.png", "graphic.png", "photo_alpha.png", "graphic_alpha.png", "odd_alpha.png", "wide.png" })
        {
            yield return new InputFile(Path.Combine(root, "tests", "assets", "corpus", "src", name));
        }

        string? local = Environment.GetEnvironmentVariable("RASTERSHARP_BENCHMARK_ENCODE_FILES");
        if (!string.IsNullOrWhiteSpace(local))
        {
            foreach (string path in local.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return new InputFile(Path.GetFullPath(path));
            }
        }
    }

    [ParamsSource(nameof(Files))]
    public InputFile File { get; set; } = null!;

    [Params(0, 4, 5)]
    public int EncoderMethod { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _image = Image.Load(File.Path);
        _options = new WebPEncoderOptions { Lossless = true, Method = EncoderMethod, Quality = 75 };
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public int Encode() => _image.ToWebP(_options).Length;
}
