using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using PictureSharp.Processing;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>
/// Loading with the gain map, HDR reconstruction and tone mapping of a JPEG with an ISO 21496-1 gain map at a quarter
/// of the base resolution, assembled in memory (the corpus files are too small and the iPhone samples are private).
/// </summary>
[MemoryDiagnoser]
public class HdrBenchmarks
{
    private static readonly DecoderOptions LoadOptions = new() { LoadGainMap = true };

    private byte[] _file = null!;
    private Image _image = null!;
    private HdrImage _hdr = null!;

    public enum Operation
    {
        Load,
        ToHdr,
        ToHdrSingleThread,
        ToneMap,
    }

    public enum MapKind
    {
        /// <summary>One channel, gamma 1.</summary>
        IsoMono,

        /// <summary>Three channels, gamma ≠ 1 and a negative minimum, as iPhone maps.</summary>
        IsoRgbGamma,
    }

    [Params("3mp", "12mp")]
    public string Size { get; set; } = "3mp";

    [Params(MapKind.IsoMono, MapKind.IsoRgbGamma)]
    public MapKind Map { get; set; }

    [Params(Operation.ToHdr, Operation.ToHdrSingleThread, Operation.ToneMap)]
    public Operation Op { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        (int width, int height) = Size == "3mp" ? (2000, 1500) : (4000, 3000);
        _file = BuildFile(width, height, Map);
        _image = Image.Load(_file, LoadOptions);
        _hdr = _image.ToHdr();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hdr.Dispose();
        _image.Dispose();
    }

    [Benchmark]
    public object Run()
    {
        switch (Op)
        {
            case Operation.Load:
            {
                using Image image = Image.Load(_file, LoadOptions);
                return image.Width;
            }

            case Operation.ToHdr:
            {
                using HdrImage hdr = _image.ToHdr();
                return hdr.Headroom;
            }

            case Operation.ToHdrSingleThread:
            {
                using HdrImage hdr = _image.ToHdr(new HdrOptions { MaxDegreeOfParallelism = 1 });
                return hdr.Headroom;
            }

            default:
            {
                using Image sdr = _hdr.ToneMap();
                return sdr.Width;
            }
        }
    }

    /// <summary>A photo-like base and a smooth gain map with two stops of headroom.</summary>
    internal static byte[] BuildFile(int width, int height, MapKind kind)
    {
        using Image photo = Image.Load(Path.Combine(FindRoot(), "tests/assets/corpus/src/photo_alpha.png"));
        using Image source = photo.Clone(x => x.Resize(width, height, ResamplingFilter.Bicubic));
        byte[] baseJpeg = source.Encode(ImageFormat.Jpeg);

        int mapWidth = width / 4, mapHeight = height / 4;
        var mapPixels = new Rgba32[mapWidth * mapHeight];
        for (int y = 0; y < mapHeight; y++)
        {
            for (int x = 0; x < mapWidth; x++)
            {
                byte r = (byte)(x * 255 / Math.Max(1, mapWidth - 1));
                byte g = (byte)(y * 255 / Math.Max(1, mapHeight - 1));
                byte b = (byte)((x ^ y) & 255);
                mapPixels[(y * mapWidth) + x] = kind == MapKind.IsoMono ? new Rgba32(r, r, r, 255) : new Rgba32(r, g, b, 255);
            }
        }

        using Image mapImage = Image.LoadPixelData(mapPixels, mapWidth, mapHeight);
        byte[] mapJpeg = mapImage.Encode(ImageFormat.Jpeg);

        // ISO 21496-1: version 0, flags (multi-channel, base colour space), headroom 0 → 2, then per channel
        // min, max, gamma, base offset, alternate offset as fractions.
        bool rgb = kind == MapKind.IsoRgbGamma;
        List<byte> iso = [.. "urn:iso:std:iso:ts:21496:-1"u8, 0, 0, 0, 0, 0, (byte)(rgb ? 0xC0 : 0x40)];
        iso.AddRange([.. U32(0), .. U32(1), .. U32(2), .. U32(1)]);
        for (int c = 0; c < (rgb ? 3 : 1); c++)
        {
            iso.AddRange(rgb
                ? [.. U32(unchecked((uint)-50)), .. U32(100), .. U32(2), .. U32(1), .. U32((uint)(85 + c)), .. U32(100), .. U32(1), .. U32(64), .. U32(1), .. U32(64)]
                : [.. U32(0), .. U32(1), .. U32(2), .. U32(1), .. U32(1), .. U32(1), .. U32(0), .. U32(1), .. U32(0), .. U32(1)]);
        }

        byte[] map = [.. mapJpeg.AsSpan(0, 2), .. Segment(0xE2, [.. iso]), .. mapJpeg.AsSpan(2)];
        byte[] Mpf(uint primarySize, uint mapOffset) => Segment(0xE2, [.. "MPF\0MM\0*"u8, .. U32(8), 0, 1, 0xB0, 0x02, 0, 7, .. U32(32), .. U32(26), .. U32(0u),
            .. U32(0x030000), .. U32(primarySize), .. U32(0), 0, 0, 0, 0, .. U32(0), .. U32((uint)map.Length), .. U32(mapOffset), 0, 0, 0, 0]);
        int primaryLength = baseJpeg.Length + Mpf(0, 0).Length;
        return [.. baseJpeg.AsSpan(0, 2), .. Mpf((uint)primaryLength, (uint)(primaryLength - 10)), .. baseJpeg.AsSpan(2), .. map];

        static byte[] Segment(byte marker, ReadOnlySpan<byte> body) => [0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];
        static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }

    internal static void Probe(string outputPath)
    {
        var results = new List<Result>();
        foreach (string size in new[] { "3mp", "12mp" })
        {
            foreach (MapKind map in Enum.GetValues<MapKind>())
            {
                foreach (Operation op in Enum.GetValues<Operation>())
                {
                    var benchmark = new HdrBenchmarks { Size = size, Map = map, Op = op };
                    benchmark.Setup();
                    try
                    {
                        benchmark.Run();
                        benchmark.Run();
                        var timings = new double[7];
                        var allocations = new double[7];
                        for (int sample = 0; sample < timings.Length; sample++)
                        {
                            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                            long allocated = GC.GetTotalAllocatedBytes(true);
                            long start = Stopwatch.GetTimestamp();
                            int count = 0;
                            do { benchmark.Run(); count++; } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 300);
                            timings[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
                            allocations[sample] = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)count;
                        }

                        Array.Sort(timings);
                        Array.Sort(allocations);
                        var result = new Result(size, map.ToString(), op.ToString(), timings[3], allocations[3]);
                        results.Add(result);
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{size},{map},{op},{result.Nanoseconds / 1e6:F2} ms,{result.AllocatedBytes:F0}"));
                    }
                    finally
                    {
                        benchmark.Cleanup();
                    }
                }
            }
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Result(string Size, string Map, string Operation, double Nanoseconds, double AllocatedBytes);
}
