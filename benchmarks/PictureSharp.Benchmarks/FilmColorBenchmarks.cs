using BenchmarkDotNet.Attributes;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>
/// The operations of 0.9.0 step 4 — film, cleanup, color management and HDR — at 3 MP (the strip at 2000 × 1375).
/// <see cref="Depth"/> 8 is the regression control for the 8-bit paths; 16 measures the high-precision ones. Mutating
/// operations run on copy-on-write clones.
/// </summary>
[MemoryDiagnoser]
public class FilmColorBenchmarks
{
    private static readonly NegativeCalibration Calibration = new() { FilmBase = new(0.8f, 0.5f, 0.3f), DensityRange = new(2f, 2.2f, 1.8f) };

    private Image _photo = null!;
    private Image _strip = null!;
    private Image _profiled = null!;
    private Image _withMap = null!;
    private HdrImage _hdr = null!;
    private FilmFrame _frame = null!;
    private NegativeCalibration _stripCalibration = null!;
    private byte[] _profiledPng = null!;

    public enum Operation
    {
        InvertNegative,
        InvertNegativeGray,
        AnalyzeNegative,
        FindFilmFrame,
        DevelopKnownFrame,
        RemoveDust,
        Denoise,
        ReduceColorNoise,
        ConvertToSrgb,
        LoadToSrgb,
        ToHdr,
        ToGainMapImage,
    }

    [Params(8, 16)]
    public int Depth { get; set; } = 8;

    [ParamsAllValues]
    public Operation Op { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        using Image source = Image.Load(Path.Combine(FindRoot(), "tests/assets/corpus/src/photo_alpha.png"));
        _photo = source.Clone(x => x.Resize(2000, 1500, ResamplingFilter.Bicubic));
        _strip = FilmNegativeBenchmarks.Strip(2000, 1375);
        _strip.Mutate(x => x.Rotate(1.5f, ResamplingFilter.Bicubic, new Rgba32(230, 240, 255, 255)));
        byte[] p3 = File.ReadAllBytes(Path.Combine(FindRoot(), "tests/assets/corpus/color/display_p3_v4_para3.icc"));
        _profiled = _photo.Clone(x => x.Apply(static _ => { }));
        _profiled.Metadata.IccProfile = p3;
        _hdr = Hdr(_photo);
        if (Depth == 16)
        {
            foreach (Image image in new[] { _photo, _strip, _profiled })
            {
                image.Mutate(x => x.ConvertPixelType(PixelType.Rgba64));
            }
        }

        _profiledPng = _profiled.ToPng();
        NegativeAnalysis analysis = _strip.AnalyzeNegative();
        _frame = analysis.Frame ?? throw new InvalidOperationException("No frame in the benchmark strip.");
        _stripCalibration = analysis.Calibration;
        using Image sdr = _hdr.ToneMap();
        _withMap = _hdr.ToGainMapImage(sdr);
        if (Depth == 16)
        {
            _withMap.Mutate(x => x.ConvertPixelType(PixelType.Rgba64));
        }
    }

    // The photo in linear light, brightened up to 8× towards the right.
    private static HdrImage Hdr(Image photo)
    {
        Rgba32[] pixels = photo.GetPixelSpan<Rgba32>().ToArray();
        var linear = new Rgba128[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            float gain = 1f + (7f * (i % photo.Width) / photo.Width);
            Rgba32 p = pixels[i];
            linear[i] = new Rgba128(Decode(p.R) * gain, Decode(p.G) * gain, Decode(p.B) * gain, p.A / 255f);
        }

        return new HdrImage(photo.Width, photo.Height, linear, HdrColorSpace.LinearSrgb, 8f);

        static float Decode(byte v) => v / 255f <= 0.04045f ? v / 255f / 12.92f : MathF.Pow(((v / 255f) + 0.055f) / 1.055f, 2.4f);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (IDisposable disposable in new IDisposable[] { _photo, _strip, _profiled, _withMap, _hdr })
        {
            disposable.Dispose();
        }
    }

    [Benchmark]
    public object Run()
    {
        switch (Op)
        {
            case Operation.AnalyzeNegative:
                return _strip.AnalyzeNegative(new NegativeOptions { Frame = _frame }).Calibration.FilmBase.Red;
            case Operation.FindFilmFrame:
                return _strip.FindFilmFrame()!.Bounds.Width;
            case Operation.LoadToSrgb:
            {
                using Image loaded = Image.Load(_profiledPng, new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
                return loaded.Width;
            }

            case Operation.ToHdr:
            {
                using HdrImage hdr = _withMap.ToHdr();
                return hdr.Headroom;
            }

            case Operation.ToGainMapImage:
            {
                using Image result = _hdr.ToGainMapImage(_withMap);
                return result.Width;
            }

            case Operation.DevelopKnownFrame:
            {
                using Image result = _strip.Clone(x => x.DevelopNegative(new NegativeOptions { Frame = _frame, Calibration = _stripCalibration }));
                return result.Width;
            }

            default:
            {
                using Image result = (Op == Operation.ConvertToSrgb ? _profiled : _photo).Clone(x => _ = Op switch
                {
                    Operation.InvertNegative => x.InvertNegative(Calibration),
                    Operation.InvertNegativeGray => x.InvertNegative(Calibration with { FilmType = FilmType.BlackAndWhiteNegative }),
                    Operation.RemoveDust => x.RemoveDust(6),
                    Operation.Denoise => x.Denoise(0.5f),
                    Operation.ReduceColorNoise => x.ReduceColorNoise(),
                    Operation.ConvertToSrgb => x.ConvertToSrgb(),
                    _ => throw new InvalidOperationException(),
                });
                return result.Width;
            }
        }
    }
}
