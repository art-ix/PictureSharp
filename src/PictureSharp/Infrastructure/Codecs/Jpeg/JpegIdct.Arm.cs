using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal static partial class JpegIdct
{
    /// <summary>
    /// <see cref="TransformAvx2"/> for ARM64 on pairs of 128-bit halves (columns 0-3 and 4-7). Returns false, leaving
    /// the work to the scalar code, when a block's values could overflow 32-bit lanes.
    /// </summary>
    private static bool TransformArm(ReadOnlySpan<short> block, ReadOnlySpan<short> quant, Span<byte> output, int stride)
    {
        ref short b = ref MemoryMarshal.GetReference(block[..64]);
        ref short q = ref MemoryMarshal.GetReference(quant[..64]);
        JpegFdct.Block8x8 m = default;
        Dequantize(ref b, ref q, 0, out m.L0, out m.H0);
        Dequantize(ref b, ref q, 8, out m.L1, out m.H1);
        Dequantize(ref b, ref q, 16, out m.L2, out m.H2);
        Dequantize(ref b, ref q, 24, out m.L3, out m.H3);
        Dequantize(ref b, ref q, 32, out m.L4, out m.H4);
        Dequantize(ref b, ref q, 40, out m.L5, out m.H5);
        Dequantize(ref b, ref q, 48, out m.L6, out m.H6);
        Dequantize(ref b, ref q, 56, out m.L7, out m.H7);
        if (!InRange(in m))
        {
            return false;
        }

        // Pass 1: columns (row k in each half). Pass 2: rows, after a transpose; transposed back for storing.
        Pass(ref m.L0, ref m.L1, ref m.L2, ref m.L3, ref m.L4, ref m.L5, ref m.L6, ref m.L7, ConstBits - Pass1Bits);
        Pass(ref m.H0, ref m.H1, ref m.H2, ref m.H3, ref m.H4, ref m.H5, ref m.H6, ref m.H7, ConstBits - Pass1Bits);
        if (!InRange(in m))
        {
            return false;
        }

        m.Transpose();
        Pass(ref m.L0, ref m.L1, ref m.L2, ref m.L3, ref m.L4, ref m.L5, ref m.L6, ref m.L7, ConstBits + Pass1Bits + 3);
        Pass(ref m.H0, ref m.H1, ref m.H2, ref m.H3, ref m.H4, ref m.H5, ref m.H6, ref m.H7, ConstBits + Pass1Bits + 3);
        m.Transpose();

        _ = output[(7 * stride) + 7];
        ref byte o = ref MemoryMarshal.GetReference(output);
        Store(ref o, RangeLimit8(m.L0, m.H0));
        Store(ref Unsafe.Add(ref o, stride), RangeLimit8(m.L1, m.H1));
        Store(ref Unsafe.Add(ref o, 2 * stride), RangeLimit8(m.L2, m.H2));
        Store(ref Unsafe.Add(ref o, 3 * stride), RangeLimit8(m.L3, m.H3));
        Store(ref Unsafe.Add(ref o, 4 * stride), RangeLimit8(m.L4, m.H4));
        Store(ref Unsafe.Add(ref o, 5 * stride), RangeLimit8(m.L5, m.H5));
        Store(ref Unsafe.Add(ref o, 6 * stride), RangeLimit8(m.L6, m.H6));
        Store(ref Unsafe.Add(ref o, 7 * stride), RangeLimit8(m.L7, m.H7));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Dequantize(ref short block, ref short quant, int offset, out Vector128<int> low, out Vector128<int> high)
    {
        Vector128<short> b = Vector128.LoadUnsafe(ref block, (nuint)offset), q = Vector128.LoadUnsafe(ref quant, (nuint)offset);
        low = Vector128.WidenLower(b) * Vector128.WidenLower(q);
        high = Vector128.WidenUpper(b) * Vector128.WidenUpper(q);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InRange(in JpegFdct.Block8x8 m)
    {
        Vector128<int> low = Vector128.Max(Vector128.Max(Vector128.Max(Vector128.Abs(m.L0), Vector128.Abs(m.L1)), Vector128.Max(Vector128.Abs(m.L2), Vector128.Abs(m.L3))),
            Vector128.Max(Vector128.Max(Vector128.Abs(m.L4), Vector128.Abs(m.L5)), Vector128.Max(Vector128.Abs(m.L6), Vector128.Abs(m.L7))));
        Vector128<int> high = Vector128.Max(Vector128.Max(Vector128.Max(Vector128.Abs(m.H0), Vector128.Abs(m.H1)), Vector128.Max(Vector128.Abs(m.H2), Vector128.Abs(m.H3))),
            Vector128.Max(Vector128.Max(Vector128.Abs(m.H4), Vector128.Abs(m.H5)), Vector128.Max(Vector128.Abs(m.H6), Vector128.Abs(m.H7))));

        // Abs(int.MinValue) stays negative, so compare as unsigned.
        return Vector128.LessThanAll(Vector128.Max(low.AsUInt32(), high.AsUInt32()), Vector128.Create((uint)SimdInputLimit));
    }

    // port: libjpeg-turbo jidctint.c jpeg_idct_islow, one pass on four lanes (d0..d7 are the eight inputs)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass(ref Vector128<int> d0, ref Vector128<int> d1, ref Vector128<int> d2, ref Vector128<int> d3,
        ref Vector128<int> d4, ref Vector128<int> d5, ref Vector128<int> d6, ref Vector128<int> d7, int shift)
    {
        Vector128<int> z1 = (d2 + d6) * Vector128.Create((int)Fix0541196100);
        Vector128<int> tmp2 = z1 + (d6 * Vector128.Create((int)-Fix1847759065));
        Vector128<int> tmp3 = z1 + (d2 * Vector128.Create((int)Fix0765366865));
        Vector128<int> tmp0 = (d0 + d4) << ConstBits;
        Vector128<int> tmp1 = (d0 - d4) << ConstBits;
        Vector128<int> tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

        // Odd part per figure 8 of the IJG notes: y7, y5, y3, y1.
        tmp0 = d7;
        tmp1 = d5;
        tmp2 = d3;
        tmp3 = d1;
        z1 = tmp0 + tmp3;
        Vector128<int> z2 = tmp1 + tmp2, z3 = tmp0 + tmp2, z4 = tmp1 + tmp3;
        Vector128<int> z5 = (z3 + z4) * Vector128.Create((int)Fix1175875602);
        tmp0 *= Vector128.Create((int)Fix0298631336);
        tmp1 *= Vector128.Create((int)Fix2053119869);
        tmp2 *= Vector128.Create((int)Fix3072711026);
        tmp3 *= Vector128.Create((int)Fix1501321110);
        z1 *= Vector128.Create((int)-Fix0899976223);
        z2 *= Vector128.Create((int)-Fix2562915447);
        z3 = (z3 * Vector128.Create((int)-Fix1961570560)) + z5;
        z4 = (z4 * Vector128.Create((int)-Fix0390180644)) + z5;
        tmp0 += z1 + z3;
        tmp1 += z2 + z4;
        tmp2 += z2 + z3;
        tmp3 += z1 + z4;

        Vector128<int> round = Vector128.Create(1 << (shift - 1));
        d0 = (tmp10 + tmp3 + round) >> shift;
        d7 = (tmp10 - tmp3 + round) >> shift;
        d1 = (tmp11 + tmp2 + round) >> shift;
        d6 = (tmp11 - tmp2 + round) >> shift;
        d2 = (tmp12 + tmp1 + round) >> shift;
        d5 = (tmp12 - tmp1 + round) >> shift;
        d3 = (tmp13 + tmp0 + round) >> shift;
        d4 = (tmp13 - tmp0 + round) >> shift;
    }

    // RangeLimit8 for a row held as two halves.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> RangeLimit8(Vector128<int> low, Vector128<int> high)
    {
        Vector128<short> words = Vector128.Narrow(RangeLimit4(low), RangeLimit4(high));
        return Vector128.Narrow(words.AsUInt16(), words.AsUInt16());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> RangeLimit4(Vector128<int> values)
    {
        Vector128<int> j = (values + Vector128.Create(128)) & Vector128.Create(1023);
        return Vector128.ConditionalSelect(Vector128.LessThan(j, Vector128.Create(256)), j,
            Vector128.ConditionalSelect(Vector128.LessThan(j, Vector128.Create(640)), Vector128.Create(255), Vector128<int>.Zero));
    }
}
