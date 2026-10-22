using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Automatic white balance under the gray-world assumption, measured on the midtones only: pixels that are not fully
/// transparent, have no channel at 0–2 or 253–255 (clipped) and whose luma lies between the 10th and 90th percentile
/// of those pixels. Counts are integers, so the result does not depend on the order of the frames' bands.
/// </summary>
internal static class GrayWorld
{
    /// <summary>Length of the luma counts.</summary>
    public const int LumaLength = 256;

    /// <summary>Length of the red, green and blue counts of the selected pixels, back to back.</summary>
    public const int ChannelLength = 3 * 256;

    /// <summary>Integer Rec. 709 luma of encoded values, rounded half up.</summary>
    public static int Luma(Rgba32 p) => ((2126 * p.R) + (7152 * p.G) + (722 * p.B) + 5000) / 10000;

    /// <summary>Whether <paramref name="p"/> takes part in the measurement at all.</summary>
    public static bool IsCandidate(Rgba32 p) =>
        p.A != 0 && p.R is >= 3 and <= 252 && p.G is >= 3 and <= 252 && p.B is >= 3 and <= 252;

    /// <summary>Adds the luma of the candidate pixels of <paramref name="pixels"/> to <paramref name="counts"/>.</summary>
    public static void CountLuma(ReadOnlySpan<Rgba32> pixels, Span<long> counts, CancellationToken cancellationToken)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Rgba32 p = pixels[i];
            if (IsCandidate(p))
            {
                counts[Luma(p)]++;
            }
        }
    }

    /// <summary>
    /// The luma range of the midtones: the smallest values whose cumulative counts reach 10% and 90% of all, or
    /// <see langword="null"/> when no pixel is a candidate.
    /// </summary>
    public static (int Low, int High)? MidtoneRange(ReadOnlySpan<long> lumaCounts)
    {
        long total = 0;
        foreach (long c in lumaCounts)
        {
            total += c;
        }

        if (total == 0)
        {
            return null;
        }

        int low = -1, high = -1;
        long cumulative = 0;
        for (int y = 0; y < LumaLength; y++)
        {
            cumulative += lumaCounts[y];
            if (low < 0 && cumulative * 10 >= total)
            {
                low = y;
            }

            if (cumulative * 10 >= total * 9)
            {
                high = y;
                break;
            }
        }

        return (low, high);
    }

    /// <summary>Adds the channel values of the candidate pixels with luma in <paramref name="range"/> to <paramref name="counts"/>.</summary>
    public static void CountChannels(ReadOnlySpan<Rgba32> pixels, (int Low, int High) range, Span<long> counts, CancellationToken cancellationToken)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Rgba32 p = pixels[i];
            if (IsCandidate(p) && Luma(p) is int y && y >= range.Low && y <= range.High)
            {
                counts[p.R]++;
                counts[256 + p.G]++;
                counts[512 + p.B]++;
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="lut"/> with gains <c>(Y / M)^strength</c> per channel in linear light, where M is the mean
    /// linear value of the counted pixels and Y its Rec. 709 luma, scaled so that the mean keeps its luminance.
    /// Returns false (and leaves the table) when nothing was counted.
    /// </summary>
    public static bool Lut(Span<byte> lut, ReadOnlySpan<long> channelCounts, float strength)
    {
        Span<float> mean = stackalloc float[3];
        for (int c = 0; c < 3; c++)
        {
            long n = 0;
            double sum = 0;
            for (int v = 0; v < 256; v++)
            {
                long count = channelCounts[(c * 256) + v];
                n += count;
                sum += count * (double)SrgbTransfer.Decode((byte)v);
            }

            if (n == 0)
            {
                return false;
            }

            mean[c] = (float)(sum / n);
        }

        float luma = (PhotoAdjust.LumaR * mean[0]) + (PhotoAdjust.LumaG * mean[1]) + (PhotoAdjust.LumaB * mean[2]);
        float r = DeterministicMath.Pow(luma / mean[0], strength);
        float g = DeterministicMath.Pow(luma / mean[1], strength);
        float b = DeterministicMath.Pow(luma / mean[2], strength);
        float normalize = luma / ((PhotoAdjust.LumaR * r * mean[0]) + (PhotoAdjust.LumaG * g * mean[1]) + (PhotoAdjust.LumaB * b * mean[2]));
        PhotoAdjust.ChannelGainsLut(lut, r * normalize, g * normalize, b * normalize);
        return true;
    }
}
