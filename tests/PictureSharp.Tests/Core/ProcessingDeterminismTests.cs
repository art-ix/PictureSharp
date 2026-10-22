using System.Runtime.InteropServices;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Float-sensitive processing pinned to the x64 results that match Pillow, so other architectures and SIMD paths
/// must produce identical pixels without reference tools.
/// </summary>
public class ProcessingDeterminismTests
{
    public static TestCases<string, string> Cases() => new()
    {
        { "enhance_noise_color_-0.4", "729f704c605e2eaf0e9e61bf6d0e0ca04ffbfd3aecb5a05d866229c28f382a3d" },
        { "enhance_photo_color_-0.4", "391afe48ab9b1ece980b2f8517517917de67020c1f942beab768f0534ef6a450" },
        { "rotate_noise_45_bicubic_False", "b898b12785fd6087f4e937f0509ce03f5e890e2e5e96ddd0101bad1564de5a90" },
        { "rotate_noise_-12.5_bicubic_True", "1f890cf65378d5cb8671b046506aa73730020ea2efd45f66df116d19b93e81cb" },
        { "rotate_photo_45_bilinear_False", "8ce835b43c0c22e0759ce32bc2af5c776959f8cc65ab7ec35ad930ad4e6e8d6a" },
    };

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Result_IsIdenticalOnEveryArchitecture(string name, string expected)
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
        await Assert.That(Corpus.Sha256(MemoryMarshal.AsBytes(result.GetPixelSpan()))).IsEqualTo(expected);
    }

    public static TestCases<string, string> PhotoAdjustCases() => new()
    {
        { "exposure", "6d787b6b54601b1710cfa35d72436800a874729f76d3b978c94a3b367f3b1cd4" },
        { "white_balance", "1b90037a3298f1bfef700b4021bc2f8130c44883d14eb50318ef990773fca6cb" },
        { "vibrance", "a2d79e11bd744f545580bc595492476252629d8218963f948dfddce15903dfb1" },
        { "vignette", "205a7482063f51b1f1c631ed1dc909c42ed7904cbf7570c8a7150df3d84712c1" },
        { "shadows_highlights", "50031b065e0bbd3cf670f89fef36f7e2bdddb0faf96c70cdb72336429de77e2b" },
        { "clarity", "a532c676e5f7cf84bdca2e54a70b981c4c489d45b79ac2b3f03c5ae7e5d2e2cf" },
        { "denoise", "6ba50dde8fda0d610ac5970c2a821426c5c3ca412e569b0a5074c0b35dc5c594" },
        { "curves", "283a9d7eafa3eecd62c45fe45d6fcb226a563bfc19ffc815f01bfe000bc3e688" },
        { "white_balance_neutral", "1c753d8f9dd35e92cbf2d530191dad3edd7e08948abdbaa3144d2217cf3d7625" },
        { "auto_white_balance", "c8e8a9900d0e38db42a06448ac9bb1fb56411ca1b986cf0cffe71f5e0bd5c611" },
        { "remove_dust", "7a8483a8188a9ab7ea4c7d9966251e2db202707816310dc86e94e751c78c2d0e" },
        { "reduce_color_noise", "0945188f431d803d341de02e390209e14c64d9ca01e4d92719012d63064b6eb3" },
        { "develop_negative", "cdeca05eb887e504fe162e743e5b677eb6c5b3bf5f02c2ba47fff8422d8df622" },
        { "develop_negative_strip", "ec2e9fd9be65c44c13eb9ce935e6ce7a1fbc8306a7945d2a85450d47057771b3" },
    };

    // 720 × 480 pixels: enough for the parallel bands, so the hashes also pin the result against the thread count
    // (compare a run with DOTNET_PROCESSOR_COUNT=1).
    [Test]
    [MethodDataSource(nameof(PhotoAdjustCases))]
    public async Task PhotoAdjustment_IsIdenticalOnEveryArchitecture(string name, string expected)
    {
        using Image source = name switch
        {
            "remove_dust" => PhotoCleanupTests.Dusty(720, 480, 9),
            "develop_negative" => FilmNegativeTests.Negative(PhotoCleanupTests.Dusty(720, 480, 9), 9),
            "develop_negative_strip" => FilmFrameTests.Strip(light: 0.3f, seed: 9, degrees: 1.2f),
            _ => ProcessingOracleTests.Noise(720, 480, 9),
        };
        using Image result = source.Clone(x => _ = name switch
        {
            "exposure" => x.Exposure(0.7f),
            "white_balance" => x.WhiteBalance(0.6f, -0.3f),
            "vibrance" => x.Vibrance(0.6f),
            "vignette" => x.Vignette(new VignetteOptions { Amount = 0.8f, Midpoint = 0.3f, Feather = 0.7f }),
            "shadows_highlights" => x.ShadowsHighlights(0.6f, -0.6f, 5f),
            "clarity" => x.Clarity(0.7f, 5f),
            "curves" => x.Curves([new(0, 10), new(70, 50), new(180, 210), new(250, 245)]),
            "white_balance_neutral" => x.WhiteBalance(new Rgba32(180, 150, 110, 255)),
            "auto_white_balance" => x.AutoWhiteBalance(0.8f),
            "remove_dust" => x.RemoveDust(4, 0.08f),
            "reduce_color_noise" => x.ReduceColorNoise(0.9f, 6),
            "develop_negative" => x.DevelopNegative(new NegativeOptions { FilmBaseRegion = new System.Drawing.Rectangle(0, 0, 720, 8) }),
            "develop_negative_strip" => x.DevelopNegative(new NegativeOptions { DustSize = 2 }),
            _ => x.Denoise(0.7f, 3),
        });
        await Assert.That(Corpus.Sha256(MemoryMarshal.AsBytes(result.GetPixelSpan()))).IsEqualTo(expected);
    }
}
