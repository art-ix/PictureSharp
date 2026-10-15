using System.Runtime.CompilerServices;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal static partial class JpegFdct
{
    public static void Transform(Span<int> data)
    {
        if (!TransformSimd(data)) TransformScalar(data);
    }

    // port: libjpeg-turbo jfdctint.c jpeg_fdct_islow (8-bit, CONST_BITS=13, PASS1_BITS=2)
    internal static void TransformScalar(Span<int> data)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            int stride = pass == 0 ? 1 : 8;
            int shift = pass == 0 ? 11 : 15;
            for (int i = 0; i < 8; i++)
            {
                Span<int> d = data[(pass == 0 ? i * 8 : i)..];
                int t0 = d[0] + d[7 * stride], t7 = d[0] - d[7 * stride];
                int t1 = d[stride] + d[6 * stride], t6 = d[stride] - d[6 * stride];
                int t2 = d[2 * stride] + d[5 * stride], t5 = d[2 * stride] - d[5 * stride];
                int t3 = d[3 * stride] + d[4 * stride], t4 = d[3 * stride] - d[4 * stride];
                int t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
                d[0] = pass == 0 ? (t10 + t11) << 2 : Descale(t10 + t11, 2);
                d[4 * stride] = pass == 0 ? (t10 - t11) << 2 : Descale(t10 - t11, 2);
                int z1 = (t12 + t13) * 4433;
                d[2 * stride] = Descale(z1 + t13 * 6270, shift);
                d[6 * stride] = Descale(z1 - t12 * 15137, shift);
                z1 = t4 + t7;
                int z2 = t5 + t6, z3 = t4 + t6, z4 = t5 + t7;
                int z5 = (z3 + z4) * 9633;
                t4 *= 2446;
                t5 *= 16819;
                t6 *= 25172;
                t7 *= 12299;
                z1 *= -7373;
                z2 *= -20995;
                z3 = z3 * -16069 + z5;
                z4 = z4 * -3196 + z5;
                d[7 * stride] = Descale(t4 + z1 + z3, shift);
                d[5 * stride] = Descale(t5 + z2 + z4, shift);
                d[3 * stride] = Descale(t6 + z2 + z3, shift);
                d[stride] = Descale(t7 + z1 + z4, shift);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Descale(int value, int bits) => (value + (1 << (bits - 1))) >> bits;

    /// <summary>Quantization divisors (8 × table entry) with exact reciprocals.</summary>
    public sealed class Divisors
    {
        internal readonly int[] Half = new int[64];
        internal readonly ulong[] Reciprocal = new ulong[64];

        // Lemire et al., "Faster Remainder by Direct Computation": with M = floor((2^64 - 1) / d) + 1,
        // the high 64 bits of M * n equal n / d for every 32-bit n.
        public Divisors(ReadOnlySpan<byte> table)
        {
            for (int i = 0; i < 64; i++)
            {
                int divisor = table[i] * 8;
                Half[i] = divisor / 2;
                Reciprocal[i] = ulong.MaxValue / (uint)divisor + 1;
            }
        }
    }

    /// <summary>
    /// <see cref="Quantize(ReadOnlySpan{int}, ReadOnlySpan{byte}, Span{short})"/> without division, writing the
    /// coefficients in zigzag order.
    /// </summary>
    public static void QuantizeZigzag(ReadOnlySpan<int> data, Divisors divisors, Span<short> result)
    {
        int[] half = divisors.Half;
        ulong[] reciprocal = divisors.Reciprocal;
        ReadOnlySpan<byte> order = JpegTables.NaturalOrder;
        for (int k = 0; k < 64; k++)
        {
            int i = order[k];
            int v = data[i];
            int value = (int)Math.BigMul(reciprocal[i], (ulong)(uint)(Math.Abs(v) + half[i]), out _);
            result[k] = (short)(v < 0 ? -value : value);
        }
    }

    // port: libjpeg-turbo jcdctmgr.c quantize (division equivalent of reciprocal implementation)
    public static void Quantize(ReadOnlySpan<int> data, ReadOnlySpan<byte> table, Span<short> result)
    {
        for (int i = 0; i < 64; i++)
        {
            int divisor = table[i] * 8;
            int value = (Math.Abs(data[i]) + divisor / 2) / divisor;
            result[i] = (short)(data[i] < 0 ? -value : value);
        }
    }
}
