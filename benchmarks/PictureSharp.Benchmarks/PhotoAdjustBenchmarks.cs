using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using PictureSharp.Processing;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>The photographic adjustments (0.7.7, 0.7.11) on a 12 MP image, each applied to a copy-on-write clone.</summary>
[MemoryDiagnoser]
public class PhotoAdjustBenchmarks
{
    private Image _image = null!;

    public enum Operation
    {
        CopyOnly,
        Exposure,
        WhiteBalance,
        Vibrance,
        Vignette,
        ShadowsHighlights,
        Clarity,
        Denoise,
        Curves,
        NeutralWhiteBalance,
        AutoWhiteBalance,
        RemoveDust,
        ReduceColorNoise,
    }

    [ParamsAllValues]
    public Operation Op { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        using Image source = Image.Load(Path.Combine(FindRoot(), "tests/assets/corpus/src/photo_alpha.png"));
        _image = source.Clone(x => x.Resize(4000, 3000, ResamplingFilter.Bicubic));
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public int Run()
    {
        using Image result = _image.Clone(x => _ = Op switch
        {
            Operation.CopyOnly => x.Apply(static _ => { }),
            Operation.Exposure => x.Exposure(0.7f),
            Operation.WhiteBalance => x.WhiteBalance(0.4f, -0.2f),
            Operation.Vibrance => x.Vibrance(0.5f),
            Operation.Vignette => x.Vignette(0.6f),
            Operation.ShadowsHighlights => x.ShadowsHighlights(0.5f, -0.5f),
            Operation.Clarity => x.Clarity(0.5f),
            Operation.Denoise => x.Denoise(0.5f),
            Operation.Curves => x.Curves([new(0, 0), new(64, 52), new(192, 206), new(255, 255)]),
            Operation.NeutralWhiteBalance => x.WhiteBalance(new Rgba32(212, 196, 170, 255)),
            Operation.AutoWhiteBalance => x.AutoWhiteBalance(),
            Operation.RemoveDust => x.RemoveDust(6),
            Operation.ReduceColorNoise => x.ReduceColorNoise(),
            _ => throw new InvalidOperationException(),
        });
        return result.Width;
    }

    /// <summary>Medians of seven samples without BenchmarkDotNet, written as JSON to <paramref name="outputPath"/>.</summary>
    internal static void Probe(string outputPath)
    {
        var results = new List<Result>();
        foreach (Operation op in Enum.GetValues<Operation>())
        {
            var benchmark = new PhotoAdjustBenchmarks { Op = op };
            benchmark.Setup();
            try
            {
                benchmark.Run();
                var timings = new double[7];
                var allocations = new double[7];
                for (int sample = 0; sample < timings.Length; sample++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long allocated = GC.GetTotalAllocatedBytes(true);
                    long start = Stopwatch.GetTimestamp();
                    int count = 0;
                    do { benchmark.Run(); count++; } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 150);
                    timings[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
                    allocations[sample] = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)count;
                }

                Array.Sort(timings);
                Array.Sort(allocations);
                var result = new Result(op.ToString(), timings[3], allocations[3]);
                results.Add(result);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{op},{result.Nanoseconds / 1e6:F1} ms,{result.AllocatedBytes / 1048576:F1} MiB"));
            }
            finally
            {
                benchmark.Cleanup();
            }
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Result(string Operation, double Nanoseconds, double AllocatedBytes);
}
