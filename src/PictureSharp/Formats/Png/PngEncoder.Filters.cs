using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Png;

/// <remarks>
/// Rows passed to the filters are padded: <c>row</c> and <c>prev</c> start with <c>bpp</c> zero bytes, followed by
/// the packed bytes, so "left" and "upper left" read zeros at the start of a row. The first row uses a zero
/// <c>prev</c>, which matches the PNG definition of filters on the first row.
/// </remarks>
internal sealed partial class PngEncoder
{
    private const int FilterCount = 5;

    /// <summary>Chooses the filter with the smallest sum of residuals taken as signed bytes (first one on ties).</summary>
    internal static int SelectFilter(ReadOnlySpan<byte> row, ReadOnlySpan<byte> prev, int bpp)
    {
        Span<long> costs = stackalloc long[FilterCount];
        costs.Clear();
        int done = FilterCostsSimd(row, prev, bpp, costs);
        FilterCostsScalar(row, prev, bpp, done, costs);
        int best = 0;
        for (int f = 1; f < FilterCount; f++)
        {
            if (costs[f] < costs[best])
            {
                best = f;
            }
        }

        return best;
    }

    /// <summary>Adds the costs of bytes from <paramref name="start"/> on (indices past the padding).</summary>
    internal static void FilterCostsScalar(ReadOnlySpan<byte> row, ReadOnlySpan<byte> prev, int bpp, int start, Span<long> costs)
    {
        int n = row.Length - bpp;
        for (int i = start; i < n; i++)
        {
            int x = row[bpp + i];
            int a = row[i];
            int b = prev[bpp + i];
            int c = prev[i];
            costs[0] += Cost(x);
            costs[1] += Cost(x - a);
            costs[2] += Cost(x - b);
            costs[3] += Cost(x - ((a + b) >> 1));
            costs[4] += Cost(x - PngConstants.Paeth(a, b, c));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Cost(int residual)
    {
        int v = residual & 0xff;
        return v < 128 ? v : 256 - v;
    }

    /// <summary>Writes the filtered bytes of a padded row to <paramref name="dst"/> (unpadded).</summary>
    internal static void ApplyFilter(int filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> prev, int bpp, Span<byte> dst)
    {
        if (filter == 0)
        {
            row[bpp..].CopyTo(dst);
            return;
        }

        int done = ApplyFilterSimd(filter, row, prev, bpp, dst);
        ApplyFilterScalar(filter, row, prev, bpp, done, dst);
    }

    internal static void ApplyFilterScalar(int filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> prev, int bpp, int start, Span<byte> dst)
    {
        int n = row.Length - bpp;
        switch (filter)
        {
            case 1:
                for (int i = start; i < n; i++)
                {
                    dst[i] = (byte)(row[bpp + i] - row[i]);
                }

                break;
            case 2:
                for (int i = start; i < n; i++)
                {
                    dst[i] = (byte)(row[bpp + i] - prev[bpp + i]);
                }

                break;
            case 3:
                for (int i = start; i < n; i++)
                {
                    dst[i] = (byte)(row[bpp + i] - ((row[i] + prev[bpp + i]) >> 1));
                }

                break;
            default:
                for (int i = start; i < n; i++)
                {
                    dst[i] = (byte)(row[bpp + i] - PngConstants.Paeth(row[i], prev[bpp + i], prev[i]));
                }

                break;
        }
    }
}
