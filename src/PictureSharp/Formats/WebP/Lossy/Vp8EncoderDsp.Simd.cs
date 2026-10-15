using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Internal;

namespace PictureSharp.Formats.WebP.Lossy;

// SIMD versions of the encoder DSP. Each one equals its scalar counterpart for every input: arithmetic
// stays in 32-bit lanes like the C code, and only the lane layout differs.
internal static partial class Vp8EncoderDsp
{
    private static void TrueMotion(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int size)
    {
        if (Vector128.IsHardwareAccelerated && top.Length >= size)
        {
            TrueMotionSimd(output, left, top, size);
        }
        else
        {
            TrueMotionScalar(output, left, top, size);
        }
    }

    private static void TrueMotionSimd(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int size)
    {
        // clip(top[x] + left[y] - left[0]) on 16-bit lanes.
        Vector128<byte> above = size == 16 ? Vector128.Create(top[..16]) : Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(top)).AsByte();
        Vector128<short> low = Vector128.WidenLower(above).AsInt16(), high = Vector128.WidenUpper(above).AsInt16();
        Vector128<short> max = Vector128.Create((short)255);
        for (int y = 0; y < size; y++)
        {
            Vector128<short> delta = Vector128.Create((short)(left[y + 1] - left[0]));
            Vector128<short> l = Vector128.Clamp(low + delta, Vector128<short>.Zero, max);
            Vector128<short> h = Vector128.Clamp(high + delta, Vector128<short>.Zero, max);
            Span<byte> row = output.Slice(y * Bps, size);
            if (size == 16) Vector128.Narrow(l.AsUInt16(), h.AsUInt16()).CopyTo(row);
            else Vector128.Narrow(l.AsUInt16(), l.AsUInt16()).GetLower().CopyTo(row);
        }
    }

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
        ref byte src = ref MemoryMarshal.GetReference(source), pred = ref MemoryMarshal.GetReference(prediction);
        Vector128<int> d0 = Simd.LoadBytes4(ref src) - Simd.LoadBytes4(ref pred);
        Vector128<int> d1 = Simd.LoadBytes4(ref Unsafe.Add(ref src, Bps)) - Simd.LoadBytes4(ref Unsafe.Add(ref pred, Bps));
        Vector128<int> d2 = Simd.LoadBytes4(ref Unsafe.Add(ref src, 2 * Bps)) - Simd.LoadBytes4(ref Unsafe.Add(ref pred, 2 * Bps));
        Vector128<int> d3 = Simd.LoadBytes4(ref Unsafe.Add(ref src, 3 * Bps)) - Simd.LoadBytes4(ref Unsafe.Add(ref pred, 3 * Bps));

        // Horizontal pass, one row per lane.
        Simd.Transpose(ref d0, ref d1, ref d2, ref d3);
        Vector128<int> a0 = d0 + d3, a1 = d1 + d2, a2 = d1 - d2, a3 = d0 - d3;
        Vector128<int> t0 = (a0 + a1) << 3;
        Vector128<int> t1 = ((a2 * 2217) + (a3 * 5352) + Vector128.Create(1812)) >> 9;
        Vector128<int> t2 = (a0 - a1) << 3;
        Vector128<int> t3 = ((a3 * 2217) - (a2 * 5352) + Vector128.Create(937)) >> 9;

        // Vertical pass, one column per lane.
        Simd.Transpose(ref t0, ref t1, ref t2, ref t3);
        a0 = t0 + t3;
        a1 = t1 + t2;
        a2 = t1 - t2;
        a3 = t0 - t3;
        Vector128<int> o0 = (a0 + a1 + Vector128.Create(7)) >> 4;
        // Adding one and then the all-ones mask of (a3 == 0) adds (a3 != 0).
        Vector128<int> o1 = (((a2 * 2217) + (a3 * 5352) + Vector128.Create(12000)) >> 16) + Vector128<int>.One + Vector128.Equals(a3, Vector128<int>.Zero);
        Vector128<int> o2 = (a0 - a1 + Vector128.Create(7)) >> 4;
        Vector128<int> o3 = ((a3 * 2217) - (a2 * 5352) + Vector128.Create(51000)) >> 16;
        // Byte inputs keep the outputs within 16 bits, so saturation never applies.
        ref short dst = ref MemoryMarshal.GetReference(output);
        Simd.PackSignedSaturate(o0, o1).StoreUnsafe(ref dst);
        Simd.PackSignedSaturate(o2, o3).StoreUnsafe(ref dst, 8);
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
        Vp8Dsp.TransformOneSse41(input, ref MemoryMarshal.GetReference(prediction), ref output[offset]);
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
            for (int y = 0; y < height; y++)
            {
                Vector128<int> d = Simd.LoadBytes4(ref Unsafe.Add(ref pa, y * Bps)) - Simd.LoadBytes4(ref Unsafe.Add(ref pb, y * Bps));
                sum += d * d;
            }
        }

        return Vector128.Sum(sum);
    }

    public static int Disto(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<ushort> weights, int size)
    {
        if (!Avx2.IsSupported) return AdvSimd.Arm64.IsSupported ? DistoArm(a, b, weights, size) : DistoScalar(a, b, weights, size);

        CheckSpan(a.Length, size, size);
        CheckSpan(b.Length, size, size);
        if (weights.Length < 16) Vp8Dsp.ThrowBlockOutOfRange();

        // The transform below leaves its output transposed, so the weights are transposed too and
        // duplicated for the two halves (a in the lower one, b in the upper one).
        ref ushort weight = ref MemoryMarshal.GetReference(weights);
        Vector128<ushort> w01 = Vector128.LoadUnsafe(ref weight), w23 = Vector128.LoadUnsafe(ref weight, 8);
        Vector128<int> w0 = Vector128.WidenLower(w01).AsInt32(), w1 = Vector128.WidenUpper(w01).AsInt32();
        Vector128<int> w2 = Vector128.WidenLower(w23).AsInt32(), w3 = Vector128.WidenUpper(w23).AsInt32();
        Simd.Transpose(ref w0, ref w1, ref w2, ref w3);
        Vector256<int> weight0 = Vector256.Create(w0, w0), weight1 = Vector256.Create(w1, w1);
        Vector256<int> weight2 = Vector256.Create(w2, w2), weight3 = Vector256.Create(w3, w3);

        ref byte pa = ref MemoryMarshal.GetReference(a), pb = ref MemoryMarshal.GetReference(b);
        int sum = 0;
        for (int y = 0; y < size; y += 4)
            for (int x = 0; x < size; x += 4)
            {
                ref byte ba = ref Unsafe.Add(ref pa, (y * Bps) + x), bb = ref Unsafe.Add(ref pb, (y * Bps) + x);
                Vector256<int> r0 = Simd.LoadBytes4x2(ref ba, ref bb);
                Vector256<int> r1 = Simd.LoadBytes4x2(ref Unsafe.Add(ref ba, Bps), ref Unsafe.Add(ref bb, Bps));
                Vector256<int> r2 = Simd.LoadBytes4x2(ref Unsafe.Add(ref ba, 2 * Bps), ref Unsafe.Add(ref bb, 2 * Bps));
                Vector256<int> r3 = Simd.LoadBytes4x2(ref Unsafe.Add(ref ba, 3 * Bps), ref Unsafe.Add(ref bb, 3 * Bps));

                // TTransform is an exact integer Hadamard transform, so the vertical pass may run first.
                Vector256<int> a0 = r0 + r2, a1 = r1 + r3, a2 = r1 - r3, a3 = r0 - r2;
                Vector256<int> v0 = a0 + a1, v1 = a3 + a2, v2 = a3 - a2, v3 = a0 - a1;
                Simd.Transpose(ref v0, ref v1, ref v2, ref v3);
                a0 = v0 + v2;
                a1 = v1 + v3;
                a2 = v1 - v3;
                a3 = v0 - v2;
                Vector256<int> weighted = (Vector256.Abs(a0 + a1) * weight0) + (Vector256.Abs(a3 + a2) * weight1)
                                        + (Vector256.Abs(a3 - a2) * weight2) + (Vector256.Abs(a0 - a1) * weight3);
                sum += Math.Abs(Vector128.Sum(weighted.GetLower() - weighted.GetUpper())) >> 5;
            }

        return sum;
    }

    public static bool QuantizeBlock(Span<short> input, Span<short> output, Vp8Matrix matrix)
    {
        if (!Avx2.IsSupported) return AdvSimd.Arm64.IsSupported ? QuantizeBlockArm(input, output, matrix) : QuantizeBlockScalar(input, output, matrix);

        if (input.Length < 16 || output.Length < 16) Vp8Dsp.ThrowBlockOutOfRange();
        ref short coefficients = ref MemoryMarshal.GetReference(input);
        Vector256<short> values = Vector256.LoadUnsafe(ref coefficients);
        Vector256<int> level0 = Quantize8(Avx2.ConvertToVector256Int32(values.GetLower()), matrix, 0, out Vector256<int> dequantized0);
        Vector256<int> level1 = Quantize8(Avx2.ConvertToVector256Int32(values.GetUpper()), matrix, 8, out Vector256<int> dequantized1);
        Vector256.Narrow(dequantized0, dequantized1).StoreUnsafe(ref coefficients);

        Vector256<short> levels = Vector256.Narrow(level0, level1);
        Vector128<byte> low = levels.GetLower().AsByte(), high = levels.GetUpper().AsByte();
        Vector128<byte> zigzag0 = Simd.ShuffleBytes(low, Vector128.Create((byte)0, 1, 2, 3, 8, 9, 0x80, 0x80, 10, 11, 4, 5, 6, 7, 12, 13))
                                | Simd.ShuffleBytes(high, Vector128.Create(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 1, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80));
        Vector128<byte> zigzag1 = Simd.ShuffleBytes(high, Vector128.Create((byte)2, 3, 8, 9, 10, 11, 4, 5, 0x80, 0x80, 6, 7, 12, 13, 14, 15))
                                | Simd.ShuffleBytes(low, Vector128.Create(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 14, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80));
        ref short dst = ref MemoryMarshal.GetReference(output);
        zigzag0.AsInt16().StoreUnsafe(ref dst);
        zigzag1.AsInt16().StoreUnsafe(ref dst, 8);
        return levels != Vector256<short>.Zero;
    }

    // QuantizeBlock_C for coefficients offset..offset+7 in natural order.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Quantize8(Vector256<int> value, Vp8Matrix matrix, int offset, out Vector256<int> dequantized)
    {
        Vector256<int> sharpen = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref matrix.Sharpen[offset]));
        Vector256<int> iq = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref matrix.Iq[offset]));
        Vector256<int> q = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref matrix.Q[offset]));
        Vector256<uint> bias = Vector256.LoadUnsafe(ref matrix.Bias[offset]);
        Vector256<uint> zeroThreshold = Vector256.LoadUnsafe(ref matrix.ZeroThreshold[offset]);

        Vector256<uint> coefficient = (Vector256.Abs(value) + sharpen).AsUInt32();
        Vector256<int> keep = Vector256.GreaterThan(coefficient, zeroThreshold).AsInt32();
        Vector256<int> level = Vector256.Min(Vector256.ShiftRightLogical((coefficient * iq.AsUInt32()) + bias, 17).AsInt32(), Vector256.Create(2047));
        Vector256<int> sign = value >> 31;
        level = ((level ^ sign) - sign) & keep;
        dequantized = level * q;
        return level;
    }
}
