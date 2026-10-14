using BenchmarkDotNet.Attributes;

namespace RasterSharp.Benchmarks;

[MemoryDiagnoser]
public class LosslessDecodeBenchmarks
{
    private byte[] _data = null!;

    public IEnumerable<InputFile> Files()
    {
        string root = FindRoot();
        foreach (string name in new[]
        {
            "photo_ll_z6.webp", "photo_ll_q0m0.webp", "graphic_ll_z6.webp",
            "photo_alpha_ll_z6.webp", "graphic_alpha_ll_z6.webp", "odd_alpha_ll_z9.webp", "wide_ll_z6.webp",
        })
        {
            yield return new InputFile(Path.Combine(root, "tests", "assets", "corpus", "webp", "lossless", name));
        }

        string? local = Environment.GetEnvironmentVariable("RASTERSHARP_BENCHMARK_WEBP_FILES");
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
        _data = System.IO.File.ReadAllBytes(File.Path);
        ImageInfo info = Image.Identify(_data);
        if (info.Format != ImageFormat.WebP || info.IsAnimated || info.WebPCompression != WebPCompression.Lossless)
        {
            throw new ArgumentException("The decode benchmark requires a static lossless WebP.", nameof(File));
        }
    }

    [Benchmark]
    public int Decode()
    {
        using Image image = Image.Load(_data);
        return image.Width;
    }

    private static string FindRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (System.IO.File.Exists(Path.Combine(directory, "RasterSharp.slnx")))
            {
                return directory;
            }
        }

        throw new DirectoryNotFoundException("RasterSharp.slnx not found above the benchmark executable.");
    }

    public sealed record InputFile(string Path)
    {
        public override string ToString() => System.IO.Path.GetFileName(Path);
    }
}
