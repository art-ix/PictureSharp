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
    /// Neither <see cref="NegativeOptions.FilmBaseRegion"/> nor the rebate of a found <see cref="FilmFrame"/> gave the
    /// film base, so it was estimated from the lightest pixels; a strip photographed with its perforation, or a region
    /// of bare film, gives more accurate colors.
    /// </summary>
    FilmBaseEstimated = 2,

    /// <summary>A channel spans less than 0.3 density units: the negative is very thin or the region holds no image.</summary>
    LowDensityRange = 4,

    /// <summary>
    /// The rebate is not smooth (<see cref="NegativeAnalysis.FilmBaseUniformity"/> above 6): a monitor's pixel grid or
    /// a patterned backlight shows through the film and will show in the picture. A diffuser, or more distance
    /// between the film and the screen, smooths it.
    /// </summary>
    BacklightTexture = 8,

    /// <summary>The photograph cuts the frame off (<see cref="FilmFrame.CutEdges"/>), so part of the picture is missing.</summary>
    FrameCut = 16,

    /// <summary>
    /// The backlight measured on the rebate is more than 25% brighter at one corner of the frame than at another.
    /// <see cref="ImageProcessingContext.DevelopNegative(NegativeOptions?)"/> evens out such a gradient (see
    /// <see cref="NegativeOptions.CorrectIllumination"/>), but not spots or arcs; a diffuser gives even light.
    /// </summary>
    UnevenIllumination = 32,
}
