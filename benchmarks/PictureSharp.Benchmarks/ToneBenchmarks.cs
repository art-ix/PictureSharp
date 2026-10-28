using BenchmarkDotNet.Attributes;
using PictureSharp.Processing;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>
/// The tone and color operations of 0.9.0 step 3 on a 3 MP image with alpha, each applied to a copy-on-write clone.
/// <see cref="Depth"/> 8 is the regression control for the 8-bit ports; 16 measures the high-precision paths.
/// </summary>
[MemoryDiagnoser]
public class ToneBenchmarks
{
    private Image _image = null!;

    public enum Operation
    {
        Brightness,
        Contrast,
        Gamma,
        Exposure,
        Levels,
        Curves,
        Invert,
        Grayscale,
        Sepia,
        Saturate,
        Vibrance,
        WhiteBalance,
        WhiteBalanceNeutral,
        AutoWhiteBalance,
        AutoContrast,
        Equalize,
        ShadowsHighlights,
        Clarity,
        MakeTransparent,
        Histogram,
        AutoCrop,
        UnsharpMask,
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
        if (Depth == 16)
        {
            _image.Mutate(x => x.ConvertPixelType(PixelType.Rgba64));
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public long Run()
    {
        if (Op == Operation.Histogram)
        {
            return _image.GetHistogram().PixelCount;
        }

        using Image result = _image.Clone(x => _ = Op switch
        {
            Operation.Brightness => x.Brightness(1.2f),
            Operation.Contrast => x.Contrast(1.3f),
            Operation.Gamma => x.Gamma(1.4f),
            Operation.Exposure => x.Exposure(0.5f),
            Operation.Levels => x.Levels(new LevelsOptions { InputBlackNormalized = 10 / 255f, InputWhiteNormalized = 240 / 255f, Gamma = 1.2f }),
            Operation.Curves => x.CurvesNormalized([new(0, 0), new(64 / 255f, 52 / 255f), new(192 / 255f, 206 / 255f), new(1, 1)]),
            Operation.Invert => x.Invert(),
            Operation.Grayscale => x.Grayscale(),
            Operation.Sepia => x.Sepia(),
            Operation.Saturate => x.Saturate(1.4f),
            Operation.Vibrance => x.Vibrance(0.5f),
            Operation.WhiteBalance => x.WhiteBalance(0.3f, -0.1f),
            Operation.WhiteBalanceNeutral => x.WhiteBalance(new Rgba32(200, 180, 160)),
            Operation.AutoWhiteBalance => x.AutoWhiteBalance(),
            Operation.AutoContrast => x.AutoContrast(1f),
            Operation.Equalize => x.Equalize(),
            Operation.ShadowsHighlights => x.ShadowsHighlights(0.4f, -0.3f),
            Operation.Clarity => x.Clarity(0.5f),
            Operation.MakeTransparent => x.MakeTransparent(new Rgba32(255, 255, 255)),
            Operation.AutoCrop => x.AutoCrop(new AutoCropOptions { Mode = AutoCropMode.Background, ToleranceNormalized = 8 / 255f }),
            Operation.UnsharpMask => x.UnsharpMask(),
            _ => throw new InvalidOperationException(),
        });
        return result.Width;
    }
}
