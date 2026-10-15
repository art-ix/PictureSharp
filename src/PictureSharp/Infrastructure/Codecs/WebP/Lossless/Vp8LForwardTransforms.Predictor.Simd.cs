using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Infrastructure.Codecs.WebP;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

// port: src/dsp/lossless_enc_sse2.c, src/dsp/lossless_enc_avx2.c PredictorSub0..13
// AVX2 on x86; 128-bit code on SSE2 or ARM64 through the Simd helpers. The encoder knows the left pixel in advance, so every residual of a run is independent.
// Each routine returns how many pixels it consumed; the caller finishes the tail in scalar code.
internal static partial class Vp8LForwardTransforms
{
    // Residuals current[i] - predict(mode, i) for 'count' pixels with current[-1], upper[-1] and upper[count] readable.
    private static int PredictorSubSimd(int mode, ref uint current, ref uint upper, int count, ref uint output)
    {
        if (!Simd.IsSupported128) return 0;
        int i = 0;
        if (Avx2.IsSupported)
        {
            for (; i + 8 <= count; i += 8)
            {
                Vector256<byte> source = Load8(ref current, i);
                Vector256<byte> prediction = mode switch
                {
                    0 => Vector256.Create(WebPConstants.ArgbBlack).AsByte(),
                    1 => Load8(ref current, i - 1),
                    2 => Load8(ref upper, i),
                    3 => Load8(ref upper, i + 1),
                    4 => Load8(ref upper, i - 1),
                    5 => Simd.Average2(Simd.Average2(Load8(ref current, i - 1), Load8(ref upper, i + 1)), Load8(ref upper, i)),
                    6 => Simd.Average2(Load8(ref current, i - 1), Load8(ref upper, i - 1)),
                    7 => Simd.Average2(Load8(ref current, i - 1), Load8(ref upper, i)),
                    8 => Simd.Average2(Load8(ref upper, i - 1), Load8(ref upper, i)),
                    9 => Simd.Average2(Load8(ref upper, i), Load8(ref upper, i + 1)),
                    10 => Simd.Average2(Simd.Average2(Load8(ref upper, i), Load8(ref upper, i + 1)),
                        Simd.Average2(Load8(ref current, i - 1), Load8(ref upper, i - 1))),
                    11 => SelectVector(Load8(ref current, i - 1), Load8(ref upper, i), Load8(ref upper, i - 1)),
                    12 => ClampedAddSubtractFullVector(Load8(ref current, i - 1), Load8(ref upper, i), Load8(ref upper, i - 1)),
                    _ => ClampedAddSubtractHalfVector(Load8(ref current, i - 1), Load8(ref upper, i), Load8(ref upper, i - 1)),
                };
                Avx2.Subtract(source, prediction).AsUInt32().StoreUnsafe(ref output, (nuint)i);
            }
        }
        for (; i + 4 <= count; i += 4)
        {
            Vector128<byte> source = Load4(ref current, i);
            Vector128<byte> prediction = mode switch
            {
                0 => Vector128.Create(WebPConstants.ArgbBlack).AsByte(),
                1 => Load4(ref current, i - 1),
                2 => Load4(ref upper, i),
                3 => Load4(ref upper, i + 1),
                4 => Load4(ref upper, i - 1),
                5 => Simd.Average2(Simd.Average2(Load4(ref current, i - 1), Load4(ref upper, i + 1)), Load4(ref upper, i)),
                6 => Simd.Average2(Load4(ref current, i - 1), Load4(ref upper, i - 1)),
                7 => Simd.Average2(Load4(ref current, i - 1), Load4(ref upper, i)),
                8 => Simd.Average2(Load4(ref upper, i - 1), Load4(ref upper, i)),
                9 => Simd.Average2(Load4(ref upper, i), Load4(ref upper, i + 1)),
                10 => Simd.Average2(Simd.Average2(Load4(ref upper, i), Load4(ref upper, i + 1)),
                    Simd.Average2(Load4(ref current, i - 1), Load4(ref upper, i - 1))),
                11 => SelectVector(Load4(ref current, i - 1), Load4(ref upper, i), Load4(ref upper, i - 1)),
                12 => ClampedAddSubtractFullVector(Load4(ref current, i - 1), Load4(ref upper, i), Load4(ref upper, i - 1)),
                _ => ClampedAddSubtractHalfVector(Load4(ref current, i - 1), Load4(ref upper, i), Load4(ref upper, i - 1)),
            };
            (source - prediction).AsUInt32().StoreUnsafe(ref output, (nuint)i);
        }
        return i;
    }

