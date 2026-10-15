namespace PictureSharp.Processing;

/// <summary>
/// Counts of each 8-bit value of the red, green, blue, alpha and luminance channels, as returned by
/// <see cref="ImageAnalysisExtensions.GetHistogram(Image, HistogramOptions?)"/>. The histogram is a snapshot; it does
/// not change with the image.
/// </summary>
public sealed class ImageHistogram
{
    private const int ChannelCount = 5;
    private readonly long[] _counts;

    internal ImageHistogram(long[] counts, long pixelCount)
    {
        _counts = counts;
        PixelCount = pixelCount;
    }

    /// <summary>The number of counted pixels; every channel's counts add up to it.</summary>
    public long PixelCount { get; }

    /// <summary>Counts of the red values 0–255.</summary>
    public ReadOnlySpan<long> Red => GetCounts(HistogramChannel.Red);

    /// <summary>Counts of the green values 0–255.</summary>
    public ReadOnlySpan<long> Green => GetCounts(HistogramChannel.Green);

    /// <summary>Counts of the blue values 0–255.</summary>
    public ReadOnlySpan<long> Blue => GetCounts(HistogramChannel.Blue);

    /// <summary>Counts of the alpha values 0–255.</summary>
    public ReadOnlySpan<long> Alpha => GetCounts(HistogramChannel.Alpha);

    /// <summary>Counts of the BT.601 luma values 0–255.</summary>
    public ReadOnlySpan<long> Luminance => GetCounts(HistogramChannel.Luminance);

    /// <summary>The 256 counts of <paramref name="channel"/>.</summary>
    public ReadOnlySpan<long> GetCounts(HistogramChannel channel) => _counts.AsSpan(Index(channel) * 256, 256);

    /// <summary>The mean value of <paramref name="channel"/>, or <see cref="double.NaN"/> when no pixel was counted.</summary>
    public double Mean(HistogramChannel channel)
    {
        ReadOnlySpan<long> counts = GetCounts(channel);
        double sum = 0;
        for (int v = 0; v < 256; v++)
        {
            sum += (double)v * counts[v];
        }

        return PixelCount == 0 ? double.NaN : sum / PixelCount;
    }

    /// <summary>
    /// The smallest value of <paramref name="channel"/> with at least <paramref name="percent"/>% of the pixels at or
    /// below it (nearest rank): 0 gives the minimum, 50 the median and 100 the maximum.
    /// </summary>
    /// <exception cref="InvalidOperationException">No pixel was counted.</exception>
    public byte Percentile(HistogramChannel channel, double percent)
    {
        if (!(percent is >= 0 and <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Expected a percentage between 0 and 100.");
        }

        ReadOnlySpan<long> counts = GetCounts(channel);
        if (PixelCount == 0)
        {
            throw new InvalidOperationException("The histogram has no pixels.");
        }

        long rank = Math.Max(1L, (long)Math.Ceiling(percent / 100 * PixelCount));
        long cumulative = 0;
        for (int v = 0; v < 256; v++)
        {
            cumulative += counts[v];
            if (cumulative >= rank)
            {
                return (byte)v;
            }
        }

        return 255;
    }

    private static int Index(HistogramChannel channel) =>
        (uint)channel < ChannelCount ? (int)channel : throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown histogram channel.");
}
