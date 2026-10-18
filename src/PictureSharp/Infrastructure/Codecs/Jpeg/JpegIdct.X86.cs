using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal static partial class JpegIdct
{
    // Products of the odd part after expanding z1..z5 (figure 8 of the IJG notes) into one multiplier per input, and of
    // the even part after expanding z1; the sums are exactly the scalar ones.
    private const short EvenD2Tmp2 = (short)Fix0541196100;
    private const short EvenD6Tmp2 = (short)(Fix0541196100 - Fix1847759065);
    private const short EvenD2Tmp3 = (short)(Fix0541196100 + Fix0765366865);
    private const short EvenD6Tmp3 = (short)Fix0541196100;
    private const short OddD7Tmp0 = (short)(Fix0298631336 - Fix0899976223 - Fix1961570560 + Fix1175875602);
    private const short OddD1Tmp0 = (short)(Fix1175875602 - Fix0899976223);
    private const short OddD3Tmp0 = (short)(Fix1175875602 - Fix1961570560);
    private const short OddD5Tmp0 = (short)Fix1175875602;
    private const short OddD7Tmp1 = (short)Fix1175875602;
    private const short OddD1Tmp1 = (short)(Fix1175875602 - Fix0390180644);
    private const short OddD3Tmp1 = (short)(Fix1175875602 - Fix2562915447);
    private const short OddD5Tmp1 = (short)(Fix2053119869 - Fix2562915447 - Fix0390180644 + Fix1175875602);
    private const short OddD7Tmp2 = (short)(Fix1175875602 - Fix1961570560);
    private const short OddD1Tmp2 = (short)Fix1175875602;
    private const short OddD3Tmp2 = (short)(Fix3072711026 - Fix2562915447 - Fix1961570560 + Fix1175875602);
    private const short OddD5Tmp2 = (short)(Fix1175875602 - Fix2562915447);
    private const short OddD7Tmp3 = (short)(Fix1175875602 - Fix0899976223);
    private const short OddD1Tmp3 = (short)(Fix1501321110 - Fix0899976223 - Fix0390180644 + Fix1175875602);
    private const short OddD3Tmp3 = (short)Fix1175875602;
    private const short OddD5Tmp3 = (short)(Fix1175875602 - Fix0390180644);

    /// <summary>
    /// <see cref="TransformScalar"/> with AVX2 for two blocks at once: each vector holds a row of block A in its lower
    /// half and the same row of block B in its upper half, and every per-lane AVX2 operation treats the halves alike.
    /// Inputs to each pass are 16-bit, so every product pair is one <c>pmaddwd</c> into 32-bit sums. Returns false,
    /// leaving the work to the scalar code, when either block's values could overflow those lanes.
    /// </summary>
    private static bool TransformAvx2(ReadOnlySpan<short> blockA, ReadOnlySpan<short> quantA, Span<byte> outputA, int strideA,
        ReadOnlySpan<short> blockB, ReadOnlySpan<short> quantB, Span<byte> outputB, int strideB)
    {
        ref short ba = ref MemoryMarshal.GetReference(blockA[..64]);
        ref short qa = ref MemoryMarshal.GetReference(quantA[..64]);
        ref short bb = ref MemoryMarshal.GetReference(blockB[..64]);
        ref short qb = ref MemoryMarshal.GetReference(quantB[..64]);
        Vector256<short> outOfRange = Vector256<short>.Zero;
        Vector256<short> d0 = Dequantize(ref ba, ref qa, ref bb, ref qb, 0, ref outOfRange);
        Vector256<short> d1 = Dequantize(ref ba, ref qa, ref bb, ref qb, 8, ref outOfRange);
        Vector256<short> d2 = Dequantize(ref ba, ref qa, ref bb, ref qb, 16, ref outOfRange);
        Vector256<short> d3 = Dequantize(ref ba, ref qa, ref bb, ref qb, 24, ref outOfRange);
        Vector256<short> d4 = Dequantize(ref ba, ref qa, ref bb, ref qb, 32, ref outOfRange);
        Vector256<short> d5 = Dequantize(ref ba, ref qa, ref bb, ref qb, 40, ref outOfRange);
        Vector256<short> d6 = Dequantize(ref ba, ref qa, ref bb, ref qb, 48, ref outOfRange);
        Vector256<short> d7 = Dequantize(ref ba, ref qa, ref bb, ref qb, 56, ref outOfRange);
        if (outOfRange != Vector256<short>.Zero)
        {
            return false;
        }

        // Pass 1: columns. Vector k is row k, so each lane is one column. The scalar shortcut for a column of
        // zero AC terms gives the same values as the full computation.
        Vector256<int> magnitude = Vector256<int>.Zero;
        Pass(ref d0, ref d1, ref d2, ref d3, ref d4, ref d5, ref d6, ref d7, ConstBits - Pass1Bits, final: false, ref magnitude);

        // Abs(int.MinValue) stays negative, so compare as unsigned.
        if (!Vector256.LessThanAll(magnitude.AsUInt32(), Vector256.Create((uint)SimdInputLimit)))
        {
            return false;
        }

        // Pass 2: rows, after a transpose; the outputs are columns again and are transposed back to rows.
        Transpose(ref d0, ref d1, ref d2, ref d3, ref d4, ref d5, ref d6, ref d7);
        Pass(ref d0, ref d1, ref d2, ref d3, ref d4, ref d5, ref d6, ref d7, ConstBits + Pass1Bits + 3, final: true, ref magnitude);
        Transpose(ref d0, ref d1, ref d2, ref d3, ref d4, ref d5, ref d6, ref d7);

        _ = outputA[(7 * strideA) + 7];
        _ = outputB[(7 * strideB) + 7];
        ref byte oa = ref MemoryMarshal.GetReference(outputA);
        ref byte ob = ref MemoryMarshal.GetReference(outputB);
        StoreSamples(ref oa, ref ob, d0);
        StoreSamples(ref Unsafe.Add(ref oa, strideA), ref Unsafe.Add(ref ob, strideB), d1);
        StoreSamples(ref Unsafe.Add(ref oa, 2 * strideA), ref Unsafe.Add(ref ob, 2 * strideB), d2);
        StoreSamples(ref Unsafe.Add(ref oa, 3 * strideA), ref Unsafe.Add(ref ob, 3 * strideB), d3);
        StoreSamples(ref Unsafe.Add(ref oa, 4 * strideA), ref Unsafe.Add(ref ob, 4 * strideB), d4);
        StoreSamples(ref Unsafe.Add(ref oa, 5 * strideA), ref Unsafe.Add(ref ob, 5 * strideB), d5);
        StoreSamples(ref Unsafe.Add(ref oa, 6 * strideA), ref Unsafe.Add(ref ob, 6 * strideB), d6);
        StoreSamples(ref Unsafe.Add(ref oa, 7 * strideA), ref Unsafe.Add(ref ob, 7 * strideB), d7);
        return true;
    }

    // A row of products of both blocks; a lane of 'outOfRange' is set when its product is not below SimdInputLimit in
    // magnitude (the high half of the 32-bit product is not the sign of the low half, or the low half is too large).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Dequantize(ref short blockA, ref short quantA, ref short blockB, ref short quantB, int offset,
        ref Vector256<short> outOfRange)
    {
        Vector256<short> x = Vector256.Create(Vector128.LoadUnsafe(ref blockA, (nuint)offset), Vector128.LoadUnsafe(ref blockB, (nuint)offset));
        Vector256<short> y = Vector256.Create(Vector128.LoadUnsafe(ref quantA, (nuint)offset), Vector128.LoadUnsafe(ref quantB, (nuint)offset));
        Vector256<short> low = Avx2.MultiplyLow(x, y);
        Vector256<short> high = Avx2.MultiplyHigh(x, y);
        Vector256<short> limit = Vector256.Create((short)(SimdInputLimit - 1));
        outOfRange |= (high ^ (low >> 15)) | Vector256.GreaterThan(low, limit) | Vector256.LessThan(low, -limit);
        return low;
    }

    /// <summary>
    /// One pass on rows d0..d7 of 16-bit values, in place. Columns 0–3 and 4–7 of each half are computed separately
    /// in 32-bit lanes and packed back; the first pass accumulates the largest magnitude, the final one range-limits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass(ref Vector256<short> d0, ref Vector256<short> d1, ref Vector256<short> d2, ref Vector256<short> d3,
        ref Vector256<short> d4, ref Vector256<short> d5, ref Vector256<short> d6, ref Vector256<short> d7, int shift, bool final,
        ref Vector256<int> magnitude)
    {
        HalfPass(Avx2.UnpackLow(d0, d4), Avx2.UnpackLow(d2, d6), Avx2.UnpackLow(d7, d1), Avx2.UnpackLow(d3, d5), shift,
            out Vector256<int> l0, out Vector256<int> l1, out Vector256<int> l2, out Vector256<int> l3,
            out Vector256<int> l4, out Vector256<int> l5, out Vector256<int> l6, out Vector256<int> l7);
        HalfPass(Avx2.UnpackHigh(d0, d4), Avx2.UnpackHigh(d2, d6), Avx2.UnpackHigh(d7, d1), Avx2.UnpackHigh(d3, d5), shift,
            out Vector256<int> h0, out Vector256<int> h1, out Vector256<int> h2, out Vector256<int> h3,
            out Vector256<int> h4, out Vector256<int> h5, out Vector256<int> h6, out Vector256<int> h7);
        if (final)
        {
            d0 = Avx2.PackSignedSaturate(RangeLimitSamples(l0), RangeLimitSamples(h0));
            d1 = Avx2.PackSignedSaturate(RangeLimitSamples(l1), RangeLimitSamples(h1));
            d2 = Avx2.PackSignedSaturate(RangeLimitSamples(l2), RangeLimitSamples(h2));
            d3 = Avx2.PackSignedSaturate(RangeLimitSamples(l3), RangeLimitSamples(h3));
            d4 = Avx2.PackSignedSaturate(RangeLimitSamples(l4), RangeLimitSamples(h4));
            d5 = Avx2.PackSignedSaturate(RangeLimitSamples(l5), RangeLimitSamples(h5));
            d6 = Avx2.PackSignedSaturate(RangeLimitSamples(l6), RangeLimitSamples(h6));
            d7 = Avx2.PackSignedSaturate(RangeLimitSamples(l7), RangeLimitSamples(h7));
            return;
        }

        magnitude = Vector256.Max(magnitude, Vector256.Max(
            Vector256.Max(Vector256.Max(Vector256.Abs(l0), Vector256.Abs(l1)), Vector256.Max(Vector256.Abs(l2), Vector256.Abs(l3))),
            Vector256.Max(Vector256.Max(Vector256.Abs(l4), Vector256.Abs(l5)), Vector256.Max(Vector256.Abs(l6), Vector256.Abs(l7)))));
        magnitude = Vector256.Max(magnitude, Vector256.Max(
            Vector256.Max(Vector256.Max(Vector256.Abs(h0), Vector256.Abs(h1)), Vector256.Max(Vector256.Abs(h2), Vector256.Abs(h3))),
            Vector256.Max(Vector256.Max(Vector256.Abs(h4), Vector256.Abs(h5)), Vector256.Max(Vector256.Abs(h6), Vector256.Abs(h7)))));

        // Only stored when the magnitudes pass the check, so the saturation never applies.
        d0 = Avx2.PackSignedSaturate(l0, h0);
        d1 = Avx2.PackSignedSaturate(l1, h1);
        d2 = Avx2.PackSignedSaturate(l2, h2);
        d3 = Avx2.PackSignedSaturate(l3, h3);
        d4 = Avx2.PackSignedSaturate(l4, h4);
        d5 = Avx2.PackSignedSaturate(l5, h5);
        d6 = Avx2.PackSignedSaturate(l6, h6);
        d7 = Avx2.PackSignedSaturate(l7, h7);
    }

    // port: libjpeg-turbo jidctint.c jpeg_idct_islow, one pass on eight lanes, from interleaved input pairs
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HalfPass(Vector256<short> p04, Vector256<short> p26, Vector256<short> p71, Vector256<short> p35, int shift,
        out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3,
        out Vector256<int> o4, out Vector256<int> o5, out Vector256<int> o6, out Vector256<int> o7)
    {
        // tmp0 and tmp1 are (d0 ± d4) << ConstBits.
        Vector256<int> tmp0 = Madd(p04, 1 << ConstBits, 1 << ConstBits);
        Vector256<int> tmp1 = Madd(p04, 1 << ConstBits, -(1 << ConstBits));
        Vector256<int> tmp2 = Madd(p26, EvenD2Tmp2, EvenD6Tmp2);
        Vector256<int> tmp3 = Madd(p26, EvenD2Tmp3, EvenD6Tmp3);
        Vector256<int> tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

        tmp0 = Madd(p71, OddD7Tmp0, OddD1Tmp0) + Madd(p35, OddD3Tmp0, OddD5Tmp0);
        tmp1 = Madd(p71, OddD7Tmp1, OddD1Tmp1) + Madd(p35, OddD3Tmp1, OddD5Tmp1);
        tmp2 = Madd(p71, OddD7Tmp2, OddD1Tmp2) + Madd(p35, OddD3Tmp2, OddD5Tmp2);
        tmp3 = Madd(p71, OddD7Tmp3, OddD1Tmp3) + Madd(p35, OddD3Tmp3, OddD5Tmp3);

        Vector256<int> round = Vector256.Create(1 << (shift - 1));
        o0 = (tmp10 + tmp3 + round) >> shift;
        o7 = (tmp10 - tmp3 + round) >> shift;
        o1 = (tmp11 + tmp2 + round) >> shift;
        o6 = (tmp11 - tmp2 + round) >> shift;
        o2 = (tmp12 + tmp1 + round) >> shift;
        o5 = (tmp12 - tmp1 + round) >> shift;
        o3 = (tmp13 + tmp0 + round) >> shift;
        o4 = (tmp13 - tmp0 + round) >> shift;
    }

    // a * ca + b * cb for each interleaved pair (a, b).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Madd(Vector256<short> pairs, int ca, int cb) =>
        Avx2.MultiplyAddAdjacent(pairs, Vector256.Create((ushort)ca | (cb << 16)).AsInt16());

    // RangeLimit[v & 1023] without the table: with j = (v + 128) & 1023, j below 256 is the sample, 256..639
    // saturate to 255 and 640..1023 to 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> RangeLimitSamples(Vector256<int> values)
    {
        Vector256<int> j = (values + Vector256.Create(128)) & Vector256.Create(1023);
        return Vector256.ConditionalSelect(Vector256.LessThan(j, Vector256.Create(256)), j,
            Vector256.ConditionalSelect(Vector256.LessThan(j, Vector256.Create(640)), Vector256.Create(255), Vector256<int>.Zero));
    }

    /// <summary>Transposes the 8x8 blocks of 16-bit values held as eight rows, one block in each 128-bit half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector256<short> r0, ref Vector256<short> r1, ref Vector256<short> r2, ref Vector256<short> r3,
        ref Vector256<short> r4, ref Vector256<short> r5, ref Vector256<short> r6, ref Vector256<short> r7)
    {
        Vector256<short> a0 = Avx2.UnpackLow(r0, r1), a1 = Avx2.UnpackHigh(r0, r1);
        Vector256<short> a2 = Avx2.UnpackLow(r2, r3), a3 = Avx2.UnpackHigh(r2, r3);
        Vector256<short> a4 = Avx2.UnpackLow(r4, r5), a5 = Avx2.UnpackHigh(r4, r5);
        Vector256<short> a6 = Avx2.UnpackLow(r6, r7), a7 = Avx2.UnpackHigh(r6, r7);
        Vector256<int> b0 = Avx2.UnpackLow(a0.AsInt32(), a2.AsInt32()), b1 = Avx2.UnpackHigh(a0.AsInt32(), a2.AsInt32());
        Vector256<int> b2 = Avx2.UnpackLow(a1.AsInt32(), a3.AsInt32()), b3 = Avx2.UnpackHigh(a1.AsInt32(), a3.AsInt32());
        Vector256<int> b4 = Avx2.UnpackLow(a4.AsInt32(), a6.AsInt32()), b5 = Avx2.UnpackHigh(a4.AsInt32(), a6.AsInt32());
        Vector256<int> b6 = Avx2.UnpackLow(a5.AsInt32(), a7.AsInt32()), b7 = Avx2.UnpackHigh(a5.AsInt32(), a7.AsInt32());
        r0 = Avx2.UnpackLow(b0.AsInt64(), b4.AsInt64()).AsInt16();
        r1 = Avx2.UnpackHigh(b0.AsInt64(), b4.AsInt64()).AsInt16();
        r2 = Avx2.UnpackLow(b1.AsInt64(), b5.AsInt64()).AsInt16();
        r3 = Avx2.UnpackHigh(b1.AsInt64(), b5.AsInt64()).AsInt16();
        r4 = Avx2.UnpackLow(b2.AsInt64(), b6.AsInt64()).AsInt16();
        r5 = Avx2.UnpackHigh(b2.AsInt64(), b6.AsInt64()).AsInt16();
        r6 = Avx2.UnpackLow(b3.AsInt64(), b7.AsInt64()).AsInt16();
        r7 = Avx2.UnpackHigh(b3.AsInt64(), b7.AsInt64()).AsInt16();
    }

    // A row of samples (0–255 in 16-bit lanes) of each block as bytes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreSamples(ref byte destinationA, ref byte destinationB, Vector256<short> row)
    {
        Vector256<byte> bytes = Avx2.PackUnsignedSaturate(row, row);
        Store(ref destinationA, bytes.GetLower());
        Store(ref destinationB, bytes.GetUpper());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref byte destination, Vector128<byte> row) =>
        Unsafe.WriteUnaligned(ref destination, row.AsUInt64().ToScalar());
}
