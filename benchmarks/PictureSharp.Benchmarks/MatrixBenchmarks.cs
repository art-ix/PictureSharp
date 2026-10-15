using BenchmarkDotNet.Attributes;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Processing;
using static PictureSharp.Benchmarks.LosslessDecodeBenchmarks;

namespace PictureSharp.Benchmarks;

/// <summary>
/// The frozen phase-10 matrix: complete encoding and decoding of every format and resizing, from memory,
/// for small committed sources, a derived 1600×1200 graphic and optional local photos.
/// Decode operations read bytes written once during setup with the operation's encoder options.
/// </summary>
[MemoryDiagnoser]
public class MatrixBenchmarks
{
    public enum Operation
    {
        WebPLossyEncode,
        WebPLossyEncode1T,
        WebPLossyDecode,
        WebPLossyDecode1T,
        WebPLosslessEncode,
        WebPLosslessEncode1T,
        WebPLosslessDecode,
        WebPLosslessDecode1T,
        PngEncode,
        PngDecode,
        JpegEncode,
        JpegDecode,
        BmpEncode,
        BmpDecode,
        GifEncode,
        GifDecode,
        PcxEncode,
        PcxDecode,
        ResizeLanczosHalf,
        ResizeBicubicDouble,
    }

    private static readonly DecoderOptions SingleThread = new() { MaxDegreeOfParallelism = 1 };

    private Image _image = null!;
    private byte[] _encoded = [];
    private EncoderOptions _options = null!;
    private DecoderOptions _decoderOptions = DecoderOptions.Default;

    public IEnumerable<InputFile> Files()
    {
        string root = FindRoot();
        foreach (string name in new[] { "photo.png", "graphic.png", "photo_alpha.png" })
        {
            yield return new InputFile(Path.Combine(root, "tests", "assets", "corpus", "src", name));
        }

        // Derived input: the palette graphic scaled 8× with nearest neighbour (1600×1200), as in the 0.4.6 matrix.
        yield return new InputFile(Path.Combine(root, "tests", "assets", "corpus", "src", "graphic.png") + GraphicScale);

        string? local = Environment.GetEnvironmentVariable("PICTURESHARP_BENCHMARK_MATRIX_FILES");
        if (!string.IsNullOrWhiteSpace(local))
        {
            foreach (string path in local.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return new InputFile(Path.GetFullPath(path));
            }
        }
    }

    private const string GraphicScale = "#8x";

    [ParamsSource(nameof(Files))]
    public InputFile File { get; set; } = null!;

    [ParamsAllValues]
    public Operation Op { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _image = LoadInput(File.Path);
        bool singleThread = Op.ToString().EndsWith("1T", StringComparison.Ordinal);
        _decoderOptions = singleThread ? SingleThread : DecoderOptions.Default;
        _options = Op switch
        {
            Operation.WebPLossyEncode or Operation.WebPLossyDecode => new WebPEncoderOptions(),
            Operation.WebPLossyEncode1T or Operation.WebPLossyDecode1T => new WebPEncoderOptions { MaxDegreeOfParallelism = 1 },
            Operation.WebPLosslessEncode or Operation.WebPLosslessDecode => new WebPEncoderOptions { Lossless = true },
            Operation.WebPLosslessEncode1T or Operation.WebPLosslessDecode1T =>
                new WebPEncoderOptions { Lossless = true, MaxDegreeOfParallelism = 1 },
            Operation.PngEncode or Operation.PngDecode => new PngEncoderOptions(),
            Operation.JpegEncode or Operation.JpegDecode => new JpegEncoderOptions(),
            Operation.BmpEncode or Operation.BmpDecode => new BmpEncoderOptions(),
            Operation.GifEncode or Operation.GifDecode => new GifEncoderOptions(),
            Operation.PcxEncode or Operation.PcxDecode => new PcxEncoderOptions(),
            _ => new PngEncoderOptions(),
        };

        if (Op.ToString().Contains("Decode", StringComparison.Ordinal))
        {
            _encoded = _image.Encode(_options);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public int Run()
    {
        switch (Op)
        {
            case Operation.ResizeLanczosHalf:
                {
                    using Image resized = _image.Clone(x => x.Resize(Math.Max(1, _image.Width / 2), Math.Max(1, _image.Height / 2), ResamplingFilter.Lanczos3));
                    return resized.Width;
                }
            case Operation.ResizeBicubicDouble:
                {
                    using Image resized = _image.Clone(x => x.Resize(_image.Width * 2, _image.Height * 2, ResamplingFilter.Bicubic));
                    return resized.Width;
                }
            case var op when op.ToString().Contains("Decode", StringComparison.Ordinal):
                {
                    using Image image = Image.Load(_encoded, _decoderOptions);
                    return image.Width;
                }
            default:
                return _image.Encode(_options).Length;
        }
    }

    internal static Image LoadInput(string path)
    {
        if (!path.EndsWith(GraphicScale, StringComparison.Ordinal))
        {
            return Image.Load(path);
        }

        using Image source = Image.Load(path[..^GraphicScale.Length]);
        return source.Clone(x => x.Resize(source.Width * 8, source.Height * 8, ResamplingFilter.Nearest));
    }
}
