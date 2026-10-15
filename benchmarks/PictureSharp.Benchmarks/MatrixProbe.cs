using System.Diagnostics;
using System.Text.Json;

namespace PictureSharp.Benchmarks;

/// <summary>Lightweight regression measurements over the existing complete-operation matrix.</summary>
internal static class MatrixProbe
{
    public static void Run(string outputPath)
    {
        var results = new List<Result>();
        foreach (LosslessDecodeBenchmarks.InputFile file in new MatrixBenchmarks().Files())
        {
            foreach (MatrixBenchmarks.Operation op in Enum.GetValues<MatrixBenchmarks.Operation>())
            {
                var benchmark = new MatrixBenchmarks { File = file, Op = op };
                benchmark.Setup();
                try
                {
                    benchmark.Run();
                    benchmark.Run();
                    var timings = new double[7];
                    var allocations = new double[7];
                    for (int sample = 0; sample < timings.Length; sample++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        long allocated = GC.GetTotalAllocatedBytes(true);
                        long start = Stopwatch.GetTimestamp();
                        int count = 0;
                        do
                        {
                            benchmark.Run();
                            count++;
                        }
                        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 120);
                        timings[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
                        allocations[sample] = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)count;
                    }

                    Array.Sort(timings);
                    Array.Sort(allocations);
                    results.Add(new(file.Path, op.ToString(), timings[3], allocations[3]));
                    Console.WriteLine($"{Path.GetFileName(file.Path)},{op},{timings[3]:F0},{allocations[3]:F0}");
                    File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally
                {
                    benchmark.Cleanup();
                }
            }
        }
    }

    private sealed record Result(string File, string Operation, double Nanoseconds, double AllocatedBytes);
}
