using System.Buffers;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

/// <summary>Counting of channel values into red, green, blue, alpha and luma tables of 256 entries each.</summary>
internal static class Histogram
{
    /// <summary>Length of the counts for all five channels, back to back.</summary>
    public const int Length = 5 * 256;

    /// <summary>Adds <paramref name="pixels"/> to <paramref name="counts"/> and returns how many were counted.</summary>
    public static long Accumulate(ReadOnlySpan<Rgba32> pixels, Span<long> counts, bool excludeTransparent)
    {
        // Neighbouring pixels often share values; alternating between two sets of 32-bit counters breaks the chain of
        // dependent increments on the same entry. A frame holds fewer than 2^31 pixels, so they cannot overflow.
        int[] rented = ArrayPool<int>.Shared.Rent(2 * Length);
        try
        {
            Span<int> even = rented.AsSpan(0, Length);
            Span<int> odd = rented.AsSpan(Length, Length);
            even.Clear();
            odd.Clear();
            int i = 0;
            for (; i + 1 < pixels.Length; i += 2)
            {
                Count(even, pixels[i], excludeTransparent);
                Count(odd, pixels[i + 1], excludeTransparent);
            }

            if (i < pixels.Length)
            {
                Count(even, pixels[i], excludeTransparent);
            }

            for (int k = 0; k < Length; k++)
            {
                counts[k] += (long)even[k] + odd[k];
            }

            // Every counted pixel adds one alpha entry.
            long counted = 0;
            for (int k = 768; k < 1024; k++)
            {
                counted += (long)even[k] + odd[k];
            }

            return counted;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Count(Span<int> counts, Rgba32 p, bool excludeTransparent)
    {
        if (excludeTransparent && p.A == 0)
        {
            return;
        }

        counts[p.R]++;
        counts[256 + p.G]++;
        counts[512 + p.B]++;
        counts[768 + p.A]++;
        counts[1024 + PixelConverter.Luma(p.R, p.G, p.B)]++;
    }
}
