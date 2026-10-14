using BenchmarkDotNet.Attributes;
using RasterSharp.Formats.WebP.Lossless;

namespace RasterSharp.Benchmarks;

[MemoryDiagnoser]
public class LosslessPredictorBenchmarks
{
    private uint[] _input = null!;
    private uint[] _pixels = null!;
    private Vp8LTransform _transform = null!;

    [Params(5, 6, 7, 10, 11, 12, 13, -1)]
    public int Mode { get; set; }

    [Params(2, 3, 5)]
    public int Bits { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int width = 1920, height = 1440;
        var random = new Random(43);
        _input = new uint[width * height];
        random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_input.AsSpan()));
        _pixels = new uint[_input.Length];
        int tiles = Vp8LDecoder.SubSampleSize(width, Bits) * Vp8LDecoder.SubSampleSize(height, Bits);
        uint[] modes = new uint[tiles];
        for (int i = 0; i < modes.Length; i++)
        {
            modes[i] = 0xff000000u | (uint)((Mode < 0 ? 10 + i % 3 : Mode) << 8);
        }

        _transform = new Vp8LTransform { XSize = width, YSize = height, Bits = Bits, Data = modes };
    }

    [IterationSetup]
    public void Reset() => _input.CopyTo(_pixels, 0);

    [Benchmark(Baseline = true)]
    public void Scalar() => LosslessTransforms.PredictorInverseTransformScalar(_transform, _pixels);

    [Benchmark]
    public void Auto() => LosslessTransforms.PredictorInverseTransform(_transform, _pixels);
}
