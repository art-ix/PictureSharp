namespace PictureSharp.Processing;

/// <summary>
/// The result of <see cref="ImageAnalysisExtensions.AnalyzeNegative(Image, NegativeOptions?)"/>: the calibration to
/// develop the negative with and what was found on the way.
/// </summary>
public sealed class NegativeAnalysis
{
    internal NegativeAnalysis(NegativeCalibration calibration, ChannelValues clippedFraction, NegativeWarnings warnings, FilmFrame? frame)
    {
        Calibration = calibration;
        ClippedFraction = clippedFraction;
        Warnings = warnings;
        Frame = frame;
    }

    /// <summary>The measured calibration; pass it in <see cref="NegativeOptions.Calibration"/> to develop without measuring again.</summary>
    public NegativeCalibration Calibration { get; }

    /// <summary>The share (0–1) of the measured, not fully transparent pixels whose channel is at 0–2 or 253–255.</summary>
    public ChannelValues ClippedFraction { get; }

    /// <summary>
    /// The frame that was measured: the one found in the strip, or <see cref="NegativeOptions.Frame"/>;
    /// <see langword="null"/> when none was found or <see cref="NegativeOptions.Region"/> or
    /// <see cref="NegativeOptions.FilmBaseRegion"/> was given. Pass it in <see cref="NegativeOptions.Frame"/> to
    /// develop without searching again.
    /// </summary>
    public FilmFrame? Frame { get; }

    /// <summary>
    /// How smooth the backlight looks through the rebate: the median standard deviation (in 8-bit levels) of the luma
    /// in small blocks of it. Below 2 for a diffused lamp, 20 and more for a monitor's pixel grid;
    /// <see langword="null"/> without a <see cref="Frame"/> whose rebate was seen.
    /// </summary>
    public float? FilmBaseUniformity => Frame?.FilmBaseUniformity;

    /// <summary>Problems found; <see cref="NegativeWarnings.None"/> when there are none.</summary>
    public NegativeWarnings Warnings { get; }
}
