using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Png;

internal sealed partial class PngEncoder
{
    /// <summary>Adds the costs of whole 16-byte groups and returns the number of bytes covered.</summary>
    private static int FilterCostsSimd(ReadOnlySpan<byte> row, ReadOnlySpan<byte> prev, int bpp, Span<long> costs)
    {
        int n = row.Length - bpp;
        if (!Vector128.IsHardwareAccelerated || n < Vector128<byte>.Count)
        {
            return 0;
        }

        ref byte r = ref MemoryMarshal.GetReference(row);
        ref byte p = ref MemoryMarshal.GetReference(prev);
        Vector128<uint> sum0 = default, sum1 = default, sum2 = default, sum3 = default, sum4 = default;
        int i = 0;
        while (i + Vector128<byte>.Count <= n)
        {
            // Each step adds at most 2 * 128 to a 16-bit lane, so 255 steps cannot overflow.
            Vector128<ushort> acc0 = default, acc1 = default, acc2 = default, acc3 = default, acc4 = default;
            for (int step = 0; step < 255 && i + Vector128<byte>.Count <= n; step++, i += Vector128<byte>.Count)
            {
                Vector128<byte> x = Vector128.LoadUnsafe(ref r, (nuint)(bpp + i));
                Vector128<byte> a = Vector128.LoadUnsafe(ref r, (nuint)i);
                Vector128<byte> b = Vector128.LoadUnsafe(ref p, (nuint)(bpp + i));
                Vector128<byte> c = Vector128.LoadUnsafe(ref p, (nuint)i);
                acc0 += CostOf(x);
                acc1 += CostOf(x - a);
                acc2 += CostOf(x - b);
                acc3 += CostOf(x - Average(a, b));
                acc4 += CostOf(x - Paeth(a, b, c));
            }

            sum0 += WidenSum(acc0);
            sum1 += WidenSum(acc1);
            sum2 += WidenSum(acc2);
            sum3 += WidenSum(acc3);
            sum4 += WidenSum(acc4);
        }

        costs[0] += Vector128.Sum(sum0);
        costs[1] += Vector128.Sum(sum1);
        costs[2] += Vector128.Sum(sum2);
        costs[3] += Vector128.Sum(sum3);
        costs[4] += Vector128.Sum(sum4);
        return i;
    }

    /// <summary>Filters whole 16-byte groups and returns the number of bytes written.</summary>
    private static int ApplyFilterSimd(int filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> prev, int bpp, Span<byte> dst)
    {
        int n = row.Length - bpp;
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        ref byte r = ref MemoryMarshal.GetReference(row);
        ref byte p = ref MemoryMarshal.GetReference(prev);
        ref byte d = ref MemoryMarshal.GetReference(dst);
        int i = 0;
        for (; i + Vector128<byte>.Count <= n; i += Vector128<byte>.Count)
        {
            Vector128<byte> x = Vector128.LoadUnsafe(ref r, (nuint)(bpp + i));
            Vector128<byte> a = Vector128.LoadUnsafe(ref r, (nuint)i);
            Vector128<byte> b = Vector128.LoadUnsafe(ref p, (nuint)(bpp + i));
            Vector128<byte> predicted = filter switch
            {
                1 => a,
                2 => b,
                3 => Average(a, b),
                _ => Paeth(a, b, Vector128.LoadUnsafe(ref p, (nuint)i)),
            };
            (x - predicted).StoreUnsafe(ref d, (nuint)i);
        }

        return i;
    }

    // min(v, 256 - v) of each byte, widened to pairs of 16-bit sums.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> CostOf(Vector128<byte> residual)
    {
        Vector128<byte> cost = Vector128.Min(residual, Vector128<byte>.Zero - residual);
        (Vector128<ushort> low, Vector128<ushort> high) = Vector128.Widen(cost);
        return low + high;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> WidenSum(Vector128<ushort> value)
    {
        (Vector128<uint> low, Vector128<uint> high) = Vector128.Widen(value);
        return low + high;
    }

    // (a + b) >> 1 without overflow.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Average(Vector128<byte> a, Vector128<byte> b) =>
        (a & b) + Vector128.ShiftRightLogical(a ^ b, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Paeth(Vector128<byte> a, Vector128<byte> b, Vector128<byte> c)
    {
        (Vector128<ushort> aLow, Vector128<ushort> aHigh) = Vector128.Widen(a);
        (Vector128<ushort> bLow, Vector128<ushort> bHigh) = Vector128.Widen(b);
        (Vector128<ushort> cLow, Vector128<ushort> cHigh) = Vector128.Widen(c);
        return Vector128.Narrow(
            Paeth(aLow.AsInt16(), bLow.AsInt16(), cLow.AsInt16()).AsUInt16(),
            Paeth(aHigh.AsInt16(), bHigh.AsInt16(), cHigh.AsInt16()).AsUInt16());
    }

    // port: PngConstants.Paeth on 16-bit lanes; with p = a + b - c: |p - a| = |b - c|, |p - b| = |a - c|.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Paeth(Vector128<short> a, Vector128<short> b, Vector128<short> c)
    {
        Vector128<short> bc = b - c;
        Vector128<short> ac = a - c;
        Vector128<short> pa = Vector128.Abs(bc);
        Vector128<short> pb = Vector128.Abs(ac);
        Vector128<short> pc = Vector128.Abs(bc + ac);
        Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
        Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
        return Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c));
    }
}
