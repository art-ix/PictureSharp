using BenchmarkDotNet.Attributes;
using PictureSharp.Formats.Jpeg;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>
/// JPEG encoding of fixed sources (q75 4:2:0, q90 4:4:4 and progressive) on one thread and with the default
/// parallelism. Reads <c>PICTURESHARP_BENCHMARK_JPEG_SOURCES</c>; without it, the corpus photo.
/// </summary>
[MemoryDiagnoser]
public class JpegEncodeBenchmarks
{
    private Image _image = null!;

    public IEnumerable<InputFile> Sources() => Inputs("PICTURESHARP_BENCHMARK_JPEG_SOURCES", Path.Combine("src", "photo.png"));

    [ParamsSource(nameof(Sources))]
    public InputFile Source { get; set; } = null!;

    [Params(1, -1)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup() => _image = Image.Load(Source.Path);

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public int Q75() => _image.Encode(new JpegEncoderOptions { Quality = 75, MaxDegreeOfParallelism = Threads }).Length;

    [Benchmark]
    public int Q90Ratio444() =>
        _image.Encode(new JpegEncoderOptions { Quality = 90, Subsampling = JpegSubsampling.Ratio444, MaxDegreeOfParallelism = Threads }).Length;

    [Benchmark]
    public int Progressive() =>
        _image.Encode(new JpegEncoderOptions { Quality = 75, Progressive = true, MaxDegreeOfParallelism = Threads }).Length;

    // Paths from the variable (separated by the platform's path separator), or the named corpus files.
    internal static IEnumerable<InputFile> Inputs(string variable, params string[] corpus)
    {
        string? local = Environment.GetEnvironmentVariable(variable);
        IEnumerable<string> paths = string.IsNullOrWhiteSpace(local)
            ? corpus.Select(name => Path.Combine(FindRoot(), "tests", "assets", "corpus", name))
            : local.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFullPath);
        return paths.Select(path => new InputFile(path));
    }
}
