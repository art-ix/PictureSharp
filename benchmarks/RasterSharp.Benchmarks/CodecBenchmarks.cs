using BenchmarkDotNet.Attributes;
using static RasterSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace RasterSharp.Benchmarks;

/// <summary>
/// Complete PNG, JPEG and lossy WebP encoding/decoding and Lanczos resizing of committed corpus sources and optional local images.
/// The decode benchmarks read bytes encoded once during setup with default options.
/// </summary>
[MemoryDiagnoser]
public class CodecBenchmarks
{
    private Image _image = null!;
    private byte[] _png = null!;
    private byte[] _jpeg = null!;
    private byte[] _webp = null!;

    public IEnumerable<InputFile> Files()
    {
        string root = FindRoot();
        foreach (string name in new[] { "photo.png", "graphic.png", "photo_alpha.png" })
        {
            yield return new InputFile(Path.Combine(root, "tests", "assets", "corpus", "src", name));
        }

        string? local = Environment.GetEnvironmentVariable("RASTERSHARP_BENCHMARK_CODEC_FILES");
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

    [GlobalSetup]
    public void Setup()
    {
        _image = Image.Load(File.Path);
        _png = _image.ToPng();
        _jpeg = _image.ToJpeg();
        _webp = _image.ToWebP();
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public int PngEncode() => _image.ToPng().Length;

    [Benchmark]
    public int PngDecode()
    {
        using Image image = Image.Load(_png);
        return image.Width;
    }

    [Benchmark]
    public int JpegEncode() => _image.ToJpeg().Length;

    [Benchmark]
    public int JpegDecode()
    {
        using Image image = Image.Load(_jpeg);
        return image.Width;
    }

    [Benchmark]
    public int WebPLossyEncode() => _image.ToWebP().Length;

    [Benchmark]
    public int WebPLossyDecode()
    {
        using Image image = Image.Load(_webp);
        return image.Width;
    }

    [Benchmark]
    public int ResizeHalfLanczos()
    {
        using Image resized = _image.Clone(x => x.Resize(Math.Max(1, _image.Width / 2), Math.Max(1, _image.Height / 2), ResamplingFilter.Lanczos3));
        return resized.Width;
    }
}
