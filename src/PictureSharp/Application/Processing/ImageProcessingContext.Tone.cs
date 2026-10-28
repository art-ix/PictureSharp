using System.Drawing;
using PictureSharp.Algorithms.Processing;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Remaps the selected channels with input and output levels and a midtone gamma (see <see cref="LevelsOptions"/>).
    /// Every pixel is mapped, including fully transparent ones; alpha is kept.
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="LevelsOptions.InputBlack"/> is not below <see cref="LevelsOptions.InputWhite"/>.</exception>
    public ImageProcessingContext Levels(LevelsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.InputBlack >= options.InputWhite)
        {
            throw new ArgumentException("InputBlack must be below InputWhite.", nameof(options));
        }

        Span<byte> map = stackalloc byte[256];
        ToneLut.Levels(map, options);
        byte[] lut = ToneLut.CreateIdentity();
        ToneLut.SetChannels(lut, map, options.Channels);
        return ApplyToneLut(lut);
    }

    /// <summary>
    /// Applies a power curve <c>x' = 255 · (x / 255)^(1 / gamma)</c> to the selected channels, as ImageMagick's
    /// <c>-gamma</c>: values above 1 brighten, below 1 darken. Alpha is kept.
    /// </summary>
    /// <param name="gamma">Finite and greater than 0; 1 keeps the image.</param>
    /// <param name="channels">The channels to change.</param>
    public ImageProcessingContext Gamma(float gamma, ColorChannels channels = ColorChannels.Rgb) =>
        Levels(new LevelsOptions { Gamma = gamma, Channels = channels });

    /// <summary>
    /// Remaps the selected channels through a smooth curve that passes through <paramref name="points"/> (input X,
    /// output Y, both 0–255), like the Curves tool of a photo editor. The curve is the shape-preserving piecewise
    /// cubic (PCHIP, as SciPy's <c>PchipInterpolator</c>), so it does not overshoot between points; outside the first
    /// and last point it stays constant. Alpha is kept.
    /// </summary>
    /// <param name="points">At least two points with finite coordinates from 0 to 255 and strictly increasing X.</param>
    /// <param name="channels">The channels to change; call again with another channel for a separate curve.</param>
    /// <example>
    /// <code>image.Mutate(x => x.Curves([new(0, 0), new(64, 52), new(192, 206), new(255, 255)]));   // gentle S</code>
    /// </example>
    /// <exception cref="ArgumentException">Fewer than two points, or X not strictly increasing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside 0–255, or <paramref name="channels"/> selects no color channel.</exception>
    public ImageProcessingContext Curves(ReadOnlySpan<PointF> points, ColorChannels channels = ColorChannels.Rgb)
    {
        if (points.Length < 2)
        {
            throw new ArgumentException("A curve needs at least two points.", nameof(points));
        }

        for (int i = 0; i < points.Length; i++)
        {
            PointF p = points[i];
            if (!(p.X is >= 0f and <= 255f) || !(p.Y is >= 0f and <= 255f))
            {
                throw new ArgumentOutOfRangeException(nameof(points), p, "Expected coordinates between 0 and 255.");
            }

            if (i > 0 && p.X <= points[i - 1].X)
            {
                throw new ArgumentException("The X coordinates must be strictly increasing.", nameof(points));
            }
        }

        if (channels is ColorChannels.None || (channels & ~ColorChannels.Rgb) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Expected a non-empty combination of Red, Green and Blue.");
        }

        Span<byte> map = stackalloc byte[256];
        ToneCurve.Fill(map, points);
        byte[] lut = ToneLut.CreateIdentity();
        ToneLut.SetChannels(lut, map, channels);
        return ApplyToneLut(lut);
    }

    /// <summary>
    /// Stretches each color channel so that its darkest value becomes 0 and its lightest 255, after discarding
    /// <paramref name="cutoff"/> percent of the darkest and of the lightest values, as Pillow's
    /// <c>ImageOps.autocontrast</c>. Alpha is kept.
    /// </summary>
    /// <param name="cutoff">Percentage (0–100) of values to discard at each end.</param>
    /// <param name="preserveTone">
    /// Whether to derive one mapping from the luminance and apply it to all channels, which keeps the hue
    /// (Pillow's <c>preserve_tone</c>, Photoshop-like) instead of stretching each channel separately.
    /// </param>
    /// <remarks>
    /// The histogram counts every pixel, including fully transparent ones. An animation is analysed as a whole and
    /// every frame gets the same mapping, so the frames do not flicker; a single image matches Pillow exactly.
    /// </remarks>
    public ImageProcessingContext AutoContrast(float cutoff = 0f, bool preserveTone = false) => AutoContrast(cutoff, cutoff, preserveTone);

    /// <summary>
    /// Stretches each color channel as <see cref="AutoContrast(float, bool)"/>, discarding <paramref name="cutoffLow"/> percent
    /// of the darkest and <paramref name="cutoffHigh"/> percent of the lightest values.
    /// </summary>
    public ImageProcessingContext AutoContrast(float cutoffLow, float cutoffHigh, bool preserveTone = false)
    {
        CheckPercent(cutoffLow, nameof(cutoffLow));
        CheckPercent(cutoffHigh, nameof(cutoffHigh));
        long[] counts = CountAllFrames();
        byte[] lut = new byte[ToneLut.Length];
        AutoTone.AutoContrast(lut, counts, cutoffLow, cutoffHigh, preserveTone);
        return ApplyToneLut(lut);
    }

    /// <summary>
    /// Equalizes the histogram of each color channel separately, spreading the values evenly over 0–255, as Pillow's
    /// <c>ImageOps.equalize</c>. Alpha is kept; the histogram counts every pixel and an animation shares one mapping
    /// (see <see cref="AutoContrast(float, bool)"/>).
    /// </summary>
    public ImageProcessingContext Equalize()
    {
        long[] counts = CountAllFrames();
        byte[] lut = new byte[ToneLut.Length];
        AutoTone.Equalize(lut, counts);
        return ApplyToneLut(lut);
    }

    private long[] CountAllFrames()
    {
        long[] counts = new long[Histogram.Length];
        foreach (ImageFrame frame in Target.Frames)
        {
            _state.CancellationToken.ThrowIfCancellationRequested();
            Histogram.Accumulate(frame.Pixels.AsSpan(), counts, excludeTransparent: false, _state.CancellationToken);
        }

        return counts;
    }

    private static void CheckPercent(float value, string paramName)
    {
        if (!(value is >= 0f and <= 100f))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Expected a percentage between 0 and 100.");
        }
    }

    private ImageProcessingContext ApplyToneLut(byte[] lut) =>
        ToneLut.IsIdentity(lut) ? this : ForEachPixelSpan((Lut: lut, Token: _state.CancellationToken), static (pixels, state) => ToneLut.Apply(pixels, state.Lut, state.Token));
}
