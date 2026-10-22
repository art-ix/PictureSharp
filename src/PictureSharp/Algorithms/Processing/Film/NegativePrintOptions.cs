namespace PictureSharp.Processing;

/// <summary>
/// How an inverted negative is printed, for <see cref="ImageProcessingContext.InvertNegative(NegativeCalibration, NegativePrintOptions?)"/>:
/// the contrast of the simulated paper, a gentle S curve and the exposure.
/// </summary>
public sealed record NegativePrintOptions
{
    /// <summary>Default options.</summary>
    public static NegativePrintOptions Default { get; } = new();

    /// <summary>
    /// The paper's contrast: how many decades of light (powers of 10) separate the film base, printed black, from the
    /// densest calibrated value, printed white. From 0.5 to 4; larger values give more contrast. Default: 1.6.
    /// </summary>
    public float Contrast
    {
        get;
        init => field = value is >= 0.5f and <= 4f ? value : throw new ArgumentOutOfRangeException(nameof(Contrast), value, "Contrast must be between 0.5 and 4.");
    } = 1.6f;

    /// <summary>
    /// Strength (0–1) of the S curve <c>s + Curve · s(1 − s)(2s − 1)</c> applied to the encoded result s (0–1), which
    /// deepens shadows and brightens highlights as a print does. Default: 0.3.
    /// </summary>
    public float Curve
    {
        get;
        init => field = value is >= 0f and <= 1f ? value : throw new ArgumentOutOfRangeException(nameof(Curve), value, "Curve must be between 0 and 1.");
    } = 0.3f;

    /// <summary>Exposure change of the print in stops (EV), from −4 to 4; positive values brighten. Default: 0.</summary>
    public float Exposure
    {
        get;
        init => field = value is >= -4f and <= 4f ? value : throw new ArgumentOutOfRangeException(nameof(Exposure), value, "Exposure must be between -4 and 4.");
    }
}
