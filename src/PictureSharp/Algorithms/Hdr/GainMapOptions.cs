using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Processing;

/// <summary>
/// Settings of <c>hdr.ToGainMapImage()</c>, which computes an ISO 21496-1 gain map from an HDR image and its SDR base.
/// The defaults are those of libultrahdr's encoder API: a full-resolution RGB map with gamma 1.
/// </summary>
public sealed record GainMapOptions
{
    /// <summary>Default settings.</summary>
    public static GainMapOptions Default { get; } = new();

    /// <summary>
    /// How many times smaller the map is than the image on each side (1–128): each map pixel averages a square of
    /// <c>Scale × Scale</c> image pixels, and the map is <c>width / Scale</c> × <c>height / Scale</c> (rounded down). A
    /// scale that would leave no map pixel is replaced by the shorter side / 8. Default: 1.
    /// </summary>
    public int Scale { get; init => field = Check.Range(value, 1, 128); } = 1;

    /// <summary>
    /// 3 for one gain per color channel, which reproduces colors of the HDR image that the SDR base cannot show; 1 for
    /// a luminance map, about a third of the size. Default: 3.
    /// </summary>
    public int ChannelCount
    {
        get;
        init => field = value is 1 or 3 ? value : throw new ArgumentOutOfRangeException(nameof(ChannelCount), value, "The channel count must be 1 or 3.");
    } = 3;

    /// <summary>The ISO 21496-1 gamma of the map codes, finite and greater than 0. Default: 1.</summary>
    public float Gamma
    {
        get;
        init => field = float.IsFinite(value) && value > 0f ? value : throw new ArgumentOutOfRangeException(nameof(Gamma), value, "The gamma must be finite and greater than 0.");
    } = 1f;

    /// <summary>
    /// The headroom at which displays show the full HDR rendition, as a linear ratio to SDR white (ISO 21496-1
    /// alternate headroom, stored as log2); displays with less headroom show a weighted mix. Must be finite and at least
    /// 1. Default: null, the image's <c>HdrImage.Headroom</c>.
    /// </summary>
    public float? Headroom
    {
        get;
        init => field = value is null || (float.IsFinite(value.Value) && value.Value >= 1f)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Headroom), value, "The headroom must be finite and at least 1.");
    }

    /// <summary>
    /// How the SDR base is made when none is given: <c>hdr.ToneMap(ToneMapping)</c>. Default: null,
    /// <see cref="ToneMappingOptions.Default"/>.
    /// </summary>
    public ToneMappingOptions? ToneMapping { get; init; }

    /// <summary>Maximum number of threads to use (1 = single-threaded, -1 = automatic). Output does not depend on it.</summary>
    public int MaxDegreeOfParallelism { get; init => field = Check.Parallelism(value); } = -1;
}
