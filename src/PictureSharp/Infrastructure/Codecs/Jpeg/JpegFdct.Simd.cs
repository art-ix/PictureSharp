using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal static partial class JpegFdct
{
    /// <summary>Runs the vector transform for this CPU; false leaves the block to the scalar code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TransformSimd(Span<int> data)
    {
        if (Avx2.IsSupported)
        {
            TransformAvx2(data);
            return true;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            TransformArm(data);
            return true;
        }

        return false;
    }

    /// <summary>
    /// <see cref="QuantizeZigzagScalar"/> with 128-bit vectors in natural order, then reordered. The quotient is a
    /// single-precision division truncated toward zero: for n &lt; 2^24 the rounding error of n / d stays below 1 / d,
    /// the distance from n / d to the next integer when it is not one, so the result equals the integer division.
    /// </summary>
    private static bool QuantizeZigzagSimd(ReadOnlySpan<int> data, Divisors divisors, Span<short> result)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return false;
        }

        ref int d = ref MemoryMarshal.GetReference(data[..64]);
        ref int half = ref MemoryMarshal.GetArrayDataReference(divisors.Half);
        Span<short> natural = stackalloc short[64];
        ref short n = ref MemoryMarshal.GetReference(natural);
        for (nuint i = 0; i < 64; i += 8)
        {
            Vector128<int> lower = Quotient(ref d, ref half, i);
            Vector128<int> upper = Quotient(ref d, ref half, i + 4);
            Vector128.Narrow(lower, upper).StoreUnsafe(ref n, i);
        }

        ReadOnlySpan<byte> order = JpegTables.NaturalOrder;
        for (int k = 0; k < 64; k++)
        {
            result[k] = natural[order[k]];
        }

        return true;
    }

    // Four signed quotients; FDCT outputs are far below 2^24. The divisors are even, so each is exactly 2 * half.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Quotient(ref int data, ref int half, nuint offset)
    {
        Vector128<int> v = Vector128.LoadUnsafe(ref data, offset);
        Vector128<int> h = Vector128.LoadUnsafe(ref half, offset);
        Vector128<int> magnitude = Vector128.Abs(v) + h;
        Vector128<int> q = Vector128.ConvertToInt32(Vector128.ConvertToSingle(magnitude) / Vector128.ConvertToSingle(h + h));
        Vector128<int> negative = Vector128.LessThan(v, Vector128<int>.Zero);
        return (q ^ negative) - negative;
    }
}
