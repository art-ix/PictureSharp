using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PictureSharp.Tests.Infrastructure;

/// <summary>Access to tests/assets: the generated corpus and its reference decodes.</summary>
public static class Corpus
{
    private static readonly Lazy<string> AssetsDir = new(FindAssets);
    private static readonly Lazy<Dictionary<string, ReferenceEntry>> Refs = new(LoadReferences);

    public static string Assets => AssetsDir.Value;

    public static string CorpusDir => Path.Combine(Assets, "corpus");

    public static string SamplesDir => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Assets)!)!, "samples");

    public static IReadOnlyDictionary<string, ReferenceEntry> References => Refs.Value;

    public static string PathOf(string relative) => Path.Combine(CorpusDir, relative);

    public static byte[] Read(string relative) => File.ReadAllBytes(PathOf(relative));

    /// <summary>Corpus files whose relative path starts with <paramref name="prefix"/>, as xUnit theory data.</summary>
    public static TheoryData<string> Files(string prefix)
    {
        var data = new TheoryData<string>();
        foreach (string key in References.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            data.Add(key);
        }

        return data;
    }

    /// <summary>
    /// A few files per format and container variant, for format-independent paths (entry points, limits,
    /// cancellation) where running the whole corpus adds time but no coverage.
    /// </summary>
    public static TheoryData<string> Representatives() =>
    [
        "bmp/im_pal8_rle.bmp", "bmp/im_rgba32_v5.bmp",
        "gif/im_interlace.gif", "gif/pil_anim_dispose2.gif",
        "ico/mixed.ico", "ico/png32.ico",
        "jpeg/exif_resolution.jpg", "jpeg/progressive_420.jpg", "jpeg/q90_420.jpg",
        "pcx/pil_rgb.pcx",
        "png/apng_alpha.png", "png/rgb8_interlaced.png", "png/rgba8.png",
        "webp/alpha/photo_alpha_a_default.webp", "webp/anim/alpha_mixed.webp", "webp/lossless/graphic_ll_z6.webp",
        "webp/lossy/photo_q75.webp", "webp/meta/lossless_exif.webp",
    ];

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string FindAssets()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "tests", "assets");
            if (File.Exists(Path.Combine(candidate, "reference.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("tests/assets/reference.json not found; run tests/assets/generate_corpus.py and make_reference.py.");
    }

    private static Dictionary<string, ReferenceEntry> LoadReferences()
    {
        using FileStream fs = File.OpenRead(Path.Combine(Assets, "reference.json"));
        return JsonSerializer.Deserialize<Dictionary<string, ReferenceEntry>>(fs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}

public sealed class ReferenceEntry
{
    public string Format { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasAlpha { get; set; }
    public bool Animated { get; set; }
    public string? Compression { get; set; }
    public int Loop { get; set; }
    public int[]? Background { get; set; }
    public int BitDepth { get; set; }
    public int HeaderSize { get; set; }
    public int ColorType { get; set; }
    public bool Interlaced { get; set; }
    public bool Progressive { get; set; }
    public string? Mode { get; set; }
    public float[]? Dpi { get; set; }
    public int Orientation { get; set; }
    public string Decoder { get; set; } = "";
    public string? ReferencePng { get; set; }
    public string? IccSha256 { get; set; }
    public string? XmpSha256 { get; set; }
    public List<ReferenceFrame> Frames { get; set; } = [];
}

public sealed class ReferenceFrame
{
    public string Sha256 { get; set; } = "";
    public double Duration { get; set; }
}
