using PictureSharp.Primitives.Numerics;

namespace PictureSharp;

/// <summary>Settings of <c>image.ToHdr()</c>.</summary>
public sealed record HdrOptions
{
    /// <summary>Default settings: the full headroom of the file in the color space of the base pixels.</summary>
    public static HdrOptions Default { get; } = new();

    /// <summary>
    /// The largest headroom to reconstruct, as a linear ratio to SDR white (not log2 as in ISO 21496-1): 4 renders up
    /// to two stops above SDR white. The result uses min(target, source headroom); 1 gives the linearized base image.
    /// Must be finite and at least 1. Default: null, the full headroom of the file.
    /// </summary>
    public float? TargetHeadroom
    {
        get;
        init => field = value is null || (float.IsFinite(value.Value) && value.Value >= 1f)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(TargetHeadroom), value, "The headroom must be finite and at least 1.");
    }

    /// <summary>
    /// The color space of the result. Default: null, the primaries of the base pixels (Display P3 for iPhone photos, or
    /// sRGB after <c>DecoderOptions.ColorConversion = ToSrgb</c>).
    /// </summary>
    public HdrColorSpace? ColorSpace { get; init => field = value is null ? null : Check.Defined(value.Value); }

    /// <summary>Maximum number of threads to use (1 = single-threaded, -1 = automatic). Output does not depend on it.</summary>
    public int MaxDegreeOfParallelism { get; init => field = Check.Parallelism(value); } = -1;
}
