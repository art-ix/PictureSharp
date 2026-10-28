namespace PictureSharp.Processing;

/// <summary>
/// Counts of the red, green, blue, alpha and luminance values of an image, as returned by
/// <see cref="ImageAnalysisExtensions.GetHistogram(Image, HistogramOptions?)"/>, in <see cref="BinCount"/> bins per
/// channel. Bin i represents the normalized value <c>i / (BinCount − 1)</c>; a sample v of an image with maximum M
/// (255 or 65535) is counted in the nearest bin, <c>floor(v · (BinCount − 1) / M + 0.5)</c>, so 256 bins on an 8-bit
/// image and 65536 on a 16-bit one count every value exactly. The histogram is a snapshot; it does not change with the
/// image.
/// </summary>
public sealed class ImageHistogram
{
    private const int ChannelCount = 5;
    private readonly long[] _counts;

    internal ImageHistogram(long[] counts, long pixelCount, int binCount)
    {
        _counts = counts;
        PixelCount = pixelCount;
        BinCount = binCount;
    }

    /// <summary>The number of counted pixels; every channel's counts add up to it.</summary>
    public long PixelCount { get; }

    /// <summary>
    /// The number of bins per channel: 256, or with <see cref="HistogramOptions.FullResolution"/> one per sample value
    /// (65536 on a 16-bit image).
    /// </summary>
    public int BinCount { get; }

    /// <summary>Counts of the red values, <see cref="BinCount"/> long.</summary>
    public ReadOnlySpan<long> Red => GetCounts(HistogramChannel.Red);

    /// <summary>Counts of the green values, <see cref="BinCount"/> long.</summary>
    public ReadOnlySpan<long> Green => GetCounts(HistogramChannel.Green);

    /// <summary>Counts of the blue values, <see cref="BinCount"/> long.</summary>
    public ReadOnlySpan<long> Blue => GetCounts(HistogramChannel.Blue);

    /// <summary>Counts of the alpha values, <see cref="BinCount"/> long.</summary>
    public ReadOnlySpan<long> Alpha => GetCounts(HistogramChannel.Alpha);

    /// <summary>Counts of the BT.601 luma values (computed at the image's depth), <see cref="BinCount"/> long.</summary>
    public ReadOnlySpan<long> Luminance => GetCounts(HistogramChannel.Luminance);

    /// <summary>The <see cref="BinCount"/> counts of <paramref name="channel"/>.</summary>
    public ReadOnlySpan<long> GetCounts(HistogramChannel channel) => _counts.AsSpan(Index(channel) * BinCount, BinCount);

    /// <summary>
    /// The mean normalized value (0–1) of <paramref name="channel"/>, from the bin representatives
    /// <c>i / (BinCount − 1)</c>, or <see cref="double.NaN"/> when no pixel was counted. With 256 bins on a 16-bit
    /// image it is approximate.
    /// </summary>
    public double MeanNormalized(HistogramChannel channel)
    {
        ReadOnlySpan<long> counts = GetCounts(channel);
        double sum = 0;
        for (int v = 0; v < counts.Length; v++)
        {
            sum += (double)v * counts[v];
        }

        return PixelCount == 0 ? double.NaN : sum / PixelCount / (BinCount - 1);
    }

    /// <summary>
    /// The smallest normalized value (0–1, a bin representative <c>i / (BinCount − 1)</c>) of <paramref name="channel"/>
    /// with at least <paramref name="percent"/>% of the pixels at or below it (nearest rank): 0 gives the minimum, 50
    /// the median and 100 the maximum.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="percent">A percentage from 0 to 100.</param>
    /// <exception cref="InvalidOperationException">No pixel was counted.</exception>
    public double PercentileNormalized(HistogramChannel channel, double percent)
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
        for (int v = 0; v < counts.Length; v++)
        {
            cumulative += counts[v];
            if (cumulative >= rank)
            {
                return v / (double)(BinCount - 1);
            }
        }

        return 1.0;
    }

    private static int Index(HistogramChannel channel) =>
        (uint)channel < ChannelCount ? (int)channel : throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown histogram channel.");
}
