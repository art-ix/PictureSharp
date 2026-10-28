using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Histogram
{
    /// <summary>Bins per channel at full resolution on 16-bit pixels.</summary>
    public const int FullResolution16 = ushort.MaxValue + 1;

    /// <summary>
    /// Adds <paramref name="pixels"/> to <paramref name="counts"/> (<see cref="Channels"/> × <paramref name="binCount"/>)
    /// and returns how many were counted. 8-bit pixels always use 256 bins; 16-bit pixels use one bin per value
    /// (<see cref="FullResolution16"/>) or 256 bins, the nearest <c>floor(v · 255 / 65535 + 0.5)</c>. Luma is BT.601 at
    /// the storage depth before binning.
    /// </summary>
    public static long Accumulate<TPixel>(ReadOnlySpan<TPixel> pixels, Span<long> counts, int binCount, bool excludeTransparent,
        CancellationToken cancellationToken) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            return Accumulate(MemoryMarshal.Cast<TPixel, Rgba32>(pixels), counts, excludeTransparent, cancellationToken);
        }

        if (typeof(TPixel) != typeof(Rgba64))
        {
            throw new UnreachableException();
        }

        ReadOnlySpan<Rgba64> wide = MemoryMarshal.Cast<TPixel, Rgba64>(pixels);
        return binCount == FullResolution16
            ? Accumulate<FullBins>(wide, counts, excludeTransparent, cancellationToken)
            : Accumulate<NarrowBins>(wide, counts, excludeTransparent, cancellationToken);
    }

    private static long Accumulate<TBins>(ReadOnlySpan<Rgba64> pixels, Span<long> counts, bool excludeTransparent, CancellationToken cancellationToken)
        where TBins : struct, IBins
    {
        cancellationToken.ThrowIfCancellationRequested();
        int bins = TBins.Count;
        // 32-bit counters, as for 8 bits: a frame holds fewer than 2^31 pixels.
        int[] rented = ArrayPool<int>.Shared.Rent(Channels * bins);
        try
        {
            Span<int> local = rented.AsSpan(0, Channels * bins);
            local.Clear();
            Span<int> r = local[..bins], g = local.Slice(bins, bins), b = local.Slice(2 * bins, bins);
            Span<int> a = local.Slice(3 * bins, bins), luma = local.Slice(4 * bins, bins);
            long counted = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if ((i & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Rgba64 p = pixels[i];
                if (excludeTransparent && p.A == 0)
                {
                    continue;
                }

                r[TBins.Bin(p.R)]++;
                g[TBins.Bin(p.G)]++;
                b[TBins.Bin(p.B)]++;
                a[TBins.Bin(p.A)]++;
                luma[TBins.Bin(PixelConverter.Luma16(p.R, p.G, p.B))]++;
                counted++;
            }

            for (int k = 0; k < local.Length; k++)
            {
                counts[k] += local[k];
            }

            return counted;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    private interface IBins
    {
        static abstract int Count { get; }

        static abstract int Bin(ushort value);
    }

    private readonly struct FullBins : IBins
    {
        public static int Count => FullResolution16;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Bin(ushort value) => value;
    }

    // floor(v · 255 / 65535 + 0.5) = floor((v + 128.5) / 257), which for integers equals the 8-bit narrowing.
    private readonly struct NarrowBins : IBins
    {
        public static int Count => 256;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Bin(ushort value) => Rgba64.Narrow(value);
    }
}
