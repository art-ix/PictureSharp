using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Jpeg;

internal sealed partial class JpegScanEncoder
{
    /// <summary>Bit k is set when <paramref name="coefficients"/>[k] is not zero.</summary>
    public static ulong NonZeroMask(ReadOnlySpan<short> coefficients)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return NonZeroMaskScalar(coefficients);
        }

        ref short c = ref MemoryMarshal.GetReference(coefficients[..64]);
        ulong mask = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector128<short> zero0 = Vector128.Equals(Vector128.LoadUnsafe(ref c, (nuint)(16 * i)), Vector128<short>.Zero);
            Vector128<short> zero1 = Vector128.Equals(Vector128.LoadUnsafe(ref c, (nuint)((16 * i) + 8)), Vector128<short>.Zero);
            mask |= (ulong)Vector128.Narrow(zero0, zero1).ExtractMostSignificantBits() << (16 * i);
        }

        return ~mask;
    }

    /// <summary>Bit k is set when |<paramref name="coefficients"/>[k]| is at least <paramref name="threshold"/> (1 or more).</summary>
    public static ulong AtLeastMask(ReadOnlySpan<short> coefficients, int threshold)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return AtLeastMaskScalar(coefficients, threshold);
        }

        ref short c = ref MemoryMarshal.GetReference(coefficients[..64]);
        Vector128<short> limit = Vector128.Create((short)(threshold - 1));
        ulong mask = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector128<short> above0 = Vector128.GreaterThan(Vector128.Abs(Vector128.LoadUnsafe(ref c, (nuint)(16 * i))), limit);
            Vector128<short> above1 = Vector128.GreaterThan(Vector128.Abs(Vector128.LoadUnsafe(ref c, (nuint)((16 * i) + 8))), limit);
            mask |= (ulong)Vector128.Narrow(above0, above1).ExtractMostSignificantBits() << (16 * i);
        }

        return mask;
    }

    internal static ulong AtLeastMaskScalar(ReadOnlySpan<short> coefficients, int threshold)
    {
        ulong mask = 0;
        for (int k = 0; k < 64; k++)
        {
            if (Math.Abs((int)coefficients[k]) >= threshold) mask |= 1UL << k;
        }

        return mask;
    }

    internal static ulong NonZeroMaskScalar(ReadOnlySpan<short> coefficients)
    {
        ulong mask = 0;
        for (int k = 0; k < 64; k++)
        {
            if (coefficients[k] != 0) mask |= 1UL << k;
        }

        return mask;
    }
}
