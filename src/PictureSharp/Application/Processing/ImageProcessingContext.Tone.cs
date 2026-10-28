using System.Buffers;
using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Application.Processing;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Remaps the selected channels with normalized input and output levels and a midtone gamma (see
    /// <see cref="LevelsOptions"/>). Every pixel is mapped, including fully transparent ones; alpha is kept.
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="LevelsOptions.InputBlackNormalized"/> is not below <see cref="LevelsOptions.InputWhiteNormalized"/>.</exception>
    public ImageProcessingContext Levels(LevelsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.InputBlackNormalized >= options.InputWhiteNormalized)
        {
            throw new ArgumentException("InputBlackNormalized must be below InputWhiteNormalized.", nameof(options));
        }

        return MapChannels(new LevelsMaps(options));
    }

    /// <summary>
    /// Applies a power curve <c>x' = x^(1 / gamma)</c> (x normalized to 0–1) to the selected channels, as ImageMagick's
    /// <c>-gamma</c>: values above 1 brighten, below 1 darken. Alpha is kept.
    /// </summary>
    /// <param name="gamma">Finite and greater than 0; 1 keeps the image.</param>
    /// <param name="channels">The channels to change.</param>
    public ImageProcessingContext Gamma(float gamma, ColorChannels channels = ColorChannels.Rgb) =>
        Levels(new LevelsOptions { Gamma = gamma, Channels = channels });

    /// <summary>
    /// Remaps the selected channels through a smooth curve that passes through <paramref name="points"/> (input X,
    /// output Y, both normalized: 0 is black and 1 white at any depth, so an 8-bit level v is <c>v / 255f</c>), like
    /// the Curves tool of a photo editor. The curve is the shape-preserving piecewise cubic (PCHIP, as SciPy's
    /// <c>PchipInterpolator</c>), so it does not overshoot between points; outside the first and last point it stays
    /// constant. It is evaluated in double precision at every sample value of the image's depth. Alpha is kept.
    /// </summary>
    /// <param name="points">At least two points with coordinates from 0 to 1 and strictly increasing X.</param>
    /// <param name="channels">The channels to change; call again with another channel for a separate curve.</param>
    /// <example>
    /// <code>image.Mutate(x => x.CurvesNormalized([new(0, 0), new(0.25f, 0.2f), new(0.75f, 0.8f), new(1, 1)]));   // gentle S</code>
    /// </example>
    /// <exception cref="ArgumentException">Fewer than two points, or X not strictly increasing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is NaN or outside 0–1, or <paramref name="channels"/> selects no color channel.</exception>
    public ImageProcessingContext CurvesNormalized(ReadOnlySpan<PointF> points, ColorChannels channels = ColorChannels.Rgb)
    {
        if (points.Length < 2)
        {
            throw new ArgumentException("A curve needs at least two points.", nameof(points));
        }

        for (int i = 0; i < points.Length; i++)
        {
            PointF p = points[i];
            if (!(p.X is >= 0f and <= 1f) || !(p.Y is >= 0f and <= 1f))
            {
                throw new ArgumentOutOfRangeException(nameof(points), p, "Expected normalized coordinates between 0 and 1.");
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

        return MapChannels(new CurveMaps(points.ToArray(), channels));
    }

    /// <summary>
    /// Stretches each color channel so that its darkest value becomes black and its lightest white, after discarding
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
    /// every frame gets the same mapping, so the frames do not flicker; a single 8-bit image matches Pillow exactly.
    /// A 16-bit image is counted at full resolution (one bin per value) and stretched exactly, rounding half up.
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
        return MapChannels(new AutoContrastMaps(_state, cutoffLow, cutoffHigh, preserveTone));
    }

    /// <summary>
    /// Equalizes the histogram of each color channel separately, spreading the values evenly over the range, as
    /// Pillow's <c>ImageOps.equalize</c>. Alpha is kept; the histogram counts every pixel and an animation shares one
    /// mapping (see <see cref="AutoContrast(float, bool)"/>). A 16-bit image is counted at full resolution and value v
    /// maps to <c>round(65535 · C(v) / (N − L))</c>, where C(v) counts the samples below v, N all samples and L those
    /// of the highest value.
    /// </summary>
    public ImageProcessingContext Equalize() => MapChannels(new EqualizeMaps(_state));

    /// <summary>
    /// The counts of every frame in the layout of <see cref="Histogram"/>: 256 bins per channel on 8-bit images, one
    /// per value on 16-bit ones.
    /// </summary>
    private static long[] CountAllFrames<TPixel>(ProcessingState state) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        int binCount = TPixel.MaxValue + 1;
        long[] counts = new long[Histogram.Channels * binCount];
        foreach (ImageFrame frame in state.Image.Frames)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            Histogram.Accumulate<TPixel>(frame.Buffer.As<TPixel>(), counts, binCount, excludeTransparent: false, state.CancellationToken);
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

    /// <summary>
    /// Maps the color channels of every frame through tables built once for the image's depth: 256 bytes per channel
    /// for 8-bit images, 65536 values (rented) for 16-bit ones. A table that changes nothing leaves the frames untouched.
    /// </summary>
    private ImageProcessingContext MapChannels<TMaps>(TMaps maps) where TMaps : struct, IChannelMaps
    {
        var operation = new ChannelMapFrames<TMaps>(_state, maps);
        PixelDispatch.Run<ChannelMapFrames<TMaps>, bool>(Target.PixelType, ref operation);
        return this;
    }

    /// <summary>The per-channel maps of a tonal operation; both tables start as the identity.</summary>
    private interface IChannelMaps
    {
        /// <summary>Fills the red, green and blue 8-bit maps (<see cref="ToneLut.Length"/>).</summary>
        void Fill(Span<byte> lut);

        /// <summary>Fills the red, green and blue 16-bit maps (<see cref="ToneLut.Length16"/>).</summary>
        void Fill(Span<ushort> lut);
    }

    private readonly struct ChannelMapFrames<TMaps>(ProcessingState state, TMaps maps) : IPixelOperation<bool>
        where TMaps : struct, IChannelMaps
    {
        public bool Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            if (typeof(TPixel) == typeof(Rgba32))
            {
                byte[] lut = ToneLut.CreateIdentity();
                maps.Fill(lut);
                if (!ToneLut.IsIdentity(lut))
                {
                    foreach (ImageFrame frame in state.WritableFrames)
                    {
                        ToneLut.Apply(frame.Buffer.As<Rgba32>(), lut, state.CancellationToken);
                    }
                }

                return true;
            }

            if (typeof(TPixel) != typeof(Rgba64))
            {
                throw PixelTypes.NotYetSupported();
            }

            ushort[] wide = ToneLut.RentIdentity16();
            try
            {
                Span<ushort> lut = wide.AsSpan(0, ToneLut.Length16);
                maps.Fill(lut);
                if (!ToneLut.IsIdentity(lut))
                {
                    foreach (ImageFrame frame in state.WritableFrames)
                    {
                        ToneLut.Apply(frame.Buffer.As<Rgba64>(), lut, state.CancellationToken);
                    }
                }
            }
            finally
            {
                ToneLut.Return16(wide);
            }

            return true;
        }
    }

    private readonly struct LevelsMaps(LevelsOptions options) : IChannelMaps
    {
        public void Fill(Span<byte> lut)
        {
            Span<byte> map = stackalloc byte[256];
            ToneLut.Levels(map, options, byte.MaxValue);
            ToneLut.SetChannels(lut, map, options.Channels);
        }

        public void Fill(Span<ushort> lut)
        {
            ushort[] map = ArrayPool<ushort>.Shared.Rent(ToneLut.Channel16);
            try
            {
                ToneLut.Levels(map.AsSpan(0, ToneLut.Channel16), options, ushort.MaxValue);
                ToneLut.SetChannels(lut, map.AsSpan(0, ToneLut.Channel16), options.Channels);
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(map);
            }
        }
    }

    private readonly struct CurveMaps(PointF[] points, ColorChannels channels) : IChannelMaps
    {
        public void Fill(Span<byte> lut)
        {
            Span<byte> map = stackalloc byte[256];
            ToneCurve.Fill(map, points, byte.MaxValue);
            ToneLut.SetChannels(lut, map, channels);
        }

        public void Fill(Span<ushort> lut)
        {
            ushort[] map = ArrayPool<ushort>.Shared.Rent(ToneLut.Channel16);
            try
            {
                ToneCurve.Fill(map.AsSpan(0, ToneLut.Channel16), points, ushort.MaxValue);
                ToneLut.SetChannels(lut, map.AsSpan(0, ToneLut.Channel16), channels);
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(map);
            }
        }
    }

    private readonly struct AutoContrastMaps(ProcessingState state, float cutoffLow, float cutoffHigh, bool preserveTone) : IChannelMaps
    {
        public void Fill(Span<byte> lut) => AutoTone.AutoContrast(lut, CountAllFrames<Rgba32>(state), cutoffLow, cutoffHigh, preserveTone);

        public void Fill(Span<ushort> lut) => AutoTone.AutoContrast(lut, CountAllFrames<Rgba64>(state), cutoffLow, cutoffHigh, preserveTone);
    }

    private readonly struct EqualizeMaps(ProcessingState state) : IChannelMaps
    {
        public void Fill(Span<byte> lut) => AutoTone.Equalize(lut, CountAllFrames<Rgba32>(state));

        public void Fill(Span<ushort> lut) => AutoTone.Equalize(lut, CountAllFrames<Rgba64>(state));
    }
}
