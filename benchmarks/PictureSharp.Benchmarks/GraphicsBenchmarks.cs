using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using PictureSharp.Processing;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>The 0.6.3 graphics operations on a small graphic and a 12 MP image, each applied to a copy-on-write clone.</summary>
[MemoryDiagnoser]
public class GraphicsBenchmarks
{
    private Image _image = null!;
    private Image _overlay = null!;
    private Image _mask = null!;
    private Image _padded = null!;

    public enum Operation
    {
        CopyOnly,
        Levels,
        Gamma,
        AutoContrast,
        Equalize,
        Histogram,
        FindTrimBounds,
        AutoCrop,
        ApplyMaskAlpha,
        ApplyMaskLuminance,
        DrawNormal,
        DrawMultiply,
        DrawSoftLight,
        DrawMultiplyLinear,
        DrawScreen,
        DrawOverlay,
        DrawDifference,
        DrawColorDodge,
        DrawColorBurn,
        DrawSoftLightLinear,
    }

    [Params("small", "12mp")]
    public string Size { get; set; } = "small";

    // The 0.6.7 SIMD operations; Levels, Histogram and DrawNormal are unchanged controls.
    [Params(Operation.Levels, Operation.Histogram, Operation.FindTrimBounds, Operation.AutoCrop, Operation.ApplyMaskAlpha,
        Operation.ApplyMaskLuminance, Operation.DrawNormal, Operation.DrawMultiply, Operation.DrawScreen, Operation.DrawSoftLight,
        Operation.DrawColorBurn, Operation.DrawMultiplyLinear)]
    public Operation Op { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        using Image source = Image.Load(Path.Combine(FindRoot(), "tests/assets/corpus/src/photo_alpha.png"));
        System.Drawing.Size size = Size == "small" ? source.Size : new(4000, 3000);
        _image = source.Clone(x => x.Resize(size.Width, size.Height, ResamplingFilter.Bicubic));
        _overlay = _image.Clone(x => x.Flip(FlipMode.Horizontal));
        _mask = _image.Clone(x => x.Rotate(RotateMode.Rotate180));
        // Transparent margins of 5% on each side around the content.
        _padded = _image.Clone(x => x.Resize(size.Width * 9 / 10, size.Height * 9 / 10).Pad(size.Width, size.Height));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _image.Dispose();
        _overlay.Dispose();
        _mask.Dispose();
        _padded.Dispose();
    }

    [Benchmark]
    public object Run()
    {
        switch (Op)
        {
            case Operation.Histogram:
                return _image.GetHistogram();
            case Operation.FindTrimBounds:
                return _padded.FindTrimBounds()!;
        }

        using Image result = (Op == Operation.AutoCrop ? _padded : _image).Clone(x => Apply(x));
        return result.Width;
    }

    private void Apply(ImageProcessingContext x)
    {
        _ = Op switch
        {
            Operation.CopyOnly => x.Apply(static _ => { }),
            Operation.Levels => x.Levels(new LevelsOptions { InputBlackNormalized = 20 / 255f, InputWhiteNormalized = 230 / 255f, Gamma = 1.2f }),
            Operation.Gamma => x.Gamma(2.2f),
            Operation.AutoContrast => x.AutoContrast(1f),
            Operation.Equalize => x.Equalize(),
            Operation.AutoCrop => x.AutoCrop(),
            Operation.ApplyMaskAlpha => x.ApplyMask(_mask),
            Operation.ApplyMaskLuminance => x.ApplyMask(_mask, new MaskOptions { Channel = MaskChannel.Luminance }),
            Operation.DrawNormal => x.DrawImage(_overlay, Point.Empty, new DrawImageOptions { Opacity = 0.8f }),
            Operation.DrawMultiply => x.DrawImage(_overlay, Point.Empty, new DrawImageOptions { Opacity = 0.8f, BlendMode = BlendMode.Multiply }),
            Operation.DrawSoftLight => x.DrawImage(_overlay, Point.Empty, new DrawImageOptions { Opacity = 0.8f, BlendMode = BlendMode.SoftLight }),
            Operation.DrawMultiplyLinear => x.DrawImage(_overlay, Point.Empty,
                new DrawImageOptions { Opacity = 0.8f, BlendMode = BlendMode.Multiply, LinearLight = true }),
            Operation.DrawScreen => Draw(x, BlendMode.Screen),
            Operation.DrawOverlay => Draw(x, BlendMode.Overlay),
            Operation.DrawDifference => Draw(x, BlendMode.Difference),
            Operation.DrawColorDodge => Draw(x, BlendMode.ColorDodge),
            Operation.DrawColorBurn => Draw(x, BlendMode.ColorBurn),
            Operation.DrawSoftLightLinear => Draw(x, BlendMode.SoftLight, linearLight: true),
            _ => throw new InvalidOperationException(),
        };
    }

    private ImageProcessingContext Draw(ImageProcessingContext x, BlendMode mode, bool linearLight = false) =>
        x.DrawImage(_overlay, Point.Empty, new DrawImageOptions { Opacity = 0.8f, BlendMode = mode, LinearLight = linearLight });

    internal static void Probe(string outputPath)
    {
        var results = new List<Result>();
        foreach (string size in new[] { "small", "12mp" })
        {
            foreach (Operation op in Enum.GetValues<Operation>())
            {
                var benchmark = new GraphicsBenchmarks { Size = size, Op = op };
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
                        do { benchmark.Run(); count++; } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 150);
                        timings[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
                        allocations[sample] = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)count;
                    }

                    Array.Sort(timings);
                    Array.Sort(allocations);
                    var result = new Result(size, op.ToString(), timings[3], allocations[3]);
                    results.Add(result);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{size},{op},{result.Nanoseconds:F0},{result.AllocatedBytes:F0}"));
                }
                finally
                {
                    benchmark.Cleanup();
                }
            }
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Result(string Size, string Operation, double Nanoseconds, double AllocatedBytes);
}
