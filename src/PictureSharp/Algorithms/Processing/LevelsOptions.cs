using PictureSharp.Algorithms.Processing;

namespace PictureSharp.Processing;

/// <summary>
/// Input and output levels with a midtone gamma for <see cref="ImageProcessingContext.Levels(LevelsOptions)"/>, as the
/// Levels dialog of image editors and ImageMagick's <c>-level</c>/<c>+level</c>. Levels are normalized: 0 is black and
/// 1 is white at any depth (an 8-bit level v is <c>v / 255f</c>). Each selected channel value x (also 0–1) becomes
/// <c>OutputBlack + (OutputWhite − OutputBlack) · clamp((x − InputBlack) / (InputWhite − InputBlack), 0, 1)^(1 / Gamma)</c>,
/// computed in double precision on the encoded (sRGB) values and rounded half up to the image's depth.
/// </summary>
public sealed record LevelsOptions
{
    /// <summary>Input level (0–1) mapped to <see cref="OutputBlackNormalized"/>; lower values clip. Must be below <see cref="InputWhiteNormalized"/>. Default: 0.</summary>
    public float InputBlackNormalized
    {
        get;
        init => field = NormalizedLevel.Check(value, nameof(InputBlackNormalized));
    }

    /// <summary>Input level (0–1) mapped to <see cref="OutputWhiteNormalized"/>; higher values clip. Default: 1.</summary>
    public float InputWhiteNormalized
    {
        get;
        init => field = NormalizedLevel.Check(value, nameof(InputWhiteNormalized));
    } = 1f;

    /// <summary>Midtone gamma, finite and greater than 0: values above 1 brighten the midtones, below 1 darken them. Default: 1.</summary>
    public float Gamma
    {
        get;
        init => field = value > 0f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(Gamma), value, "Gamma must be a finite number greater than 0.");
    } = 1f;

    /// <summary>Output level (0–1) for <see cref="InputBlackNormalized"/>. May exceed <see cref="OutputWhiteNormalized"/> to invert. Default: 0.</summary>
    public float OutputBlackNormalized
    {
        get;
        init => field = NormalizedLevel.Check(value, nameof(OutputBlackNormalized));
    }

    /// <summary>Output level (0–1) for <see cref="InputWhiteNormalized"/>. Default: 1.</summary>
    public float OutputWhiteNormalized
    {
        get;
        init => field = NormalizedLevel.Check(value, nameof(OutputWhiteNormalized));
    } = 1f;

    /// <summary>The channels to change. Default: <see cref="ColorChannels.Rgb"/>.</summary>
    public ColorChannels Channels
    {
        get;
        init => field = value is not ColorChannels.None && (value & ~ColorChannels.Rgb) == 0 ? value : throw new ArgumentOutOfRangeException(nameof(Channels), value, "Expected a non-empty combination of Red, Green and Blue.");
    } = ColorChannels.Rgb;
}