    // Whether any of the pixels is fully transparent.
    private static bool AnyTransparentSimd(ReadOnlySpan<uint> pixels, out int consumed)
    {
        ref uint p = ref MemoryMarshal.GetReference(pixels);
        Vector128<uint> alpha = Vector128.Create(0xff000000u);
        int i = 0;
        if (Simd.IsSupported128)
        {
            for (; i + 4 <= pixels.Length; i += 4)
            {
                if (Vector128.EqualsAny(Vector128.LoadUnsafe(ref p, (nuint)i) & alpha, Vector128<uint>.Zero))
                {
                    consumed = i;
                    return true;
                }
            }
        }
        consumed = i;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load4(ref uint pixels, int offset) => Vector128.LoadUnsafe(ref Unsafe.Add(ref pixels, offset)).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Load8(ref uint pixels, int offset) => Vector256.LoadUnsafe(ref Unsafe.Add(ref pixels, offset)).AsByte();

    // port: src/dsp/lossless_enc_sse2.c GetSumAbsDiff32_SSE2, PredictorSub11_SSE2
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> SelectVector(Vector128<byte> left, Vector128<byte> top, Vector128<byte> topLeft)
    {
        Vector128<int> pa = SumAbsDiff32(top, topLeft), pb = SumAbsDiff32(left, topLeft);
        Vector128<byte> mask = Vector128.GreaterThan(pb, pa).AsByte();
        return (mask & left) | Vector128.AndNot(top, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> SelectVector(Vector256<byte> left, Vector256<byte> top, Vector256<byte> topLeft)
    {
        Vector256<int> pa = SumAbsDiff32(top, topLeft), pb = SumAbsDiff32(left, topLeft);
        Vector256<byte> mask = Avx2.CompareGreaterThan(pb, pa).AsByte();
        return Avx2.Or(Avx2.And(mask, left), Avx2.AndNot(mask, top));
    }

    // Per pixel sum of |a - b| over the four bytes. Unpacking with the same upper half (a) on both operands
    // leaves the other 32 bits of each sad lane at zero.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> SumAbsDiff32(Vector128<byte> a, Vector128<byte> b)
    {
        Vector128<uint> a32 = a.AsUInt32(), b32 = b.AsUInt32();
        Vector128<ushort> low = Simd.SumAbsoluteDifferences(Simd.UnpackLow(a32, a32).AsByte(), Simd.UnpackLow(b32, a32).AsByte());
        Vector128<ushort> high = Simd.SumAbsoluteDifferences(Simd.UnpackHigh(a32, a32).AsByte(), Simd.UnpackHigh(b32, a32).AsByte());
        return Simd.PackSignedSaturate(low.AsInt32(), high.AsInt32()).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> SumAbsDiff32(Vector256<byte> a, Vector256<byte> b)
    {
        Vector256<uint> a32 = a.AsUInt32(), b32 = b.AsUInt32();
        Vector256<ushort> low = Avx2.SumAbsoluteDifferences(Avx2.UnpackLow(a32, a32).AsByte(), Avx2.UnpackLow(b32, a32).AsByte());
        Vector256<ushort> high = Avx2.SumAbsoluteDifferences(Avx2.UnpackHigh(a32, a32).AsByte(), Avx2.UnpackHigh(b32, a32).AsByte());
        return Avx2.PackSignedSaturate(low.AsInt32(), high.AsInt32()).AsInt32();
    }

    // port: src/dsp/lossless_enc_sse2.c PredictorSub12_SSE2
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ClampedAddSubtractFullVector(Vector128<byte> left, Vector128<byte> top, Vector128<byte> topLeft)
    {
        Vector128<byte> zero = Vector128<byte>.Zero;
        Vector128<short> low = Simd.UnpackLow(left, zero).AsInt16() + (Simd.UnpackLow(top, zero).AsInt16() - Simd.UnpackLow(topLeft, zero).AsInt16());
        Vector128<short> high = Simd.UnpackHigh(left, zero).AsInt16() + (Simd.UnpackHigh(top, zero).AsInt16() - Simd.UnpackHigh(topLeft, zero).AsInt16());
        return Simd.PackUnsignedSaturate(low, high);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> ClampedAddSubtractFullVector(Vector256<byte> left, Vector256<byte> top, Vector256<byte> topLeft)
    {
        Vector256<byte> zero = Vector256<byte>.Zero;
        Vector256<short> low = Avx2.Add(Avx2.UnpackLow(left, zero).AsInt16(),
            Avx2.Subtract(Avx2.UnpackLow(top, zero).AsInt16(), Avx2.UnpackLow(topLeft, zero).AsInt16()));
        Vector256<short> high = Avx2.Add(Avx2.UnpackHigh(left, zero).AsInt16(),
            Avx2.Subtract(Avx2.UnpackHigh(top, zero).AsInt16(), Avx2.UnpackHigh(topLeft, zero).AsInt16()));
        return Avx2.PackUnsignedSaturate(low, high);
    }

    // port: src/dsp/lossless_enc_sse2.c PredictorSub13_SSE2
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ClampedAddSubtractHalfVector(Vector128<byte> left, Vector128<byte> top, Vector128<byte> topLeft)
    {
        Vector128<byte> zero = Vector128<byte>.Zero;
        return Simd.PackUnsignedSaturate(AddSubtractHalf(Simd.UnpackLow(left, zero).AsInt16(), Simd.UnpackLow(top, zero).AsInt16(), Simd.UnpackLow(topLeft, zero).AsInt16()), AddSubtractHalf(Simd.UnpackHigh(left, zero).AsInt16(), Simd.UnpackHigh(top, zero).AsInt16(), Simd.UnpackHigh(topLeft, zero).AsInt16()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> ClampedAddSubtractHalfVector(Vector256<byte> left, Vector256<byte> top, Vector256<byte> topLeft)
    {
        Vector256<byte> zero = Vector256<byte>.Zero;
        return Avx2.PackUnsignedSaturate(
            AddSubtractHalf(Avx2.UnpackLow(left, zero).AsInt16(), Avx2.UnpackLow(top, zero).AsInt16(), Avx2.UnpackLow(topLeft, zero).AsInt16()),
            AddSubtractHalf(Avx2.UnpackHigh(left, zero).AsInt16(), Avx2.UnpackHigh(top, zero).AsInt16(), Avx2.UnpackHigh(topLeft, zero).AsInt16()));
    }

    // C division truncates toward zero: negative odd differences are corrected before the arithmetic shift.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> AddSubtractHalf(Vector128<short> left, Vector128<short> top, Vector128<short> topLeft)
    {
        Vector128<short> average = (top + left) >>> 1;
        Vector128<short> difference = (average - topLeft) - Vector128.GreaterThan(topLeft, average);
        return average + (difference >> 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> AddSubtractHalf(Vector256<short> left, Vector256<short> top, Vector256<short> topLeft)
    {
        Vector256<short> average = Avx2.ShiftRightLogical(Avx2.Add(top, left), 1);
        Vector256<short> difference = Avx2.Subtract(Avx2.Subtract(average, topLeft), Avx2.CompareGreaterThan(topLeft, average));
        return Avx2.Add(average, Avx2.ShiftRightArithmetic(difference, 1));
    }
}
