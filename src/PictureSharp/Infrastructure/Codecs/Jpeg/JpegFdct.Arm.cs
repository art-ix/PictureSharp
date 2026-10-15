using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal static partial class JpegFdct
{
    /// <summary>
    /// <see cref="TransformAvx2"/> for ARM64: each 256-bit row is a pair of 128-bit halves (columns 0-3 and 4-7), and
    /// both halves go through the same 32-bit operations as the scalar code.
    /// </summary>
    private static void TransformArm(Span<int> data)
    {
        ref int d = ref MemoryMarshal.GetReference(data[..64]);
        Block8x8 b = Block8x8.Load(ref d);

        // Pass 1 transforms rows: after the transpose, row k holds element k of every row.
        b.Transpose();
        Pass(ref b.L0, ref b.L1, ref b.L2, ref b.L3, ref b.L4, ref b.L5, ref b.L6, ref b.L7, first: true);
        Pass(ref b.H0, ref b.H1, ref b.H2, ref b.H3, ref b.H4, ref b.H5, ref b.H6, ref b.H7, first: true);

        // Pass 2 transforms columns.
        b.Transpose();
        Pass(ref b.L0, ref b.L1, ref b.L2, ref b.L3, ref b.L4, ref b.L5, ref b.L6, ref b.L7, first: false);
        Pass(ref b.H0, ref b.H1, ref b.H2, ref b.H3, ref b.H4, ref b.H5, ref b.H6, ref b.H7, first: false);
        b.Store(ref d);
    }

    // port: libjpeg-turbo jfdctint.c jpeg_fdct_islow, one pass on four lanes (d0..d7 are the eight inputs)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass(ref Vector128<int> d0, ref Vector128<int> d1, ref Vector128<int> d2, ref Vector128<int> d3,
        ref Vector128<int> d4, ref Vector128<int> d5, ref Vector128<int> d6, ref Vector128<int> d7, bool first)
    {
        int shift = first ? 11 : 15;
        Vector128<int> t0 = d0 + d7, t7 = d0 - d7;
        Vector128<int> t1 = d1 + d6, t6 = d1 - d6;
        Vector128<int> t2 = d2 + d5, t5 = d2 - d5;
        Vector128<int> t3 = d3 + d4, t4 = d3 - d4;
        Vector128<int> t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
        d0 = first ? (t10 + t11) << 2 : Descale(t10 + t11, 2);
        d4 = first ? (t10 - t11) << 2 : Descale(t10 - t11, 2);
        Vector128<int> z1 = (t12 + t13) * Vector128.Create(4433);
        d2 = Descale(z1 + t13 * Vector128.Create(6270), shift);
        d6 = Descale(z1 - t12 * Vector128.Create(15137), shift);
        z1 = t4 + t7;
        Vector128<int> z2 = t5 + t6, z3 = t4 + t6, z4 = t5 + t7;
        Vector128<int> z5 = (z3 + z4) * Vector128.Create(9633);
        t4 *= Vector128.Create(2446);
        t5 *= Vector128.Create(16819);
        t6 *= Vector128.Create(25172);
        t7 *= Vector128.Create(12299);
        z1 *= Vector128.Create(-7373);
        z2 *= Vector128.Create(-20995);
        z3 = z3 * Vector128.Create(-16069) + z5;
        z4 = z4 * Vector128.Create(-3196) + z5;
        d7 = Descale(t4 + z1 + z3, shift);
        d5 = Descale(t5 + z2 + z4, shift);
        d3 = Descale(t6 + z2 + z3, shift);
        d1 = Descale(t7 + z1 + z4, shift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Descale(Vector128<int> value, int bits) => (value + Vector128.Create(1 << (bits - 1))) >> bits;

    /// <summary>An 8x8 block of 32-bit values as eight rows of two 128-bit halves (Lk: columns 0-3, Hk: columns 4-7).</summary>
    internal struct Block8x8
    {
        public Vector128<int> L0, H0, L1, H1, L2, H2, L3, H3, L4, H4, L5, H5, L6, H6, L7, H7;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Block8x8 Load(ref int d) => new()
        {
            L0 = Vector128.LoadUnsafe(ref d),
            H0 = Vector128.LoadUnsafe(ref d, 4),
            L1 = Vector128.LoadUnsafe(ref d, 8),
            H1 = Vector128.LoadUnsafe(ref d, 12),
            L2 = Vector128.LoadUnsafe(ref d, 16),
            H2 = Vector128.LoadUnsafe(ref d, 20),
            L3 = Vector128.LoadUnsafe(ref d, 24),
            H3 = Vector128.LoadUnsafe(ref d, 28),
            L4 = Vector128.LoadUnsafe(ref d, 32),
            H4 = Vector128.LoadUnsafe(ref d, 36),
            L5 = Vector128.LoadUnsafe(ref d, 40),
            H5 = Vector128.LoadUnsafe(ref d, 44),
            L6 = Vector128.LoadUnsafe(ref d, 48),
            H6 = Vector128.LoadUnsafe(ref d, 52),
            L7 = Vector128.LoadUnsafe(ref d, 56),
            H7 = Vector128.LoadUnsafe(ref d, 60),
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Store(ref int d)
        {
            L0.StoreUnsafe(ref d); H0.StoreUnsafe(ref d, 4);
            L1.StoreUnsafe(ref d, 8); H1.StoreUnsafe(ref d, 12);
            L2.StoreUnsafe(ref d, 16); H2.StoreUnsafe(ref d, 20);
            L3.StoreUnsafe(ref d, 24); H3.StoreUnsafe(ref d, 28);
            L4.StoreUnsafe(ref d, 32); H4.StoreUnsafe(ref d, 36);
            L5.StoreUnsafe(ref d, 40); H5.StoreUnsafe(ref d, 44);
            L6.StoreUnsafe(ref d, 48); H6.StoreUnsafe(ref d, 52);
            L7.StoreUnsafe(ref d, 56); H7.StoreUnsafe(ref d, 60);
        }

        /// <summary>Transposes the block: the four 4x4 quadrants are transposed and the off-diagonal ones swapped.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Transpose()
        {
            Simd.Transpose(ref L0, ref L1, ref L2, ref L3);
            Simd.Transpose(ref H0, ref H1, ref H2, ref H3);
            Simd.Transpose(ref L4, ref L5, ref L6, ref L7);
            Simd.Transpose(ref H4, ref H5, ref H6, ref H7);
            (H0, L4) = (L4, H0);
            (H1, L5) = (L5, H1);
            (H2, L6) = (L6, H2);
            (H3, L7) = (L7, H3);
        }
    }
}
