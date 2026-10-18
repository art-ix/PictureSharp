using BenchmarkDotNet.Attributes;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>
/// JPEG decoding of fixed files on one thread and with the default parallelism. Reads
/// <c>PICTURESHARP_BENCHMARK_JPEG_FILES</c>; without it, the committed baseline, 4:4:4 and progressive corpus files.
/// </summary>
[MemoryDiagnoser]
public class JpegDecodeBenchmarks
{
    private byte[] _jpeg = null!;

    public IEnumerable<InputFile> Files() =>
        JpegEncodeBenchmarks.Inputs("PICTURESHARP_BENCHMARK_JPEG_FILES", Path.Combine("jpeg", "q90_420.jpg"), Path.Combine("jpeg", "q90_444.jpg"),
            Path.Combine("jpeg", "progressive_420.jpg"));

    [ParamsSource(nameof(Files))]
    public InputFile File { get; set; } = null!;

    [Params(1, -1)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup() => _jpeg = System.IO.File.ReadAllBytes(File.Path);

    [Benchmark]
    public int Decode()
    {
        using Image image = Image.Load(_jpeg, new DecoderOptions { MaxDegreeOfParallelism = Threads });
        return image.Width;
    }
}
