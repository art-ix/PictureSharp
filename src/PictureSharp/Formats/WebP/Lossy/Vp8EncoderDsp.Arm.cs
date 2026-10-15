using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PictureSharp.Internal;

namespace PictureSharp.Formats.WebP.Lossy;

// ARM64 versions of the AVX2-only encoder DSP: the 256-bit lanes become two 128-bit halves with the same 32-bit
// arithmetic, so each one equals its scalar counterpart for every input.
internal static partial class Vp8EncoderDsp
{
    private static int DistoArm(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<ushort> weights, int size)
    {
        CheckSpan(a.Length, size, size);
        CheckSpan(b.Length, size, size);
        if (weights.Length < 16) Vp8Dsp.ThrowBlockOutOfRange();

        // The transform below leaves its output transposed, so the weights are transposed too.
        ref ushort weight = ref MemoryMarshal.GetReference(weights);
        Vector128<ushort> w01 = Vector128.LoadUnsafe(ref weight), w23 = Vector128.LoadUnsafe(ref weight, 8);
        Vector128<int> w0 = Vector128.WidenLower(w01).AsInt32(), w1 = Vector128.WidenUpper(w01).AsInt32();
        Vector128<int> w2 = Vector128.WidenLower(w23).AsInt32(), w3 = Vector128.WidenUpper(w23).AsInt32();
        Simd.Transpose(ref w0, ref w1, ref w2, ref w3);

        ref byte pa = ref MemoryMarshal.GetReference(a), pb = ref MemoryMarshal.GetReference(b);
        int sum = 0;
        for (int y = 0; y < size; y += 4)
            for (int x = 0; x < size; x += 4)
            {
                Vector128<int> weightedA = WeightedHadamard(ref Unsafe.Add(ref pa, (y * Bps) + x), w0, w1, w2, w3);
                Vector128<int> weightedB = WeightedHadamard(ref Unsafe.Add(ref pb, (y * Bps) + x), w0, w1, w2, w3);
                sum += Math.Abs(Vector128.Sum(weightedA - weightedB)) >> 5;
            }

        return sum;
    }

    // TTransform of one 4x4 block (vertical pass first, as in the AVX2 code), weighted per lane before the final sum.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WeightedHadamard(ref byte block, Vector128<int> w0, Vector128<int> w1, Vector128<int> w2, Vector128<int> w3)
    {
        Vector128<int> r0 = Simd.LoadBytes4(ref block), r1 = Simd.LoadBytes4(ref Unsafe.Add(ref block, Bps));
        Vector128<int> r2 = Simd.LoadBytes4(ref Unsafe.Add(ref block, 2 * Bps)), r3 = Simd.LoadBytes4(ref Unsafe.Add(ref block, 3 * Bps));
        Vector128<int> a0 = r0 + r2, a1 = r1 + r3, a2 = r1 - r3, a3 = r0 - r2;
        Vector128<int> v0 = a0 + a1, v1 = a3 + a2, v2 = a3 - a2, v3 = a0 - a1;
        Simd.Transpose(ref v0, ref v1, ref v2, ref v3);
        a0 = v0 + v2;
        a1 = v1 + v3;
        a2 = v1 - v3;
        a3 = v0 - v2;
        return (Vector128.Abs(a0 + a1) * w0) + (Vector128.Abs(a3 + a2) * w1) + (Vector128.Abs(a3 - a2) * w2) + (Vector128.Abs(a0 - a1) * w3);
    }

