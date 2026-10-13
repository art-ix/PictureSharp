using RasterSharp.Internal;

namespace RasterSharp;

/// <summary>
/// Base record for format-specific encoder options. Values are checked when they are set, and a modified copy can be
/// made with <c>with</c>, e.g. <c>options with { Quality = 90 }</c>.
/// </summary>
public abstract record EncoderOptions
{
    private protected EncoderOptions()
    {
    }

    /// <summary>The format produced by the encoder.</summary>
    public abstract ImageFormat Format { get; }

    /// <summary>Which metadata blocks to write when present on the image. Default: all.</summary>
    public MetadataKinds Metadata
    {
        get;
        init => field = (value & ~MetadataKinds.All) == 0 ? value : throw new ArgumentOutOfRangeException(nameof(Metadata), value, "Unknown metadata kind.");
    } = MetadataKinds.All;
}

/// <summary>Content presets that tune WebP lossy encoding parameters (as in libwebp's WebPPreset).</summary>
public enum WebPPreset
{
    /// <summary>Default preset.</summary>
    Default = 0,
    /// <summary>Digital picture, like portrait, inner shot.</summary>
    Picture,
    /// <summary>Outdoor photograph, with natural lighting.</summary>
    Photo,
    /// <summary>Hand or line drawing, with high-contrast details.</summary>
    Drawing,
    /// <summary>Small-sized colorful images.</summary>
    Icon,
    /// <summary>Text-like images.</summary>
    Text,
}

/// <summary>Predictive filter applied to the alpha plane of lossy images.</summary>
public enum WebPAlphaFilter
{
    /// <summary>No filtering.</summary>
    None = 0,
    /// <summary>Pick a filter with a fast heuristic.</summary>
    Fast = 1,
    /// <summary>Try all filters and keep the smallest result.</summary>
    Best = 2,
}

/// <summary>Options for the WebP encoder. Mirrors libwebp's WebPConfig.</summary>
public sealed record WebPEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.WebP;

    /// <summary>
    /// Quality 0–100. For lossy: visual quality (default 75). For lossless: compression effort
    /// (higher is smaller and slower).
    /// </summary>
    public float Quality { get; init => field = Check.Range(value, 0f, 100f); } = 75f;

    /// <summary>Use the lossless (VP8L) bitstream.</summary>
    public bool Lossless { get; init; }

    /// <summary>Quality/speed trade-off 0 (fast) – 6 (slow, best). Default: 4.</summary>
    public int Method { get; init => field = Check.Range(value, 0, 6); } = 4;

    /// <summary>Content preset; applied before the explicit tuning options below.</summary>
    public WebPPreset Preset { get; init => field = Check.Defined(value); } = WebPPreset.Default;

    /// <summary>Alpha plane quality 0–100 (100 = lossless alpha). Default: 100.</summary>
    public int AlphaQuality { get; init => field = Check.Range(value, 0, 100); } = 100;

    /// <summary>Compress the alpha plane with the lossless coder (otherwise stored raw). Default: true.</summary>
    public bool AlphaCompression { get; init; } = true;

    /// <summary>Predictive filter for the alpha plane. Default: <see cref="WebPAlphaFilter.Fast"/>.</summary>
    public WebPAlphaFilter AlphaFilter { get; init => field = Check.Defined(value); } = WebPAlphaFilter.Fast;

    /// <summary>Disable RGB cleanup under fully transparent pixels. Lossless mode preserves these RGB values exactly.</summary>
    public bool Exact { get; init; }

    /// <summary>Near-lossless preprocessing level 0–100 for lossless mode (100 = off). Default: 100.</summary>
    public int NearLossless { get; init => field = Check.Range(value, 0, 100); } = 100;

    /// <summary>Spatial noise shaping strength 0–100. Null uses the preset value (default 50).</summary>
    public int? SnsStrength { get; init => field = Check.Range(value, 0, 100); }

    /// <summary>Deblocking filter strength 0–100 (0 = off). Null uses the preset value (default 60).</summary>
    public int? FilterStrength { get; init => field = Check.Range(value, 0, 100); }

    /// <summary>Deblocking filter sharpness 0 (sharpest filtering) – 7. Null uses the preset value.</summary>
    public int? FilterSharpness { get; init => field = Check.Range(value, 0, 7); }

    /// <summary>Automatically adjust filter strength.</summary>
    public bool AutoFilter { get; init; }

    /// <summary>Number of segments 1–4. Null uses the preset value (default 4).</summary>
    public int? Segments { get; init => field = Check.Range(value, 1, 4); }

    /// <summary>log2 of the number of token partitions 0–3. Default: 0.</summary>
    public int Partitions { get; init => field = Check.Range(value, 0, 3); }

    /// <summary>Target output size in bytes (0 = disabled). Takes precedence over <see cref="Quality"/>.</summary>
    public int TargetSize { get; init => field = Check.AtLeast(value, 0); }

    /// <summary>Target PSNR in dB (0 = disabled).</summary>
    public float TargetPsnr { get; init => field = Check.AtLeast(value, 0f); }

    /// <summary>Number of entropy-analysis passes 1–10. Default: 1.</summary>
    public int Passes { get; init => field = Check.Range(value, 1, 10); } = 1;

    /// <summary>Use the slower, more accurate "sharp" RGB→YUV conversion.</summary>
    public bool UseSharpYuv { get; init; }

    /// <summary>Animation: allow mixing lossy and lossless frames, choosing the smaller per frame.</summary>
    public bool AllowMixed { get; init; }

    /// <summary>Animation: also try full-canvas candidates and disable periodic keyframes to minimize size.</summary>
    public bool MinimizeSize { get; init; }

    /// <summary>Animation: lower bound of the keyframe search window (default 9). Normalized as in libwebp.</summary>
    public int KeyframeMin { get; init => field = Check.AtLeast(value, 0); } = 9;

    /// <summary>Animation: maximum keyframe distance (default 17). Zero disables insertion; one makes every frame a keyframe.</summary>
    public int KeyframeMax { get; init => field = Check.AtLeast(value, 0); } = 17;

    /// <summary>Maximum number of threads to use (1 = single-threaded, -1 = automatic). Output does not depend on it.</summary>
    public int MaxDegreeOfParallelism
    {
        get;
        init => field = value == -1 || value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), value, "Expected -1 or a positive number.");
    } = -1;

    internal WebPEncoderOptions ForAnimationFrame(bool lossless, bool blend) => new()
    {
        Quality = Quality, Lossless = lossless, Method = Method, Preset = Preset,
        AlphaQuality = AlphaQuality, AlphaCompression = AlphaCompression, AlphaFilter = AlphaFilter,
        Exact = Exact, NearLossless = NearLossless, SnsStrength = SnsStrength,
        FilterStrength = blend && !lossless ? 0 : FilterStrength,
        FilterSharpness = FilterSharpness, AutoFilter = !blend && AutoFilter,
        Segments = Segments, Partitions = Partitions, TargetSize = TargetSize,
        TargetPsnr = TargetPsnr, Passes = Passes, UseSharpYuv = UseSharpYuv,
        Metadata = MetadataKinds.None, MaxDegreeOfParallelism = MaxDegreeOfParallelism,
    };
}

