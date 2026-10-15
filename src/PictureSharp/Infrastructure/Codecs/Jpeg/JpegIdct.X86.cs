using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal static partial class JpegIdct
{
    /// <summary>
    /// <see cref="TransformScalar"/> with AVX2 on eight columns (then eight rows) at once. Returns false, leaving
    /// the work to the scalar code, when a block's values could overflow 32-bit lanes.
    /// </summary>
    private static bool TransformAvx2(ReadOnlySpan<short> block, ReadOnlySpan<short> quant, Span<byte> output, int stride)
    {
        ref short b = ref MemoryMarshal.GetReference(block[..64]);
        ref short q = ref MemoryMarshal.GetReference(quant[..64]);
        Vector256<int> r0 = Dequantize(ref b, ref q, 0), r1 = Dequantize(ref b, ref q, 8);
        Vector256<int> r2 = Dequantize(ref b, ref q, 16), r3 = Dequantize(ref b, ref q, 24);
        Vector256<int> r4 = Dequantize(ref b, ref q, 32), r5 = Dequantize(ref b, ref q, 40);
        Vector256<int> r6 = Dequantize(ref b, ref q, 48), r7 = Dequantize(ref b, ref q, 56);
        if (!InRange(r0, r1, r2, r3, r4, r5, r6, r7))
        {
            return false;
        }

        // Pass 1: columns. Vector k is row k, so each lane is one column. The scalar shortcut for a column of
        // zero AC terms gives the same values as the full computation.
        Pass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits - Pass1Bits);
        if (!InRange(r0, r1, r2, r3, r4, r5, r6, r7))
        {
            return false;
        }

        // Pass 2: rows, after a transpose; the outputs are transposed back to rows.
        JpegFdct.Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Pass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits + Pass1Bits + 3);
        JpegFdct.Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        _ = output[(7 * stride) + 7];
        ref byte o = ref MemoryMarshal.GetReference(output);
        Store(ref o, RangeLimit8(r0));
        Store(ref Unsafe.Add(ref o, stride), RangeLimit8(r1));
        Store(ref Unsafe.Add(ref o, 2 * stride), RangeLimit8(r2));
        Store(ref Unsafe.Add(ref o, 3 * stride), RangeLimit8(r3));
        Store(ref Unsafe.Add(ref o, 4 * stride), RangeLimit8(r4));
        Store(ref Unsafe.Add(ref o, 5 * stride), RangeLimit8(r5));
        Store(ref Unsafe.Add(ref o, 6 * stride), RangeLimit8(r6));
        Store(ref Unsafe.Add(ref o, 7 * stride), RangeLimit8(r7));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Dequantize(ref short block, ref short quant, int offset) =>
        Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref block, (nuint)offset)) *
        Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref quant, (nuint)offset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InRange(Vector256<int> r0, Vector256<int> r1, Vector256<int> r2, Vector256<int> r3,
        Vector256<int> r4, Vector256<int> r5, Vector256<int> r6, Vector256<int> r7)
    {
        Vector256<int> max = Vector256.Max(Vector256.Max(Vector256.Max(Vector256.Abs(r0), Vector256.Abs(r1)), Vector256.Max(Vector256.Abs(r2), Vector256.Abs(r3))),
            Vector256.Max(Vector256.Max(Vector256.Abs(r4), Vector256.Abs(r5)), Vector256.Max(Vector256.Abs(r6), Vector256.Abs(r7))));

        // Abs(int.MinValue) stays negative, so compare as unsigned.
        return Vector256.LessThanAll(max.AsUInt32(), Vector256.Create((uint)SimdInputLimit));
    }

    // port: libjpeg-turbo jidctint.c jpeg_idct_islow, one pass on eight lanes (d0..d7 are the eight inputs)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass(ref Vector256<int> d0, ref Vector256<int> d1, ref Vector256<int> d2, ref Vector256<int> d3,
        ref Vector256<int> d4, ref Vector256<int> d5, ref Vector256<int> d6, ref Vector256<int> d7, int shift)
    {
        Vector256<int> z1 = (d2 + d6) * Vector256.Create((int)Fix0541196100);
        Vector256<int> tmp2 = z1 + (d6 * Vector256.Create((int)-Fix1847759065));
        Vector256<int> tmp3 = z1 + (d2 * Vector256.Create((int)Fix0765366865));
        Vector256<int> tmp0 = (d0 + d4) << ConstBits;
        Vector256<int> tmp1 = (d0 - d4) << ConstBits;
        Vector256<int> tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

        // Odd part per figure 8 of the IJG notes: y7, y5, y3, y1.
        tmp0 = d7;
        tmp1 = d5;
        tmp2 = d3;
        tmp3 = d1;
        z1 = tmp0 + tmp3;
        Vector256<int> z2 = tmp1 + tmp2, z3 = tmp0 + tmp2, z4 = tmp1 + tmp3;
        Vector256<int> z5 = (z3 + z4) * Vector256.Create((int)Fix1175875602);
        tmp0 *= Vector256.Create((int)Fix0298631336);
        tmp1 *= Vector256.Create((int)Fix2053119869);
        tmp2 *= Vector256.Create((int)Fix3072711026);
        tmp3 *= Vector256.Create((int)Fix1501321110);
        z1 *= Vector256.Create((int)-Fix0899976223);
        z2 *= Vector256.Create((int)-Fix2562915447);
        z3 = (z3 * Vector256.Create((int)-Fix1961570560)) + z5;
        z4 = (z4 * Vector256.Create((int)-Fix0390180644)) + z5;
        tmp0 += z1 + z3;
        tmp1 += z2 + z4;
        tmp2 += z2 + z3;
        tmp3 += z1 + z4;

        Vector256<int> round = Vector256.Create(1 << (shift - 1));
        d0 = (tmp10 + tmp3 + round) >> shift;
        d7 = (tmp10 - tmp3 + round) >> shift;
        d1 = (tmp11 + tmp2 + round) >> shift;
        d6 = (tmp11 - tmp2 + round) >> shift;
        d2 = (tmp12 + tmp1 + round) >> shift;
        d5 = (tmp12 - tmp1 + round) >> shift;
        d3 = (tmp13 + tmp0 + round) >> shift;
        d4 = (tmp13 - tmp0 + round) >> shift;
    }

    // RangeLimit[v & 1023] without the table: with j = (v + 128) & 1023, j below 256 is the sample, 256..639
    // saturate to 255 and 640..1023 to 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> RangeLimit8(Vector256<int> values)
    {
        Vector256<int> j = (values + Vector256.Create(128)) & Vector256.Create(1023);
        Vector256<int> limited = Vector256.ConditionalSelect(Vector256.LessThan(j, Vector256.Create(256)), j,
            Vector256.ConditionalSelect(Vector256.LessThan(j, Vector256.Create(640)), Vector256.Create(255), Vector256<int>.Zero));
        Vector128<short> words = Vector128.Narrow(limited.GetLower(), limited.GetUpper());
        return Vector128.Narrow(words.AsUInt16(), words.AsUInt16());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref byte destination, Vector128<byte> row) =>
        Unsafe.WriteUnaligned(ref destination, row.AsUInt64().ToScalar());
}
