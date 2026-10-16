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
            Histogram.Accumulate(frame.GetPixelSpan(), counts, excludeTransparent: false, _state.CancellationToken);
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
