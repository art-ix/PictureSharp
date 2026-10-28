using System.Buffers;

namespace PictureSharp.Algorithms.Processing;

internal static partial class AutoTone
{
    /// <summary>
    /// <see cref="AutoContrast(Span{byte}, ReadOnlySpan{long}, double, double, bool)"/> for 16-bit images: the same
    /// cuts on 65536 counts per channel (the full-resolution layout of <see cref="Histogram"/>), then the exact linear
    /// stretch <c>65535 · (v − low) / (high − low)</c> rounded half up instead of Pillow's truncation. A channel without a range keeps the identity that
    /// <paramref name="lut"/> must already hold.
    /// </summary>
    public static void AutoContrast(Span<ushort> lut, ReadOnlySpan<long> counts, double cutoffLow, double cutoffHigh, bool preserveTone)
    {
        const int N = ToneLut.Channel16;
        double[] rented = ArrayPool<double>.Shared.Rent(N);
        try
        {
            Span<double> h = rented.AsSpan(0, N);
            for (int c = 0; c < 3; c++)
            {
                int layer = preserveTone ? 4 : c;
                ReadOnlySpan<long> source = counts.Slice(layer * N, N);
                for (int i = 0; i < N; i++)
                {
                    h[i] = source[i];
                }

                Span<ushort> map = lut.Slice(c * N, N);
                if (ContrastRange(h, cutoffLow, cutoffHigh) is not (int low, int high))
                {
                    continue;
                }

                long range = high - low;
                for (int v = 0; v < N; v++)
                {
                    // Exact rational rounding (2 · 65535 · (v − low) + range) / (2 · range); below low the quotient is
                    // at most 0 and above high at least 65535, so truncation towards zero does not matter after clamping.
                    long scaled = ((2L * ushort.MaxValue * (v - low)) + range) / (2 * range);
                    map[v] = (ushort)Math.Clamp(scaled, 0, ushort.MaxValue);
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// <see cref="Equalize(Span{byte}, ReadOnlySpan{long})"/> for 16-bit images on 65536 counts per channel: each value v
    /// maps to <c>round(65535 · C(v) / (total − last))</c>, clamped, where C(v) counts the samples below v and last is
    /// the count of the highest present value, Pillow's formula without its integer step (which would leave images of
    /// fewer than 65535 pixels unchanged). Unequalizable channels keep the identity that <paramref name="lut"/> holds.
    /// </summary>
    public static void Equalize(Span<ushort> lut, ReadOnlySpan<long> counts)
    {
        const int N = ToneLut.Channel16;
        for (int c = 0; c < 3; c++)
        {
            ReadOnlySpan<long> h = counts.Slice(c * N, N);
            long total = 0, last = 0;
            int nonZero = 0;
            for (int i = 0; i < N; i++)
            {
                if (h[i] != 0)
                {
                    total += h[i];
                    last = h[i];
                    nonZero++;
                }
            }

            long spread = total - last;
            if (nonZero <= 1 || spread == 0)
            {
                continue;
            }

            Span<ushort> map = lut.Slice(c * N, N);
            Int128 below = 0;
            for (int v = 0; v < N; v++)
            {
                Int128 scaled = ((2 * ushort.MaxValue * below) + spread) / (2 * (Int128)spread);
                map[v] = (ushort)(long)Int128.Min(scaled, ushort.MaxValue);
                below += h[v];
            }
        }
    }
}
