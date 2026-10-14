using RasterSharp.Internal;

namespace RasterSharp;

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
