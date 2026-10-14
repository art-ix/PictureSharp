using System.Runtime.InteropServices;
using RasterSharp.Tests.Infrastructure;

namespace RasterSharp.Tests.Core;

/// <summary>
/// Float-sensitive processing pinned to the x64 results that match Pillow, so other architectures and SIMD paths
/// must produce identical pixels without reference tools.
/// </summary>
public class ProcessingDeterminismTests
{
    public static TheoryData<string, string> Cases() => new()
    {
        { "enhance_noise_color_-0.4", "729f704c605e2eaf0e9e61bf6d0e0ca04ffbfd3aecb5a05d866229c28f382a3d" },
        { "enhance_photo_color_-0.4", "391afe48ab9b1ece980b2f8517517917de67020c1f942beab768f0534ef6a450" },
        { "rotate_noise_45_bicubic_False", "b898b12785fd6087f4e937f0509ce03f5e890e2e5e96ddd0101bad1564de5a90" },
        { "rotate_noise_-12.5_bicubic_True", "1f890cf65378d5cb8671b046506aa73730020ea2efd45f66df116d19b93e81cb" },
        { "rotate_photo_45_bilinear_False", "8ce835b43c0c22e0759ce32bc2af5c776959f8cc65ab7ec35ad930ad4e6e8d6a" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Result_IsIdenticalOnEveryArchitecture(string name, string expected)
    {
        using Image source = name.Contains("_noise_", StringComparison.Ordinal)
            ? ProcessingOracleTests.Noise(41, 29, 1)
            : Image.Load(Corpus.PathOf("jpeg/q90_444.jpg"));
        using Image result = name switch
        {
            "enhance_noise_color_-0.4" or "enhance_photo_color_-0.4" => source.Clone(x => x.Saturate(-0.4f)),
            "rotate_noise_45_bicubic_False" => source.Clone(x => x.Rotate(45, ResamplingFilter.Bicubic, expand: false)),
            "rotate_noise_-12.5_bicubic_True" => source.Clone(x => x.Rotate(-12.5f, ResamplingFilter.Bicubic, expand: true)),
            _ => source.Clone(x => x.Rotate(45, ResamplingFilter.Bilinear, expand: false)),
        };
        Assert.Equal(expected, Corpus.Sha256(MemoryMarshal.AsBytes(result.GetPixelSpan())));
    }
}
