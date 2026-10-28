using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using PictureSharp.Processing;

namespace PictureSharp.Benchmarks;

/// <summary>
/// Finding, measuring and developing a photographed 35 mm strip (0.7.13) at 4000 × 2750 pixels: a light box, two rows
/// of perforation, the frame and its neighbors, tilted by 1.5°.
/// </summary>
[MemoryDiagnoser]
public class FilmNegativeBenchmarks
{
    private Image _image = null!;
    private FilmFrame _frame = null!;
    private NegativeCalibration _calibration = null!;

    public enum Operation
    {
        FindFilmFrame,
        AnalyzeNegative,
        DevelopKnownFrame,
        DevelopNegative,
    }

    [ParamsAllValues]
    public Operation Op { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _image = Strip(4000, 2750);
        _image.Mutate(x => x.Rotate(1.5f, ResamplingFilter.Bicubic, new Rgba32(230, 240, 255, 255)));
        NegativeAnalysis analysis = _image.AnalyzeNegative();
        _frame = analysis.Frame ?? throw new InvalidOperationException("No frame in the benchmark strip.");
        _calibration = analysis.Calibration;
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public int Run()
    {
        switch (Op)
        {
            case Operation.FindFilmFrame:
                return _image.FindFilmFrame()!.Bounds.Width;
            case Operation.AnalyzeNegative:
                return (int)_image.AnalyzeNegative(new NegativeOptions { Frame = _frame }).ClippedFraction.Red;
            default:
                var options = Op == Operation.DevelopKnownFrame ? new NegativeOptions { Frame = _frame, Calibration = _calibration } : null;
                using (Image result = _image.Clone(x => x.DevelopNegative(options)))
                {
                    return result.Width;
                }
        }
    }

    // Pitch = width / 16; the frame is 36 × 24 mm in the center, holes 2.8 × 2 mm, the picture a smooth pattern.
    internal static Image Strip(int width, int height)
    {
        float mm = width / 16f / 4.75f, top = (height - (35 * mm)) / 2, cx = width / 2f;
        var image = new Image(width, height, new Rgba32(230, 240, 255, 255));
        for (int y = 0; y < height; y++)
        {
            float fy = y + 0.5f - top;
            if (fy < 0 || fy >= 35 * mm)
            {
                continue;
            }

            for (int x = 0; x < width; x++)
            {
                float fx = x + 0.5f - cx, pitch = 4.75f * mm;
                float along = (fx / pitch) - MathF.Round(fx / pitch);
                if (MathF.Abs(along * pitch) < 1.4f * mm && (MathF.Abs(fy - (3 * mm)) < mm || MathF.Abs(fy - (32 * mm)) < mm))
                {
                    continue;
                }

                float inFrame = fx - (MathF.Round(fx / (38 * mm)) * 38 * mm);
                bool picture = MathF.Abs(inFrame) < 18 * mm && fy >= 5.5f * mm && fy < 29.5f * mm;
                float shade = 0.5f + (0.5f * MathF.Sin(x / 230f) * MathF.Cos(y / 170f));
                image[x, y] = picture
                    ? new Rgba32((byte)(70 + (60 * shade)), (byte)(30 + (40 * shade)), (byte)(15 + (25 * shade)), 255)
                    : new Rgba32(200, 120, 70, 255);
            }
        }

        return image;
    }

    /// <summary>Medians of seven samples without BenchmarkDotNet, written as JSON to <paramref name="outputPath"/>.</summary>
    internal static void Probe(string outputPath)
    {
        var results = new List<Result>();
        foreach (Operation op in Enum.GetValues<Operation>())
        {
            var benchmark = new FilmNegativeBenchmarks { Op = op };
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