    private static bool QuantizeBlockArm(Span<short> input, Span<short> output, Vp8Matrix matrix)
    {
        if (input.Length < 16 || output.Length < 16) Vp8Dsp.ThrowBlockOutOfRange();
        ref short coefficients = ref MemoryMarshal.GetReference(input);
        Vector128<short> values0 = Vector128.LoadUnsafe(ref coefficients), values1 = Vector128.LoadUnsafe(ref coefficients, 8);
        Vector128<ushort> sharpen0 = Vector128.LoadUnsafe(ref matrix.Sharpen[0]), sharpen1 = Vector128.LoadUnsafe(ref matrix.Sharpen[8]);
        Vector128<ushort> iq0 = Vector128.LoadUnsafe(ref matrix.Iq[0]), iq1 = Vector128.LoadUnsafe(ref matrix.Iq[8]);
        Vector128<ushort> q0 = Vector128.LoadUnsafe(ref matrix.Q[0]), q1 = Vector128.LoadUnsafe(ref matrix.Q[8]);
        Vector128<int> level0 = Quantize4(Vector128.WidenLower(values0), Vector128.WidenLower(sharpen0), Vector128.WidenLower(iq0), Vector128.WidenLower(q0), matrix, 0, out Vector128<int> dequantized0);
        Vector128<int> level1 = Quantize4(Vector128.WidenUpper(values0), Vector128.WidenUpper(sharpen0), Vector128.WidenUpper(iq0), Vector128.WidenUpper(q0), matrix, 4, out Vector128<int> dequantized1);
        Vector128<int> level2 = Quantize4(Vector128.WidenLower(values1), Vector128.WidenLower(sharpen1), Vector128.WidenLower(iq1), Vector128.WidenLower(q1), matrix, 8, out Vector128<int> dequantized2);
        Vector128<int> level3 = Quantize4(Vector128.WidenUpper(values1), Vector128.WidenUpper(sharpen1), Vector128.WidenUpper(iq1), Vector128.WidenUpper(q1), matrix, 12, out Vector128<int> dequantized3);
        Vector128.Narrow(dequantized0, dequantized1).StoreUnsafe(ref coefficients);
        Vector128.Narrow(dequantized2, dequantized3).StoreUnsafe(ref coefficients, 8);

        Vector128<short> levelsLow = Vector128.Narrow(level0, level1), levelsHigh = Vector128.Narrow(level2, level3);
        Vector128<byte> low = levelsLow.AsByte(), high = levelsHigh.AsByte();
        Vector128<byte> zigzag0 = Simd.ShuffleBytes(low, Vector128.Create((byte)0, 1, 2, 3, 8, 9, 0x80, 0x80, 10, 11, 4, 5, 6, 7, 12, 13))
                                | Simd.ShuffleBytes(high, Vector128.Create(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 1, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80));
        Vector128<byte> zigzag1 = Simd.ShuffleBytes(high, Vector128.Create((byte)2, 3, 8, 9, 10, 11, 4, 5, 0x80, 0x80, 6, 7, 12, 13, 14, 15))
                                | Simd.ShuffleBytes(low, Vector128.Create(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 14, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80));
        ref short dst = ref MemoryMarshal.GetReference(output);
        zigzag0.AsInt16().StoreUnsafe(ref dst);
        zigzag1.AsInt16().StoreUnsafe(ref dst, 8);
        return (levelsLow | levelsHigh) != Vector128<short>.Zero;
    }

    // QuantizeBlock_C for coefficients offset..offset+3 in natural order (sharpen, iq and q already widened).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Quantize4(Vector128<int> value, Vector128<uint> sharpen, Vector128<uint> iq, Vector128<uint> q,
        Vp8Matrix matrix, int offset, out Vector128<int> dequantized)
    {
        Vector128<uint> bias = Vector128.LoadUnsafe(ref matrix.Bias[offset]);
        Vector128<uint> zeroThreshold = Vector128.LoadUnsafe(ref matrix.ZeroThreshold[offset]);

        Vector128<uint> coefficient = Vector128.Abs(value).AsUInt32() + sharpen;
        Vector128<int> keep = Vector128.GreaterThan(coefficient, zeroThreshold).AsInt32();
        Vector128<int> level = Vector128.Min(Vector128.ShiftRightLogical((coefficient * iq) + bias, 17).AsInt32(), Vector128.Create(2047));
        Vector128<int> sign = value >> 31;
        level = ((level ^ sign) - sign) & keep;
        dequantized = level * q.AsInt32();
        return level;
    }
}
