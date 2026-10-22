namespace PictureSharp.Processing;

/// <summary>
/// How a negative is turned into a positive: the color of the bare film base, the density range of each channel and a
/// midtone gamma per channel. <see cref="ImageAnalysisExtensions.AnalyzeNegative(Image, NegativeOptions?)"/> measures
/// it; pass one calibration to every frame of a roll (see <see cref="Combine"/>) so they look alike.
/// </summary>
/// <remarks>
/// A pixel's channel value v is decoded to linear light L, its density above the base is
/// <c>d = log10(FilmBase / max(L, 1/1024))</c>, and <c>t = clamp(d / DensityRange, 0, 1.05)^MidtoneGamma</c> is
/// the share of the range the pixel reaches (0 is the film base, which prints black).
/// </remarks>
public sealed record NegativeCalibration
{
    /// <summary>The kind of film; a black-and-white calibration has the same values in every channel. Default: color.</summary>
    public FilmType FilmType
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(FilmType), value, "Unknown film type.");
    }

    /// <summary>The linear light (above 0, at most 1) that passes the bare film base in each channel.</summary>
    public required ChannelValues FilmBase
    {
        get;
        init => field = IsInRange(value, 0f, 1f, minimumInclusive: false) ? value : throw new ArgumentOutOfRangeException(nameof(FilmBase), value, "Every channel must be above 0 and at most 1.");
    }

    /// <summary>The density above the base (decades, from 0.05 to 6) that prints white in each channel.</summary>
    public required ChannelValues DensityRange
    {
        get;
        init => field = IsInRange(value, 0.05f, 6f, minimumInclusive: true) ? value : throw new ArgumentOutOfRangeException(nameof(DensityRange), value, "Every channel must be between 0.05 and 6.");
    }

    /// <summary>
    /// The exponent (from 0.2 to 5) applied to each channel's share of the density range, which balances the midtones
    /// between the channels. Default: 1 in every channel.
    /// </summary>
    public ChannelValues MidtoneGamma
    {
        get;
        init => field = IsInRange(value, 0.2f, 5f, minimumInclusive: true) ? value : throw new ArgumentOutOfRangeException(nameof(MidtoneGamma), value, "Every channel must be between 0.2 and 5.");
    } = new(1f, 1f, 1f);

    /// <summary>
    /// One calibration for several frames of the same roll: the median film base and midtone gamma and the largest
    /// density range of each channel.
    /// </summary>
    /// <exception cref="ArgumentException">No calibration is given, or their film types differ.</exception>
    public static NegativeCalibration Combine(IEnumerable<NegativeCalibration> calibrations)
    {
        ArgumentNullException.ThrowIfNull(calibrations);
        NegativeCalibration[] all = [.. calibrations];
        if (all.Length == 0)
        {
            throw new ArgumentException("At least one calibration is required.", nameof(calibrations));
        }

        if (all.Any(c => c is null || c.FilmType != all[0].FilmType))
        {
            throw new ArgumentException("Every calibration must be present and of the same film type.", nameof(calibrations));
        }

        return new NegativeCalibration
        {
            FilmType = all[0].FilmType,
            FilmBase = Per(all, static (c, i) => c.FilmBase[i], Median),
            DensityRange = Per(all, static (c, i) => c.DensityRange[i], static values => values.Max()),
            MidtoneGamma = Per(all, static (c, i) => c.MidtoneGamma[i], Median),
        };
    }

    private static ChannelValues Per(NegativeCalibration[] all, Func<NegativeCalibration, int, float> pick, Func<float[], float> combine) =>
        new(combine([.. all.Select(c => pick(c, 0))]), combine([.. all.Select(c => pick(c, 1))]), combine([.. all.Select(c => pick(c, 2))]));

    // The lower median, so the result is one of the inputs.
    private static float Median(float[] values)
    {
        Array.Sort(values);
        return values[(values.Length - 1) / 2];
    }

    private static bool IsInRange(ChannelValues v, float minimum, float maximum, bool minimumInclusive)
    {
        return In(v.Red) && In(v.Green) && In(v.Blue);

        bool In(float x) => (minimumInclusive ? x >= minimum : x > minimum) && x <= maximum;
    }
}
