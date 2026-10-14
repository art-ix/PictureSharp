using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RasterSharp.Formats.Jpeg;

internal static partial class JpegFdct
{
    /// <summary>
    /// <see cref="TransformScalar"/> with AVX2: the block is transposed so that each pass works on eight rows (or
    /// columns) at once, with the same 32-bit operations as the scalar code.
    /// </summary>
    private static void TransformAvx2(Span<int> data)
    {
        ref int d = ref MemoryMarshal.GetReference(data[..64]);
        Vector256<int> r0 = Vector256.LoadUnsafe(ref d), r1 = Vector256.LoadUnsafe(ref d, 8);
        Vector256<int> r2 = Vector256.LoadUnsafe(ref d, 16), r3 = Vector256.LoadUnsafe(ref d, 24);
        Vector256<int> r4 = Vector256.LoadUnsafe(ref d, 32), r5 = Vector256.LoadUnsafe(ref d, 40);
        Vector256<int> r6 = Vector256.LoadUnsafe(ref d, 48), r7 = Vector256.LoadUnsafe(ref d, 56);

        // Pass 1 transforms rows: after the transpose, vector k holds element k of every row.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Pass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, first: true);

        // Pass 2 transforms columns: transposed back, vector k is row k.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Pass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, first: false);

        r0.StoreUnsafe(ref d);
        r1.StoreUnsafe(ref d, 8);
        r2.StoreUnsafe(ref d, 16);
        r3.StoreUnsafe(ref d, 24);
        r4.StoreUnsafe(ref d, 32);
        r5.StoreUnsafe(ref d, 40);
        r6.StoreUnsafe(ref d, 48);
        r7.StoreUnsafe(ref d, 56);
    }

    // port: libjpeg-turbo jfdctint.c jpeg_fdct_islow, one pass on eight lanes (d0..d7 are the eight inputs)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass(ref Vector256<int> d0, ref Vector256<int> d1, ref Vector256<int> d2, ref Vector256<int> d3,
        ref Vector256<int> d4, ref Vector256<int> d5, ref Vector256<int> d6, ref Vector256<int> d7, bool first)
    {
        int shift = first ? 11 : 15;
        Vector256<int> t0 = d0 + d7, t7 = d0 - d7;
        Vector256<int> t1 = d1 + d6, t6 = d1 - d6;
        Vector256<int> t2 = d2 + d5, t5 = d2 - d5;
        Vector256<int> t3 = d3 + d4, t4 = d3 - d4;
        Vector256<int> t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
        d0 = first ? (t10 + t11) << 2 : Descale(t10 + t11, 2);
        d4 = first ? (t10 - t11) << 2 : Descale(t10 - t11, 2);
        Vector256<int> z1 = (t12 + t13) * Vector256.Create(4433);
        d2 = Descale(z1 + t13 * Vector256.Create(6270), shift);
        d6 = Descale(z1 - t12 * Vector256.Create(15137), shift);
        z1 = t4 + t7;
        Vector256<int> z2 = t5 + t6, z3 = t4 + t6, z4 = t5 + t7;
        Vector256<int> z5 = (z3 + z4) * Vector256.Create(9633);
        t4 *= Vector256.Create(2446);
        t5 *= Vector256.Create(16819);
        t6 *= Vector256.Create(25172);
        t7 *= Vector256.Create(12299);
        z1 *= Vector256.Create(-7373);
        z2 *= Vector256.Create(-20995);
        z3 = z3 * Vector256.Create(-16069) + z5;
        z4 = z4 * Vector256.Create(-3196) + z5;
        d7 = Descale(t4 + z1 + z3, shift);
        d5 = Descale(t5 + z2 + z4, shift);
        d3 = Descale(t6 + z2 + z3, shift);
        d1 = Descale(t7 + z1 + z4, shift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Descale(Vector256<int> value, int bits) => (value + Vector256.Create(1 << (bits - 1))) >> bits;

    /// <summary>Transposes an 8x8 block of 32-bit values held as eight rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose(ref Vector256<int> r0, ref Vector256<int> r1, ref Vector256<int> r2, ref Vector256<int> r3,
        ref Vector256<int> r4, ref Vector256<int> r5, ref Vector256<int> r6, ref Vector256<int> r7)
    {
        Vector256<int> t0 = Avx2.UnpackLow(r0, r1), t1 = Avx2.UnpackHigh(r0, r1);
        Vector256<int> t2 = Avx2.UnpackLow(r2, r3), t3 = Avx2.UnpackHigh(r2, r3);
        Vector256<int> t4 = Avx2.UnpackLow(r4, r5), t5 = Avx2.UnpackHigh(r4, r5);
        Vector256<int> t6 = Avx2.UnpackLow(r6, r7), t7 = Avx2.UnpackHigh(r6, r7);
        Vector256<int> u0 = Avx2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32(), u1 = Avx2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
        Vector256<int> u2 = Avx2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32(), u3 = Avx2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
        Vector256<int> u4 = Avx2.UnpackLow(t4.AsInt64(), t6.AsInt64()).AsInt32(), u5 = Avx2.UnpackHigh(t4.AsInt64(), t6.AsInt64()).AsInt32();
        Vector256<int> u6 = Avx2.UnpackLow(t5.AsInt64(), t7.AsInt64()).AsInt32(), u7 = Avx2.UnpackHigh(t5.AsInt64(), t7.AsInt64()).AsInt32();
        r0 = Avx2.Permute2x128(u0, u4, 0x20);
        r1 = Avx2.Permute2x128(u1, u5, 0x20);
        r2 = Avx2.Permute2x128(u2, u6, 0x20);
        r3 = Avx2.Permute2x128(u3, u7, 0x20);
        r4 = Avx2.Permute2x128(u0, u4, 0x31);
        r5 = Avx2.Permute2x128(u1, u5, 0x31);
        r6 = Avx2.Permute2x128(u2, u6, 0x31);
        r7 = Avx2.Permute2x128(u3, u7, 0x31);
    }
}