/// <summary>PNG color type selection.</summary>
public enum PngColorType
{
    /// <summary>Choose the smallest lossless representation (palette, grayscale, RGB or RGBA).</summary>
    Auto = 0,
    /// <summary>8-bit grayscale (color is converted to luminance).</summary>
    Grayscale,
    /// <summary>8-bit grayscale with alpha.</summary>
    GrayscaleAlpha,
    /// <summary>24-bit RGB (alpha is dropped).</summary>
    Rgb,
    /// <summary>32-bit RGBA.</summary>
    Rgba,
    /// <summary>Indexed color; fails if the image has more than 256 distinct colors.</summary>
    Palette,
}

/// <summary>PNG scanline filter selection.</summary>
public enum PngFilterStrategy
{
    /// <summary>Choose per row with the minimum-sum-of-absolute-differences heuristic.</summary>
    Adaptive = 0,
    /// <summary>Filter type 0.</summary>
    None,
    /// <summary>Filter type 1.</summary>
    Sub,
    /// <summary>Filter type 2.</summary>
    Up,
    /// <summary>Filter type 3.</summary>
    Average,
    /// <summary>Filter type 4.</summary>
    Paeth,
}

/// <summary>Options for the PNG encoder.</summary>
public sealed record PngEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Png;

    /// <summary>Deflate compression level 0 (store) – 9 (best). Default: 6.</summary>
    public int CompressionLevel { get; init => field = Check.Range(value, 0, 9); } = 6;

    /// <summary>Output color type. Default: automatic.</summary>
    public PngColorType ColorType { get; init => field = Check.Defined(value); } = PngColorType.Auto;

    /// <summary>Scanline filter strategy. Default: adaptive.</summary>
    public PngFilterStrategy FilterStrategy { get; init => field = Check.Defined(value); } = PngFilterStrategy.Adaptive;

    /// <summary>Write an Adam7-interlaced image.</summary>
    public bool Interlace { get; init; }

    /// <summary>Write all frames of animated images as APNG (otherwise only the first frame). Default: true.</summary>
    public bool SaveAnimation { get; init; } = true;
}

/// <summary>Options for the BMP encoder.</summary>
public sealed record BmpEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Bmp;

    /// <summary>
    /// Bits per pixel: 1, 4 or 8 (palette), 24 (BGR) or 32 (BGRA with alpha), or 0 to choose automatically:
    /// a palette when the image is opaque and has at most 256 colors, 24 bits when it is opaque, otherwise 32 bits.
    /// Palette and 24-bit output drop the alpha channel. Default: 0.
    /// </summary>
    public int BitsPerPixel
    {
        get;
        init => field = value is 0 or 1 or 4 or 8 or 24 or 32 ? value : throw new ArgumentOutOfRangeException(nameof(BitsPerPixel), value, "Expected 0, 1, 4, 8, 24 or 32.");
    }
}
