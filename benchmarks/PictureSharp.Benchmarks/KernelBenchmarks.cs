using System.Drawing;
using BenchmarkDotNet.Attributes;
using PictureSharp.Processing;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>
/// The kernels and composition of 0.9.0 step 2 on a 3 MP image with alpha, each applied to a copy-on-write clone.
/// <see cref="Depth"/> 8 is the regression control for the 8-bit ports; 16 measures the high-precision paths.
/// </summary>
[MemoryDiagnoser]
public class KernelBenchmarks
{
    private Image _image = null!;
    private Image _overlay = null!;

    public enum Operation
    {
        ResizeLanczosHalf,
        ResizeLinear,
        RotateBicubic,
        GaussianBlur,
        BoxBlur,
        Sharpen,
        UnsharpMask,
        Vignette,
        DrawNormal,
        DrawMultiply,
        ApplyMask,
        Opacity,
        Fill,
        BackgroundColor,
    }

    [Params(8, 16)]
    public int Depth { get; set; } = 8;

    [ParamsAllValues]
    public Operation Op { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        using Image source = Image.Load(Path.Combine(FindRoot(), "tests/assets/corpus/src/photo_alpha.png"));
        _image = source.Clone(x => x.Resize(2000, 1500, ResamplingFilter.Bicubic));
        _overlay = _image.Clone(x => x.Flip(FlipMode.Horizontal));
        if (Depth == 16)
        {
            _image.Mutate(x => x.ConvertPixelType(PixelType.Rgba64));
            _overlay.Mutate(x => x.ConvertPixelType(PixelType.Rgba64));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _image.Dispose();
        _overlay.Dispose();
    }

    [Benchmark]
    public int Run()
    {
        using Image result = _image.Clone(x => _ = Op switch
        {
            Operation.ResizeLanczosHalf => x.Resize(1000, 750),
            Operation.ResizeLinear => x.Resize(new ResizeOptions { Size = new(1000, 750), LinearLight = true }),
            Operation.RotateBicubic => x.Rotate(17f),
            Operation.GaussianBlur => x.GaussianBlur(3f),
            Operation.BoxBlur => x.BoxBlur(3f),
            Operation.Sharpen => x.Sharpen(),
            Operation.UnsharpMask => x.UnsharpMask(),
            Operation.Vignette => x.Vignette(0.6f),
            Operation.DrawNormal => x.DrawImage(_overlay, Point.Empty, 0.8f),
            Operation.DrawMultiply => x.DrawImage(_overlay, Point.Empty, new DrawImageOptions { Opacity = 0.8f, BlendMode = BlendMode.Multiply }),
            Operation.ApplyMask => x.ApplyMask(_overlay),
            Operation.Opacity => x.Opacity(0.6f),
            Operation.Fill => x.Fill(new Rgba32(10, 20, 30, 200)),
            Operation.BackgroundColor => x.BackgroundColor(new Rgba32(240, 230, 220)),
            _ => throw new InvalidOperationException(),
        });
        return result.Width;
    }
}
