using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>Metadata-only operations compared with decoding and re-encoding the same synthetic photo.</summary>
[MemoryDiagnoser]
public class MetadataBenchmarks
{
    private byte[] _data = null!;
    private EncoderOptions _options = null!;

    [Params("jpeg", "png", "webp")]
    public string Format { get; set; } = "jpeg";

    [GlobalSetup]
    public void Setup()
    {
        string root = FindRoot();
        using Image source = Image.Load(Path.Combine(root, "tests/assets/corpus/src/photo.png"));
        source.Metadata.Exif = System.IO.File.ReadAllBytes(Path.Combine(root, "tests/assets/corpus/metadata/little.exif"));
        _options = Format switch { "jpeg" => new JpegEncoderOptions(), "png" => new PngEncoderOptions(), _ => new WebPEncoderOptions { Lossless = true } };
        _data = source.Encode(_options);
    }

    [Benchmark]
    public ImageMetadata ReadMetadata() => Image.ReadMetadata(_data);

    [Benchmark]
    public byte[] EditMetadata() => Image.EditMetadata(_data, m => m.ExifProfile.RemoveGps());

    [Benchmark]
    public int Decode()
    {
        using Image image = Image.Load(_data);
        return image.Width;
    }

    [Benchmark]
    public byte[] DecodeEncode()
    {
        using Image image = Image.Load(_data);
        image.Metadata.ExifProfile.RemoveGps();
        return image.Encode(_options);
    }

    internal static void Probe(string outputPath)
    {
        var results = new List<Result>();
        foreach (string format in new[] { "jpeg", "png", "webp" })
        {
            var benchmark = new MetadataBenchmarks { Format = format };
            benchmark.Setup();
            foreach (var (name, run) in new (string, Action)[]
            {
                ("ReadMetadata", () => benchmark.ReadMetadata()), ("EditMetadata", () => benchmark.EditMetadata()),
                ("Decode", () => benchmark.Decode()), ("DecodeEncode", () => benchmark.DecodeEncode()),
            })
            {
                run(); run();
                var timings = new double[7];
                var allocations = new double[7];
                for (int sample = 0; sample < timings.Length; sample++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long allocated = GC.GetTotalAllocatedBytes(true);
                    long start = Stopwatch.GetTimestamp();
                    int count = 0;
                    do { run(); count++; } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 150);
                    timings[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
                    allocations[sample] = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)count;
                }
                Array.Sort(timings); Array.Sort(allocations);
                var result = new Result(format, name, timings[3], allocations[3]);
                results.Add(result);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{format},{name},{result.Nanoseconds:F0},{result.AllocatedBytes:F0}"));
            }
        }
        System.IO.File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Result(string Format, string Operation, double Nanoseconds, double AllocatedBytes);
}
