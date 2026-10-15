namespace PictureSharp.Processing;

/// <summary>
/// Input and output levels with a midtone gamma for <see cref="ImageProcessingContext.Levels(LevelsOptions)"/>, as the
/// Levels dialog of image editors and ImageMagick's <c>-level</c>/<c>+level</c>. Each selected channel value x becomes
/// <c>OutputBlack + (OutputWhite − OutputBlack) · clamp((x − InputBlack) / (InputWhite − InputBlack), 0, 1)^(1 / Gamma)</c>,
/// computed in double precision on the encoded (sRGB) values and rounded half up.
/// </summary>
public sealed record LevelsOptions
{
    /// <summary>Input value mapped to <see cref="OutputBlack"/>; lower values clip. Must be below <see cref="InputWhite"/>. Default: 0.</summary>
    public byte InputBlack { get; init; }

    /// <summary>Input value mapped to <see cref="OutputWhite"/>; higher values clip. Default: 255.</summary>
    public byte InputWhite { get; init; } = 255;

    /// <summary>Midtone gamma, finite and greater than 0: values above 1 brighten the midtones, below 1 darken them. Default: 1.</summary>
    public float Gamma
    {
        get;
        init => field = value > 0f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(Gamma), value, "Gamma must be a finite number greater than 0.");
    } = 1f;

    /// <summary>Output value for <see cref="InputBlack"/>. May exceed <see cref="OutputWhite"/> to invert. Default: 0.</summary>
    public byte OutputBlack { get; init; }

    /// <summary>Output value for <see cref="InputWhite"/>. Default: 255.</summary>
    public byte OutputWhite { get; init; } = 255;

    /// <summary>The channels to change. Default: <see cref="ColorChannels.Rgb"/>.</summary>
    public ColorChannels Channels
    {
        get;
        init => field = value is not ColorChannels.None && (value & ~ColorChannels.Rgb) == 0 ? value : throw new ArgumentOutOfRangeException(nameof(Channels), value, "Expected a non-empty combination of Red, Green and Blue.");
    } = ColorChannels.Rgb;
}
