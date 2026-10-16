using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;

namespace PictureSharp.Benchmarks;

/// <summary>
/// Load → Resize to half → Save of a JPEG with an ISO 21496-1 gain map at a quarter of the base resolution (0.6.9),
/// with the map kept (<see cref="JpegGainMapMode.Preserve"/>) and as plain SDR (map not loaded).
/// </summary>
[MemoryDiagnoser]
public class HdrEditBenchmarks
{
    private byte[] _file = null!;

    [Params("12mp", "48mp")]
    public string Size { get; set; } = "12mp";

    [Params(false, true)]
    public bool KeepGainMap { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        (int width, int height) = Size == "12mp" ? (4000, 3000) : (8000, 6000);
        _file = HdrBenchmarks.BuildFile(width, height, HdrBenchmarks.MapKind.IsoRgbGamma);
    }

    [Benchmark]
    public int Run()
    {
        using Image image = Image.Load(_file, new DecoderOptions { LoadGainMap = KeepGainMap });
        image.Mutate(x => x.Resize(image.Width / 2, image.Height / 2));
        return image.ToJpeg(new JpegEncoderOptions { Quality = 90, GainMap = KeepGainMap ? JpegGainMapMode.Require : JpegGainMapMode.None }).Length;
    }

    internal static void Probe(string outputPath)
    {
        var results = new List<Result>();
        foreach (string size in new[] { "12mp", "48mp" })
        {
            foreach (bool keep in new[] { false, true })
            {
                var benchmark = new HdrEditBenchmarks { Size = size, KeepGainMap = keep };
                benchmark.Setup();
                int length = benchmark.Run();
                benchmark.Run();
                benchmark.Run();
                var timings = new double[9];
                var allocations = new double[9];
                for (int sample = 0; sample < timings.Length; sample++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long allocated = GC.GetTotalAllocatedBytes(true);
                    long start = Stopwatch.GetTimestamp();
                    benchmark.Run();
                    timings[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                    allocations[sample] = GC.GetTotalAllocatedBytes(true) - allocated;
                }

                Array.Sort(timings);
                Array.Sort(allocations);
                var result = new Result(size, keep, timings[4], allocations[4], length);
                results.Add(result);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{size},{(keep ? "gain map" : "sdr")},{result.Nanoseconds / 1e6:F1} ms,{result.AllocatedBytes / 1048576:F1} MiB,{length} bytes"));
            }
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Result(string Size, bool KeepGainMap, double Nanoseconds, double AllocatedBytes, int OutputBytes);
}
