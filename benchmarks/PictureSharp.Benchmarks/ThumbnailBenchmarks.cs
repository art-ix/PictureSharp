using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>
/// JPEG thumbnails: <c>Load</c> followed by <c>Thumbnail</c> against <see cref="DecoderOptions.TargetSize"/> with the default
/// reducing gap (2) and with gap 1. Without local files the corpus photo is scaled to 4032×3024 and encoded as baseline
/// 4:2:0 and progressive JPEG during setup.
/// </summary>
[MemoryDiagnoser]
public class ThumbnailBenchmarks
{
    private byte[] _data = null!;

    public IEnumerable<InputFile> Files()
    {
        string? local = Environment.GetEnvironmentVariable("PICTURESHARP_BENCHMARK_THUMBNAIL_FILES");
        if (string.IsNullOrWhiteSpace(local))
        {
            yield return new InputFile("synthetic-12mp-baseline");
            yield return new InputFile("synthetic-12mp-progressive");
            yield break;
        }

        foreach (string path in local.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return new InputFile(Path.GetFullPath(path));
        }
    }

    [ParamsSource(nameof(Files))]
    public InputFile File { get; set; } = null!;

    [Params(256, 512, 1920)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup() => _data = Read(File.Path);

    [Benchmark(Baseline = true)]
    public Image LoadThenThumbnail() => Run(_data, Mode.LoadThenThumbnail, Size);

    [Benchmark]
    public Image TargetSize() => Run(_data, Mode.TargetSize, Size);

    [Benchmark]
    public Image TargetSizeGap1() => Run(_data, Mode.TargetSizeGap1, Size);

    public enum Mode
    {
        LoadThenThumbnail,
        TargetSize,
        TargetSizeGap1,
    }

    internal static Image Run(byte[] data, Mode mode, int size)
    {
        if (mode == Mode.LoadThenThumbnail)
        {
            Image image = Image.Load(data);
            image.Mutate(x => x.Thumbnail(size, size));
            return image;
        }

        return Image.Load(data, new DecoderOptions { TargetSize = new(size, size), TargetReducingGap = mode == Mode.TargetSize ? 2 : 1 });
    }

    internal static byte[] Read(string path)
    {
        if (System.IO.File.Exists(path))
        {
            return System.IO.File.ReadAllBytes(path);
        }

        using Image source = Image.Load(Path.Combine(FindRoot(), "tests", "assets", "corpus", "src", "photo.png"));
        using Image large = source.Clone(x => x.Resize(4032, 3024, ResamplingFilter.Bicubic));
        return large.ToJpeg(new JpegEncoderOptions { Progressive = path.EndsWith("progressive", StringComparison.Ordinal) });
    }

    /// <summary>
    /// <c>peak &lt;file&gt; &lt;mode&gt; &lt;size&gt;</c>: one operation in a fresh process; prints the rise of the peak resident set
    /// (Linux <c>VmHWM</c>) over the state after reading the input and warming up on a small image, and the managed
    /// bytes allocated.
    /// </summary>
    internal static void PeakProbe(string[] args)
    {
        byte[] data = Read(args[1]);
        Mode mode = Enum.Parse<Mode>(args[2]);
        int size = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        using (Image warm = Image.Load(Path.Combine(FindRoot(), "tests", "assets", "corpus", "jpeg", "q90_420.jpg")))
        {
            Run(warm.ToJpeg(), mode, 64).Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = PeakResident();
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        using Image image = Run(data, mode, size);
        watch.Stop();
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Console.WriteLine(FormattableString.Invariant(
            $"{Path.GetFileName(args[1])}\t{mode}\t{size}\t{image.Width}x{image.Height}\tpeak+{(PeakResident() - before) / 1048576.0:F1} MiB\talloc {allocated / 1048576.0:F1} MiB\t{watch.Elapsed.TotalMilliseconds:F0} ms (cold)"));
    }

    private static long PeakResident()
    {
        foreach (string line in System.IO.File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
            {
                return long.Parse(line.AsSpan(6).Trim().TrimEnd("kB").Trim(), System.Globalization.CultureInfo.InvariantCulture) * 1024;
            }
        }

        return 0;
    }
}
