using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RasterSharp.Internal;

/// <summary>Shuffles and loads shared by the SIMD paths of the codecs (x86 only; callers keep a scalar path).</summary>
internal static class Simd
{
    /// <summary><c>(a + b) &gt;&gt; 1</c> per byte, rounding down. port: src/dsp/lossless_sse2.c Average2_m128i.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Average2(Vector128<byte> a, Vector128<byte> b)
    {
        Vector128<byte> ones = Vector128.Create((byte)1);
        return Sse2.Subtract(Sse2.Average(a, b), Sse2.And(Sse2.Xor(a, b), ones));
    }

    /// <summary><c>(a + b) &gt;&gt; 1</c> per byte, rounding down. port: src/dsp/lossless_avx2.c Average2_m256i.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> Average2(Vector256<byte> a, Vector256<byte> b)
    {
        Vector256<byte> ones = Vector256.Create((byte)1);
        return Avx2.Subtract(Avx2.Average(a, b), Avx2.And(Avx2.Xor(a, b), ones));
    }

    /// <summary>Transposes the 4x4 matrix whose rows are <paramref name="r0"/>..<paramref name="r3"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Transpose(ref Vector128<int> r0, ref Vector128<int> r1, ref Vector128<int> r2, ref Vector128<int> r3)
    {
        Vector128<long> t0 = Sse2.UnpackLow(r0, r1).AsInt64(), t1 = Sse2.UnpackLow(r2, r3).AsInt64();
        Vector128<long> t2 = Sse2.UnpackHigh(r0, r1).AsInt64(), t3 = Sse2.UnpackHigh(r2, r3).AsInt64();
        r0 = Sse2.UnpackLow(t0, t1).AsInt32();
        r1 = Sse2.UnpackHigh(t0, t1).AsInt32();
        r2 = Sse2.UnpackLow(t2, t3).AsInt32();
        r3 = Sse2.UnpackHigh(t2, t3).AsInt32();
    }

    /// <summary>Transposes the two 4x4 matrices held in the 128-bit halves of the rows independently.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Transpose(ref Vector256<int> r0, ref Vector256<int> r1, ref Vector256<int> r2, ref Vector256<int> r3)
    {
        Vector256<long> t0 = Avx2.UnpackLow(r0, r1).AsInt64(), t1 = Avx2.UnpackLow(r2, r3).AsInt64();
        Vector256<long> t2 = Avx2.UnpackHigh(r0, r1).AsInt64(), t3 = Avx2.UnpackHigh(r2, r3).AsInt64();
        r0 = Avx2.UnpackLow(t0, t1).AsInt32();
        r1 = Avx2.UnpackHigh(t0, t1).AsInt32();
        r2 = Avx2.UnpackLow(t2, t3).AsInt32();
        r3 = Avx2.UnpackHigh(t2, t3).AsInt32();
    }

    /// <summary>Zero-extends the four bytes at <paramref name="source"/> to ints.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> LoadBytes4(ref byte source) =>
        Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref source)).AsByte());

    /// <summary>Zero-extends four bytes at <paramref name="low"/> and four at <paramref name="high"/> to the two halves of a vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<int> LoadBytes4x2(ref byte low, ref byte high) =>
        Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(
            Unsafe.ReadUnaligned<uint>(ref low) | ((ulong)Unsafe.ReadUnaligned<uint>(ref high) << 32)).AsByte());
}
