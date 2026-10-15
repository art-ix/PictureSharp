using BenchmarkDotNet.Attributes;
using PictureSharp.Formats.Ico;

namespace PictureSharp.Benchmarks;

/// <summary>ICO encode/decode cost for deterministic RGBA icons, including the container.</summary>
[MemoryDiagnoser]
public class IcoBenchmarks
{
    private Image _image = null!;
    private byte[] _encoded = [];
    private IcoEncoderOptions _options = null!;

    [Params(16, 32, 256)]
    public int Size { get; set; }

    [ParamsAllValues]
    public IcoEncoding Encoding { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _image = new Image(Size, Size);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                _image[x, y] = new((byte)(x * 13), (byte)(y * 17), (byte)(x + y), (byte)(((x + y) % 3) * 127));
            }
        }

        _options = new() { Encoding = Encoding };
        _encoded = _image.ToIco(_options);
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public int Encode() => _image.ToIco(_options).Length;

    [Benchmark]
    public int Decode()
    {
        using Image image = Image.Load(_encoded);
        return image.Width;
    }
}
