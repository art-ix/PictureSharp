using BenchmarkDotNet.Attributes;
using RasterSharp;

namespace RasterSharp.Benchmarks;

[MemoryDiagnoser]
public class PixelConversionBenchmarks
{
    private Image _image = null!;
    private byte[] _buffer = null!;

    [Params(PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Bgra32Premultiplied)]
    public PixelFormat Format { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _image = new Image(1920, 1080, System.Drawing.Color.CornflowerBlue);
        _buffer = new byte[1920 * 1080 * 4];
    }

    [Benchmark]
    public void CopyPixels() => _image.CopyPixels(_buffer, Format);

    [Benchmark]
    public void LoadPixels() => _image.LoadPixels(_buffer, Format);
}
