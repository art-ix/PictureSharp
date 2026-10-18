using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PictureSharp.Formats.WebP;

namespace PictureSharp.Benchmarks;

/// <summary>
/// Lossy WebP encode (q75, m4) and decode from memory, one thread and default, measured as the native libwebp 1.6.0
/// reference program: median of 21 calls (7 above 4 MP) after at least
/// 50 warm-up calls and 1 s (at most 3 s), so the measured code is fully tiered. Includes the returned <see cref="Image"/>.
/// </summary>
/// <remarks><c>webp-probe &lt;output.json&gt; &lt;image&gt;...</c>; with <c>PICTURESHARP_WEBP_PROBE_OUT</c> set, the encoded
/// bytes are written there as <c>&lt;name&gt;.webp</c> for comparison with libwebp.</remarks>
internal static class WebPProbe
{
    public static void Run(string outputPath, IEnumerable<string> files)
    {
        var results = new List<Result>();
        string? encodedDirectory = Environment.GetEnvironmentVariable("PICTURESHARP_WEBP_PROBE_OUT");
        foreach (string path in files)
        {
            using Image image = Image.Load(path);
            int runs = (long)image.Width * image.Height > 4_000_000 ? 7 : 21;
            string name = Path.GetFileNameWithoutExtension(path);
            foreach (int threads in new[] { 1, -1 })
            {
                var options = new WebPEncoderOptions { MaxDegreeOfParallelism = threads };
                var decoderOptions = new DecoderOptions { MaxDegreeOfParallelism = threads };
                byte[] encoded = image.Encode(options);
                if (encodedDirectory is not null && threads == 1)
                {
                    File.WriteAllBytes(Path.Combine(encodedDirectory, name + ".webp"), encoded);
                }

                string suffix = threads == 1 ? "1T" : "";
                results.Add(Measure(name, "LossyEncode" + suffix, runs, () => image.Encode(options).Length));
                results.Add(Measure(name, "LossyDecode" + suffix, runs, () =>
                {
                    using Image decoded = Image.Load(encoded, decoderOptions);
                    return decoded.Width;
                }));
            }

            File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static Result Measure(string name, string operation, int runs, Func<int> action)
    {
        // At least 50 calls and 1 s (at most 3 s): the JIT promotes methods to optimized code only after a 100 ms
        // pause in new compilations, so 50 short calls could leave paths that this input reaches first unoptimized.
        long warmup = Stopwatch.GetTimestamp();
        for (int i = 0; Stopwatch.GetElapsedTime(warmup).TotalSeconds < 3 && (i < 50 || Stopwatch.GetElapsedTime(warmup).TotalMilliseconds < 1000); i++)
        {
            action();
        }

        var timings = new double[runs];
        long allocated = 0;
        int gen2 = GC.CollectionCount(2);
        for (int i = 0; i < runs; i++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            long start = Stopwatch.GetTimestamp();
            action();
            timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated += GC.GetTotalAllocatedBytes(true) - before;
        }

        Array.Sort(timings);
        var result = new Result(name, operation, timings[runs / 2], timings[0], allocated / runs, GC.CollectionCount(2) - gen2);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name},{operation},{result.MedianMs:F3},{result.MinMs:F3},{result.AllocatedBytes},{result.Gen2Collections}"));
        return result;
    }

    private sealed record Result(string File, string Operation, double MedianMs, double MinMs, long AllocatedBytes, int Gen2Collections);
}
