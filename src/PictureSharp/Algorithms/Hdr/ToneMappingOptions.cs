namespace PictureSharp.Processing;

/// <summary>
/// Settings of <c>hdr.ToneMap()</c>, which maps linear HDR pixels to an 8-bit sRGB image with the extended Reinhard
/// operator on luminance: <c>Lout = L × (1 + L / W²) / (1 + L)</c>, the same scale for R, G and B, then clipping to
/// the sRGB gamut and the sRGB transfer function.
/// </summary>
public sealed record ToneMappingOptions
{
    /// <summary>Default settings: no exposure change and the white point at the image's headroom.</summary>
    public static ToneMappingOptions Default { get; } = new();

    /// <summary>Exposure change in stops (EV) applied before the operator: pixels are multiplied by 2^Exposure. Default: 0.</summary>
    public float Exposure
    {
        get;
        init => field = float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(Exposure), value, "The exposure must be finite.");
    }

    /// <summary>
    /// The linear luminance W that maps to SDR white, in units after <see cref="Exposure"/>; brighter pixels clip.
    /// 1 means plain clipping. Must be finite and at least 1. Default: null, the image's <c>HdrImage.Headroom</c>.
    /// </summary>
    public float? WhitePoint
    {
        get;
        init => field = value is null || (float.IsFinite(value.Value) && value.Value >= 1f)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(WhitePoint), value, "The white point must be finite and at least 1.");
    }
}
