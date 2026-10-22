namespace PictureSharp.Processing;

/// <summary>
/// The result of <see cref="ImageAnalysisExtensions.AnalyzeNegative(Image, NegativeOptions?)"/>: the calibration to
/// develop the negative with and what was found on the way.
/// </summary>
public sealed class NegativeAnalysis
{
    internal NegativeAnalysis(NegativeCalibration calibration, ChannelValues clippedFraction, NegativeWarnings warnings)
    {
        Calibration = calibration;
        ClippedFraction = clippedFraction;
        Warnings = warnings;
    }

    /// <summary>The measured calibration; pass it in <see cref="NegativeOptions.Calibration"/> to develop without measuring again.</summary>
    public NegativeCalibration Calibration { get; }

    /// <summary>The share (0–1) of the measured, not fully transparent pixels whose channel is at 0–2 or 253–255.</summary>
    public ChannelValues ClippedFraction { get; }

    /// <summary>Problems found; <see cref="NegativeWarnings.None"/> when there are none.</summary>
    public NegativeWarnings Warnings { get; }
}
