using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

// SIMD versions of the encoder DSP. Each one equals its scalar counterpart for every input: arithmetic
// stays in 32-bit lanes like the C code, and only the lane layout differs.
internal static partial class Vp8EncoderDsp
{
    private const int BlockSpan = Vp8Dsp.BlockSpan;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckSpan(int length, int width, int height)
    {
        if (length < ((height - 1) * Bps) + width) Vp8Dsp.ThrowBlockOutOfRange();
    }

    public static void FTransform(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prediction, Span<short> output)
    {
        if (!Simd.IsSupportedSse41)
        {
            FTransformScalar(source, prediction, output);
            return;
        }

        CheckSpan(source.Length, 4, 4);
        CheckSpan(prediction.Length, 4, 4);
        if (output.Length < 16) Vp8Dsp.ThrowBlockOutOfRange();
        FTransformSse41(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetReference(prediction), out Vector128<short> low, out Vector128<short> high);
        ref short dst = ref MemoryMarshal.GetReference(output);
        low.StoreUnsafe(ref dst);
        high.StoreUnsafe(ref dst, 8);
    }

    // port: src/dsp/enc_sse2.c FTransform_SSE2. The 4x4 block at src - pred: coefficients 0-7 in low, 8-15 in high.
    // Byte inputs keep every 16-bit intermediate in range, so the result equals FTransform_C for every input.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FTransformSse41(ref byte src, ref byte pred, out Vector128<short> low, out Vector128<short> high)
    {
        // 00 01 10 11 02 03 12 13 and 20 21 30 31 22 23 32 33, widened to 16 bits.
        Vector128<short> src01 = InterleaveRows(ref src, 0), src23 = InterleaveRows(ref src, 2 * Bps);
        Vector128<short> pred01 = InterleaveRows(ref pred, 0), pred23 = InterleaveRows(ref pred, 2 * Bps);
        FTransformPass1(src01 - pred01, src23 - pred23, out Vector128<short> v01, out Vector128<short> v32);
        FTransformPass2(v01, v32, out low, out high);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> InterleaveRows(ref byte block, int offset)
    {
        Vector128<byte> row0 = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, offset))).AsByte();
        Vector128<byte> row1 = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, offset + Bps))).AsByte();
        return Simd.UnpackLow(Simd.UnpackLow(row0.AsInt16(), row1.AsInt16()).AsByte(), Vector128<byte>.Zero).AsInt16();
    }

    // port: src/dsp/enc_sse2.c FTransformPass1_SSE2
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FTransformPass1(Vector128<short> in01, Vector128<short> in23, out Vector128<short> out01, out Vector128<short> out32)
    {
        // 00 01 10 11 03 02 13 12 and 20 21 30 31 23 22 33 32
        Vector128<short> swap = Vector128.Create((short)0, 1, 2, 3, 5, 4, 7, 6);
        Vector128<long> shuf01 = Vector128.Shuffle(in01, swap).AsInt64(), shuf23 = Vector128.Shuffle(in23, swap).AsInt64();
        Vector128<short> s01 = Simd.UnpackLow(shuf01, shuf23).AsInt16(), s32 = Simd.UnpackHigh(shuf01, shuf23).AsInt16();
        // [a0 a1 | a0' a1' | ...] and [a3 a2 | a3' a2' | ...]
        Vector128<short> a01 = s01 + s32, a32 = s01 - s32;
        Vector128<int> tmp0 = Simd.MultiplyAddAdjacent(a01, Vector128.Create((short)8));
        Vector128<int> tmp2 = Simd.MultiplyAddAdjacent(a01, Vector128.Create((short)8, -8, 8, -8, 8, -8, 8, -8));
        Vector128<int> tmp1 = (Simd.MultiplyAddAdjacent(a32, Vector128.Create((short)5352, 2217, 5352, 2217, 5352, 2217, 5352, 2217)) + Vector128.Create(1812)) >> 9;
        Vector128<int> tmp3 = (Simd.MultiplyAddAdjacent(a32, Vector128.Create((short)2217, -5352, 2217, -5352, 2217, -5352, 2217, -5352)) + Vector128.Create(937)) >> 9;
        Vector128<short> s03 = Simd.PackSignedSaturate(tmp0, tmp2), s12 = Simd.PackSignedSaturate(tmp1, tmp3);
        Vector128<int> sLow = Simd.UnpackLow(s03, s12).AsInt32(), sHigh = Simd.UnpackHigh(s03, s12).AsInt32();
        out01 = Simd.UnpackLow(sLow, sHigh).AsInt16();
        out32 = Vector128.Shuffle(Simd.UnpackHigh(sLow, sHigh), Vector128.Create(2, 3, 0, 1)).AsInt16();
    }

    // port: src/dsp/enc_sse2.c FTransformPass2_SSE2
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FTransformPass2(Vector128<short> v01, Vector128<short> v32, out Vector128<short> low, out Vector128<short> high)
    {
        // a3 = v0 - v3, a2 = v1 - v2
        Vector128<short> a32 = v01 - v32;
        Vector128<short> a22 = Simd.UnpackHigh(a32.AsInt64(), a32.AsInt64()).AsInt16();
        Vector128<short> b23 = Simd.UnpackLow(a22, a32);
        Vector128<int> c1 = Simd.MultiplyAddAdjacent(b23, Vector128.Create((short)2217, 5352, 2217, 5352, 2217, 5352, 2217, 5352));
        Vector128<int> c3 = Simd.MultiplyAddAdjacent(b23, Vector128.Create((short)-5352, 2217, -5352, 2217, -5352, 2217, -5352, 2217));
        // f1 = ((b3 * 5352 + b2 * 2217 + 12000) >> 16) + 1, f3 = ((b3 * 2217 - b2 * 5352 + 51000) >> 16)
        Vector128<int> e1 = (c1 + Vector128.Create(12000 + (1 << 16))) >> 16;
        Vector128<int> e3 = (c3 + Vector128.Create(51000)) >> 16;
        Vector128<short> f1 = Simd.PackSignedSaturate(e1, e1), f3 = Simd.PackSignedSaturate(e3, e3);
        // g1 = f1 + (a3 != 0) = f1 + 1 - (a3 == 0)
        Vector128<short> g1 = f1 + Vector128.Equals(a32, Vector128<short>.Zero);

        // a0 = v0 + v3, a1 = v1 + v2; d0 = (a0 + a1 + 7) >> 4, d2 = (a0 - a1 + 7) >> 4
        Vector128<short> a01 = v01 + v32;
        Vector128<short> a01Plus7 = a01 + Vector128.Create((short)7);
        Vector128<short> a11 = Simd.UnpackHigh(a01.AsInt64(), a01.AsInt64()).AsInt16();
        Vector128<short> d0 = (a01Plus7 + a11) >> 4, d2 = (a01Plus7 - a11) >> 4;
        low = Simd.UnpackLow(d0.AsInt64(), g1.AsInt64()).AsInt16();
        high = Simd.UnpackLow(d2.AsInt64(), f3.AsInt64()).AsInt16();
    }

    /// <summary>FTransform of two horizontally adjacent 4x4 blocks into output[0..15] and output[16..31].</summary>
    // port: src/dsp/enc_sse2.c FTransform2_SSE2
    public static void FTransform2(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prediction, Span<short> output)
    {
        if (!Simd.IsSupportedSse41)
        {
            FTransformScalar(source, prediction, output);
            FTransformScalar(source[4..], prediction[4..], output[16..]);
            return;
        }

        CheckSpan(source.Length, 8, 4);
        CheckSpan(prediction.Length, 8, 4);
        if (output.Length < 32) Vp8Dsp.ThrowBlockOutOfRange();
        ref byte src = ref MemoryMarshal.GetReference(source), pred = ref MemoryMarshal.GetReference(prediction);
        Vector128<short> diff0 = LoadRow8(ref src, 0) - LoadRow8(ref pred, 0);
        Vector128<short> diff1 = LoadRow8(ref src, Bps) - LoadRow8(ref pred, Bps);
        Vector128<short> diff2 = LoadRow8(ref src, 2 * Bps) - LoadRow8(ref pred, 2 * Bps);
        Vector128<short> diff3 = LoadRow8(ref src, 3 * Bps) - LoadRow8(ref pred, 3 * Bps);
        // 00 01 10 11 02 03 12 13 of each block.
        Vector128<short> shuf01Low = Simd.UnpackLow(diff0.AsInt32(), diff1.AsInt32()).AsInt16();
        Vector128<short> shuf23Low = Simd.UnpackLow(diff2.AsInt32(), diff3.AsInt32()).AsInt16();
        Vector128<short> shuf01High = Simd.UnpackHigh(diff0.AsInt32(), diff1.AsInt32()).AsInt16();
        Vector128<short> shuf23High = Simd.UnpackHigh(diff2.AsInt32(), diff3.AsInt32()).AsInt16();
        FTransformPass1(shuf01Low, shuf23Low, out Vector128<short> v01Low, out Vector128<short> v32Low);
        FTransformPass1(shuf01High, shuf23High, out Vector128<short> v01High, out Vector128<short> v32High);
        ref short dst = ref MemoryMarshal.GetReference(output);
        FTransformPass2(v01Low, v32Low, out Vector128<short> low, out Vector128<short> high);
        low.StoreUnsafe(ref dst);
        high.StoreUnsafe(ref dst, 8);
        FTransformPass2(v01High, v32High, out low, out high);
        low.StoreUnsafe(ref dst, 16);
        high.StoreUnsafe(ref dst, 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadRow8(ref byte block, int offset) =>
        Simd.UnpackLow(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref block, offset))).AsByte(), Vector128<byte>.Zero).AsInt16();

    // port: src/dsp/enc_sse2.c CollectHistogram_SSE2
    public static (int MaxValue, int LastNonZero) CollectHistogram(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prediction, int start, int end)
    {
        if (!Simd.IsSupportedSse41)
        {
            return CollectHistogramScalar(source, prediction, start, end);
        }

        Span<int> distribution = stackalloc int[32];
        distribution.Clear();
        Span<ushort> bins = stackalloc ushort[16];
        ref byte src = ref MemoryMarshal.GetReference(source), pred = ref MemoryMarshal.GetReference(prediction);
        ref ushort bin = ref MemoryMarshal.GetReference(bins);
        Vector128<ushort> maxBin = Vector128.Create((ushort)31);
        for (int b = start; b < end; b++)
        {
            int offset = Scan[b];
            CheckSpan(source.Length - offset, 4, 4);
            CheckSpan(prediction.Length - offset, 4, 4);
            FTransformSse41(ref Unsafe.Add(ref src, offset), ref Unsafe.Add(ref pred, offset), out Vector128<short> low, out Vector128<short> high);
            // min(|c| >> 3, 31); the unsigned view keeps |-32768| = 32768 like the scalar code.
            Vector128.Min(Vector128.ShiftRightLogical(Vector128.Abs(low).AsUInt16(), 3), maxBin).StoreUnsafe(ref bin);
            Vector128.Min(Vector128.ShiftRightLogical(Vector128.Abs(high).AsUInt16(), 3), maxBin).StoreUnsafe(ref bin, 8);
            for (int k = 0; k < 16; k++)
            {
                distribution[Unsafe.Add(ref bin, k)]++;
            }
        }

        return HistogramStats(distribution);
    }

    public static void ITransform(ReadOnlySpan<byte> prediction, ReadOnlySpan<short> input, byte[] output, int offset)
    {
        if (!Simd.IsSupportedSse41)
        {
            ITransformScalar(prediction, input, output, offset);
            return;
        }

        CheckSpan(prediction.Length, 4, 4);
        Vp8Dsp.CheckBlock(input, output.Length, offset);
        ref short coefficients = ref MemoryMarshal.GetReference(input);
        Vector128<short> in01 = Vector128.LoadUnsafe(ref coefficients), in23 = Vector128.LoadUnsafe(ref coefficients, 8);
        // With |coefficient| <= 2048 every value the 16-bit transform shifts, multiplies or clips stays below 2^15
        // (the passes reach 3.9 and 14.8 times the input) and wrapped sums are exact modulo 2^16. Larger values,
        // which the encoder's own levels do not reach, take the 32-bit transform of the decoder.
        if (Vector128.GreaterThanAny(Vector128.Max(Vector128.Abs(in01).AsUInt16(), Vector128.Abs(in23).AsUInt16()), Vector128.Create((ushort)2048)))
        {
            Vp8Dsp.TransformOneSse41(input, ref MemoryMarshal.GetReference(prediction), ref output[offset]);
            return;
        }

        ITransformOne16(in01, in23, ref MemoryMarshal.GetReference(prediction), ref output[offset]);
    }

    // port: src/dsp/enc_sse2.c ITransform_One_SSE2. MUL1(x) and MUL2(x) are mulhi(x, 20091) + x and
    // mulhi(x, -30068) + x: 35468 = 65536 - 30068 keeps the constant within 16 bits.
    private static void ITransformOne16(Vector128<short> in01, Vector128<short> in23, ref byte reference, ref byte dst)
    {
        Vector128<short> k1k2 = Vector128.Create((short)20091, 20091, 20091, 20091, -30068, -30068, -30068, -30068);
        Vector128<short> k2k1 = Vector128.Create((short)-30068, -30068, -30068, -30068, 20091, 20091, 20091, 20091);

        // Vertical pass and transpose.
        InverseTransformPass(in01, in23, k1k2, k2k1, out Vector128<short> tmp01, out Vector128<short> tmp23);
        Vector128<short> transpose0 = Simd.UnpackLow(tmp01, tmp23), transpose1 = Simd.UnpackHigh(tmp01, tmp23);
        Vector128<short> t01 = Simd.UnpackLow(transpose0, transpose1), t23 = Simd.UnpackHigh(transpose0, transpose1);

        // Horizontal pass, rounding and transpose.
        Vector128<short> dc = t01 + Vector128.Create((short)4, 4, 4, 4, 0, 0, 0, 0);
        InverseTransformPass(dc, t23, k1k2, k2k1, out tmp01, out tmp23);
        tmp01 >>= 3;
        tmp23 >>= 3;
        transpose0 = Simd.UnpackLow(tmp01, tmp23);
        transpose1 = Simd.UnpackHigh(tmp01, tmp23);
        t01 = Simd.UnpackLow(transpose0, transpose1);
        t23 = Simd.UnpackHigh(transpose0, transpose1);

        // Add to the prediction and store.
        Vector128<uint> pixels = Simd.PackUnsignedSaturate(LoadRows4(ref reference, 0) + t01, LoadRows4(ref reference, 2 * Bps) + t23).AsUInt32();
        Unsafe.WriteUnaligned(ref dst, pixels.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Bps), pixels.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2 * Bps), pixels.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 3 * Bps), pixels.GetElement(3));
    }

    // One pass of ITransform_One_SSE2 on in01 = x0 | x1 and in23 = x2 | x3: returns (x0 + x2 + d | x0 - x2 + c) and
    // (x0 + x2 - d | x0 - x2 - c) with its halves swapped, where c = MUL2(x1) - MUL1(x3) and d = MUL1(x1) + MUL2(x3).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InverseTransformPass(Vector128<short> in01, Vector128<short> in23, Vector128<short> k1k2, Vector128<short> k2k1,
        out Vector128<short> tmp01, out Vector128<short> tmp23)
    {
        Vector128<short> in1 = Simd.UnpackHigh(in01.AsInt64(), in01.AsInt64()).AsInt16();
        Vector128<short> in3 = Simd.UnpackHigh(in23.AsInt64(), in23.AsInt64()).AsInt16();
        Vector128<short> ad3 = in01 + in23, bc3 = in01 - in23;
        Vector128<short> c1d1 = Simd.MultiplyHigh(in1, k2k1), c2d2 = Simd.MultiplyHigh(in3, k1k2);
        Vector128<short> c = Simd.UnpackHigh(bc3.AsInt64(), bc3.AsInt64()).AsInt16() + (c1d1 - c2d2);
        Vector128<short> du = ad3 + (c1d1 + c2d2);
        Vector128<short> d = Simd.UnpackHigh(du.AsInt64(), du.AsInt64()).AsInt16();
        Vector128<short> ab = Simd.UnpackLow(ad3.AsInt64(), bc3.AsInt64()).AsInt16();
        Vector128<short> dcv = Simd.UnpackLow(d.AsInt64(), c.AsInt64()).AsInt16();
        tmp01 = ab + dcv;
        tmp23 = Vector128.Shuffle((ab - dcv).AsInt32(), Vector128.Create(2, 3, 0, 1)).AsInt16();
    }

    public static int Sse(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height)
    {
        if (!Simd.IsSupported128 || (width != 16 && width != 4)) return SseScalar(a, b, width, height);

        CheckSpan(a.Length, width, height);
        CheckSpan(b.Length, width, height);
        ref byte pa = ref MemoryMarshal.GetReference(a), pb = ref MemoryMarshal.GetReference(b);
        Vector128<int> sum = Vector128<int>.Zero;
        if (width == 16)
        {
            for (int y = 0; y < height; y++)
            {
                Vector128<byte> va = Vector128.LoadUnsafe(ref pa, (nuint)(y * Bps)), vb = Vector128.LoadUnsafe(ref pb, (nuint)(y * Bps));
                Vector128<short> low = Vector128.WidenLower(va).AsInt16() - Vector128.WidenLower(vb).AsInt16();
                Vector128<short> high = Vector128.WidenUpper(va).AsInt16() - Vector128.WidenUpper(vb).AsInt16();
                sum += Simd.MultiplyAddAdjacent(low, low) + Simd.MultiplyAddAdjacent(high, high);
            }
        }
        else
        {
            // port: src/dsp/enc_sse2.c SSE4x4_SSE2
            Vector128<short> d0 = LoadRows4(ref pa, 0) - LoadRows4(ref pb, 0);
            Vector128<short> d1 = LoadRows4(ref pa, 2 * Bps) - LoadRows4(ref pb, 2 * Bps);
            sum = Simd.MultiplyAddAdjacent(d0, d0) + Simd.MultiplyAddAdjacent(d1, d1);
        }

        return Vector128.Sum(sum);
    }

    // Rows y and y + 1 of a 4-wide block, widened to 16 bits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadRows4(ref byte block, int offset)
    {
        ulong rows = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, offset))
                   | ((ulong)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, offset + Bps)) << 32);
        return Simd.UnpackLow(Vector128.CreateScalarUnsafe(rows).AsByte(), Vector128<byte>.Zero).AsInt16();
    }

    public static int Disto(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<ushort> weights, int size)
    {
        if (!Simd.IsSupportedSse41) return DistoScalar(a, b, weights, size);

        CheckSpan(a.Length, size, size);
        CheckSpan(b.Length, size, size);
        if (weights.Length < 16) Vp8Dsp.ThrowBlockOutOfRange();

        // The transform below leaves the vertical frequency in the lanes, so the weights are transposed:
        // w01 = [w0 w4 w8 w12 | w1 w5 w9 w13], w23 = [w2 w6 w10 w14 | w3 w7 w11 w15]. They must stay below 32768.
        ref ushort weight = ref MemoryMarshal.GetReference(weights);
        Vector128<short> w0 = Vector128.LoadUnsafe(ref weight).AsInt16(), w8 = Vector128.LoadUnsafe(ref weight, 8).AsInt16();
        Vector128<short> wLow = Simd.UnpackLow(w0, w8), wHigh = Simd.UnpackHigh(w0, w8);
        Vector128<short> w01 = Simd.UnpackLow(wLow, wHigh), w23 = Simd.UnpackHigh(wLow, wHigh);

        ref byte pa = ref MemoryMarshal.GetReference(a), pb = ref MemoryMarshal.GetReference(b);
        int sum = 0;
        for (int y = 0; y < size; y += 4)
            for (int x = 0; x < size; x += 4)
            {
                int offset = (y * Bps) + x;
                sum += Math.Abs(TTransform2(ref Unsafe.Add(ref pa, offset), ref Unsafe.Add(ref pb, offset), w01, w23)) >> 5;
            }

        return sum;
    }

    // port: src/dsp/enc_sse41.c TTransform_SSE41: the weighted sum of block a minus that of block b, both
    // transformed at once in the two halves of 16-bit vectors (byte inputs keep every value within 16 bits).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TTransform2(ref byte a, ref byte b, Vector128<short> w01, Vector128<short> w23)
    {
        // a(y, 0..3) b(y, 0..3) per row.
        Vector128<short> r0 = LoadPair4(ref a, ref b, 0), r1 = LoadPair4(ref a, ref b, Bps);
        Vector128<short> r2 = LoadPair4(ref a, ref b, 2 * Bps), r3 = LoadPair4(ref a, ref b, 3 * Bps);

        // Vertical pass first: the transform is separable, so the order does not change the values.
        Vector128<short> a0 = r0 + r2, a1 = r1 + r3, a2 = r1 - r3, a3 = r0 - r2;
        Transpose2x4x4(a0 + a1, a3 + a2, a3 - a2, a0 - a1, out Vector128<short> t0, out Vector128<short> t1, out Vector128<short> t2, out Vector128<short> t3);

        // Horizontal pass: h_j holds horizontal frequency j, lane k the vertical frequency k.
        a0 = t0 + t2;
        a1 = t1 + t3;
        a2 = t1 - t3;
        a3 = t0 - t2;
        Vector128<short> h0 = a0 + a1, h1 = a3 + a2, h2 = a3 - a2, h3 = a0 - a1;
        Vector128<long> h01a = Simd.UnpackLow(h0.AsInt64(), h1.AsInt64()), h23a = Simd.UnpackLow(h2.AsInt64(), h3.AsInt64());
        Vector128<long> h01b = Simd.UnpackHigh(h0.AsInt64(), h1.AsInt64()), h23b = Simd.UnpackHigh(h2.AsInt64(), h3.AsInt64());
        Vector128<int> sumA = Simd.MultiplyAddAdjacent(Vector128.Abs(h01a.AsInt16()), w01) + Simd.MultiplyAddAdjacent(Vector128.Abs(h23a.AsInt16()), w23);
        Vector128<int> sumB = Simd.MultiplyAddAdjacent(Vector128.Abs(h01b.AsInt16()), w01) + Simd.MultiplyAddAdjacent(Vector128.Abs(h23b.AsInt16()), w23);
        return Vector128.Sum(sumA - sumB);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadPair4(ref byte a, ref byte b, int offset)
    {
        ulong pair = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref a, offset)) | ((ulong)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, offset)) << 32);
        return Simd.UnpackLow(Vector128.CreateScalarUnsafe(pair).AsByte(), Vector128<byte>.Zero).AsInt16();
    }

    // port: src/dsp/common_sse2.h VP8Transpose_2_4x4_16b. Transposes the 4x4 blocks in the two halves independently.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose2x4x4(Vector128<short> in0, Vector128<short> in1, Vector128<short> in2, Vector128<short> in3,
        out Vector128<short> out0, out Vector128<short> out1, out Vector128<short> out2, out Vector128<short> out3)
    {
        Vector128<int> t00 = Simd.UnpackLow(in0, in1).AsInt32(), t01 = Simd.UnpackLow(in2, in3).AsInt32();
        Vector128<int> t02 = Simd.UnpackHigh(in0, in1).AsInt32(), t03 = Simd.UnpackHigh(in2, in3).AsInt32();
        Vector128<long> t10 = Simd.UnpackLow(t00, t01).AsInt64(), t11 = Simd.UnpackLow(t02, t03).AsInt64();
        Vector128<long> t12 = Simd.UnpackHigh(t00, t01).AsInt64(), t13 = Simd.UnpackHigh(t02, t03).AsInt64();
        out0 = Simd.UnpackLow(t10, t11).AsInt16();
        out1 = Simd.UnpackHigh(t10, t11).AsInt16();
        out2 = Simd.UnpackLow(t12, t13).AsInt16();
        out3 = Simd.UnpackHigh(t12, t13).AsInt16();
    }

    public static bool QuantizeBlock(Span<short> input, Span<short> output, Vp8Matrix matrix)
    {
        if (!Simd.IsSupportedSsse3) return QuantizeBlockScalar(input, output, matrix);

        if (input.Length < 16 || output.Length < 16) Vp8Dsp.ThrowBlockOutOfRange();
        ref short coefficients = ref MemoryMarshal.GetReference(input);
        Vector128<short> in0 = Vector128.LoadUnsafe(ref coefficients), in8 = Vector128.LoadUnsafe(ref coefficients, 8);
        Vector128<short> out0 = Quantize8(in0, matrix, 0), out8 = Quantize8(in8, matrix, 8);
        (out0 * Vector128.LoadUnsafe(ref matrix.Q[0]).AsInt16()).StoreUnsafe(ref coefficients);
        (out8 * Vector128.LoadUnsafe(ref matrix.Q[8]).AsInt16()).StoreUnsafe(ref coefficients, 8);

        Vector128<byte> low = out0.AsByte(), high = out8.AsByte();
        Vector128<byte> zigzag0 = Simd.ShuffleBytes(low, Vector128.Create((byte)0, 1, 2, 3, 8, 9, 0x80, 0x80, 10, 11, 4, 5, 6, 7, 12, 13))
                                | Simd.ShuffleBytes(high, Vector128.Create(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 1, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80));
        Vector128<byte> zigzag1 = Simd.ShuffleBytes(high, Vector128.Create((byte)2, 3, 8, 9, 10, 11, 4, 5, 0x80, 0x80, 6, 7, 12, 13, 14, 15))
                                | Simd.ShuffleBytes(low, Vector128.Create(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 14, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80));
        ref short dst = ref MemoryMarshal.GetReference(output);
        zigzag0.AsInt16().StoreUnsafe(ref dst);
        zigzag1.AsInt16().StoreUnsafe(ref dst, 8);
        return (out0 | out8) != Vector128<short>.Zero;
    }

    // port: src/dsp/enc_sse41.c DoQuantizeBlock_SSE41 for coefficients offset..offset+7, returning the signed levels.
    // The unsigned 16-bit |in| + sharpen and its 32-bit product with iq are exact (q >= 4 keeps iq <= 32768). No
    // zero-threshold test is needed: ZeroThreshold is the largest coefficient whose quotient is zero, as in QuantizeBlock_C.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Quantize8(Vector128<short> value, Vp8Matrix matrix, int offset)
    {
        Vector128<ushort> iq = Vector128.LoadUnsafe(ref matrix.Iq[offset]);
        Vector128<ushort> coefficient = Vector128.Abs(value).AsUInt16() + Vector128.LoadUnsafe(ref matrix.Sharpen[offset]);
        Vector128<ushort> productHigh = Simd.MultiplyHigh(coefficient, iq), productLow = coefficient * iq;
        Vector128<int> level0 = (Simd.UnpackLow(productLow, productHigh).AsInt32() + Vector128.LoadUnsafe(ref matrix.Bias[offset]).AsInt32()) >> 17;
        Vector128<int> level4 = (Simd.UnpackHigh(productLow, productHigh).AsInt32() + Vector128.LoadUnsafe(ref matrix.Bias[offset + 4]).AsInt32()) >> 17;
        Vector128<short> level = Vector128.Min(Simd.PackSignedSaturate(level0, level4), Vector128.Create((short)2047));
        Vector128<short> sign = value >> 15;
        return (level ^ sign) - sign;
    }
}
