using System.Drawing;

namespace PictureSharp.Processing;

/// <summary>
/// Settings for <see cref="ImageProcessingContext.DevelopNegative(NegativeOptions?)"/> and
/// <see cref="ImageAnalysisExtensions.AnalyzeNegative(Image, NegativeOptions?)"/>: what to measure, how to print and
/// how much to clean.
/// </summary>
public sealed record NegativeOptions
{
    /// <summary>
    /// Default options: a color negative whose frame is found, straightened and cropped, with the backlight evened
    /// out and color noise removed, without dust removal.
    /// </summary>
    public static NegativeOptions Default { get; } = new();

    /// <summary>The kind of film. Default: <see cref="FilmType.ColorNegative"/>.</summary>
    public FilmType FilmType
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(FilmType), value, "Unknown film type.");
    }

    /// <summary>
    /// The area that is measured, which must lie inside the image: the picture of a frame without the rebate, the
    /// backlight or fingers. <see langword="null"/> (the default) measures the found <see cref="FilmFrame"/>, or the
    /// whole image when there is none. Setting it (or <see cref="FilmBaseRegion"/>) turns off the search for the
    /// frame, so the whole image is developed.
    /// </summary>
    public Rectangle? Region { get; init; }

    /// <summary>
    /// An area of bare film (the rebate beside the perforation or the gap between frames), which must lie inside the
    /// image; its median color is the film base. <see langword="null"/> (the default) takes the base from the rebate
    /// of the found <see cref="FilmFrame"/>, or else estimates it from the lightest pixels of <see cref="Region"/> and
    /// reports <see cref="NegativeWarnings.FilmBaseEstimated"/>. Setting it turns off the search for the frame.
    /// </summary>
    public Rectangle? FilmBaseRegion { get; init; }

    /// <summary>
    /// The frame to measure and develop, from <see cref="ImageAnalysisExtensions.FindFilmFrame(Image)"/> or
    /// <see cref="NegativeAnalysis.Frame"/> of the same image, so it is not searched for again. Default:
    /// <see langword="null"/>, which searches unless <see cref="Region"/> or <see cref="FilmBaseRegion"/> is set.
    /// </summary>
    public FilmFrame? Frame { get; init; }

    /// <summary>
    /// Whether <see cref="ImageProcessingContext.DevelopNegative(NegativeOptions?)"/> straightens the strip and crops
    /// it to the frame, as <c>Rotate(frame.Angle).Crop(frame.Bounds)</c>; otherwise the whole image is developed.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool Crop { get; init; } = true;

    /// <summary>
    /// Whether <see cref="ImageProcessingContext.DevelopNegative(NegativeOptions?)"/> evens out the backlight: the
    /// film base follows a gradient measured on the frame's rebate (along the strip, and across it when the rebate on
    /// both sides is in the photograph), so a frame lit more brightly on one side prints evenly. Applies only to a
    /// found frame. Default: <see langword="true"/>.
    /// </summary>
    public bool CorrectIllumination { get; init; } = true;

    /// <summary>
    /// A calibration to use instead of measuring the image, for example one shared by a roll; then
    /// <see cref="FilmType"/>, <see cref="Region"/>, <see cref="FilmBaseRegion"/>, <see cref="HighlightPercentile"/>
    /// and <see cref="MidtoneBalance"/> are ignored. The frame is still found, straightened, cropped and evenly lit.
    /// Default: <see langword="null"/>.
    /// </summary>
    public NegativeCalibration? Calibration { get; init; }

    /// <summary>
    /// The percentile (50–100) of each channel's density that prints white: the densest
    /// <c>100 − HighlightPercentile</c> percent of the measured pixels are clipped to white. Default: 99.7.
    /// </summary>
    public float HighlightPercentile
    {
        get;
        init => field = value is >= 50f and <= 100f ? value : throw new ArgumentOutOfRangeException(nameof(HighlightPercentile), value, "HighlightPercentile must be between 50 and 100.");
    } = 99.7f;

    /// <summary>
    /// How strongly (0–1) the midtones of the channels are pulled together: each channel's median gets the gamma that
    /// moves it this far (in a power-law sense) towards the geometric mean of the medians. Default: 0.6.
    /// </summary>
    public float MidtoneBalance
    {
        get;
        init => field = value is >= 0f and <= 1f ? value : throw new ArgumentOutOfRangeException(nameof(MidtoneBalance), value, "MidtoneBalance must be between 0 and 1.");
    } = 0.6f;

    /// <summary>
    /// The largest dust speck (pixels across) that <see cref="ImageProcessingContext.RemoveDust(int, float)"/> removes
    /// after inversion, from 0 to 64; <see langword="null"/> uses 0.5% of the shorter side, at least 2. Default: 0, no
    /// dust removal.
    /// </summary>
    /// <remarks>
    /// Dust removal is off by default because it cannot tell a speck from isolated detail of the same size, such as a
    /// catchlight or a small letter. Choose a size close to the dust and smaller than the finest lettering.
    /// </remarks>
    public int? DustSize
    {
        get;
        init => field = value is null or (>= 0 and <= 64) ? value : throw new ArgumentOutOfRangeException(nameof(DustSize), value, "DustSize must be between 0 and 64.");
    } = 0;

    /// <summary>
    /// The strength (0–1) of <see cref="ImageProcessingContext.ReduceColorNoise(float, int)"/> after inverting a color
    /// negative; 0 skips it. Default: 1.
    /// </summary>
    public float ColorNoiseReduction
    {
        get;
        init => field = value is >= 0f and <= 1f ? value : throw new ArgumentOutOfRangeException(nameof(ColorNoiseReduction), value, "ColorNoiseReduction must be between 0 and 1.");
    } = 1f;

    /// <summary>How the inverted negative is printed. Default: <see cref="NegativePrintOptions.Default"/>.</summary>
    public NegativePrintOptions Print
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(Print));
    } = NegativePrintOptions.Default;
}
