namespace PictureSharp.Processing;

/// <summary>Problems <see cref="ImageAnalysisExtensions.AnalyzeNegative(Image, NegativeOptions?)"/> found in a photographed negative.</summary>
[Flags]
public enum NegativeWarnings
{
    /// <summary>No problem found.</summary>
    None = 0,

    /// <summary>
    /// More than 1% of the measured pixels have a channel at 0–2 or 253–255, so dense areas (usually the sky) lost
    /// detail in that channel. A bluish backlight or a shorter exposure keeps all channels in range.
    /// </summary>
    ChannelClipped = 1,

    /// <summary>
    /// No <see cref="NegativeOptions.FilmBaseRegion"/> was given, so the film base was estimated from the lightest
    /// pixels; a region of bare film (the rebate or the gap between frames) gives more accurate colors.
    /// </summary>
    FilmBaseEstimated = 2,

    /// <summary>A channel spans less than 0.3 density units: the negative is very thin or the region holds no image.</summary>
    LowDensityRange = 4,
}
