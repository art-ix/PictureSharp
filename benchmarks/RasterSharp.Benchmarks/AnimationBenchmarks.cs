using BenchmarkDotNet.Attributes;
using static RasterSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace RasterSharp.Benchmarks;

/// <summary>
/// Animated WebP (lossy and lossless), GIF and APNG encoding and decoding of deterministic 16-frame pans
/// over committed corpus sources (opaque and with alpha). Decode operations read bytes written once during setup.
/// </summary>
[MemoryDiagnoser]
public class AnimationBenchmarks
{
    public enum Operation
    {
        WebPLossyEncode,
        WebPLossyDecode,
        WebPLosslessEncode,
        WebPLosslessDecode,
        GifEncode,
        GifDecode,
        ApngEncode,
        ApngDecode,
    }

    private const int FrameCount = 16;

    private Image _animation = null!;
    private EncoderOptions _options = null!;
    private byte[] _encoded = [];

    public IEnumerable<string> Sources() => ["photo.png", "photo_alpha.png"];

    [ParamsSource(nameof(Sources))]
    public string Source { get; set; } = null!;

    [ParamsAllValues]
    public Operation Op { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _animation = CreatePan(Path.Combine(FindRoot(), "tests", "assets", "corpus", "src", Source));
        _options = Op switch
        {
            Operation.WebPLossyEncode or Operation.WebPLossyDecode => new WebPEncoderOptions(),
            Operation.WebPLosslessEncode or Operation.WebPLosslessDecode => new WebPEncoderOptions { Lossless = true },
            Operation.GifEncode or Operation.GifDecode => new GifEncoderOptions(),
            _ => new PngEncoderOptions(),
        };

        if (Op.ToString().EndsWith("Decode", StringComparison.Ordinal))
        {
            _encoded = _animation.Encode(_options);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _animation.Dispose();

    [Benchmark]
    public int Run()
    {
        if (!Op.ToString().EndsWith("Decode", StringComparison.Ordinal))
        {
            return _animation.Encode(_options).Length;
        }

        using Image image = Image.Load(_encoded);
        return image.FrameCount;
    }

    // The source doubled with bicubic resampling; each frame is a window shifted by (8, 4) pixels.
    internal static Image CreatePan(string path)
    {
        using Image source = Image.Load(path);
        using Image large = source.Clone(x => x.Resize(source.Width * 2, source.Height * 2, ResamplingFilter.Bicubic));
        int width = large.Width - (8 * FrameCount);
        int height = large.Height - (4 * FrameCount);
        Image animation = large.Clone(x => x.Crop(0, 0, width, height));
        animation.Frames.RootFrame.Duration = TimeSpan.FromMilliseconds(80);
        for (int i = 1; i < FrameCount; i++)
        {
            using Image frame = large.Clone(x => x.Crop(8 * i, 4 * i, width, height));
            animation.Frames.Add(frame, TimeSpan.FromMilliseconds(80));
        }

        return animation;
    }
}
