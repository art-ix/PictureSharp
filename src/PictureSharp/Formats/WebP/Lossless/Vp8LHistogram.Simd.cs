using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.WebP.Lossless;

internal sealed partial class Vp8LHistogram
{
    // Sum, maximum and number of zeros of x + y over whole vectors; returns how many entries were consumed.
    private static int SumMaxZerosSimd(ref uint x, ref uint y, int length, out uint sum, out uint max, out int zeros)
    {
        int i = 0;
        sum = 0; max = 0; zeros = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<uint> sums = default, maxima = default, zeroCounts = default;
            for (; i + 8 <= length; i += 8)
            {
                Vector256<uint> value = Vector256.LoadUnsafe(ref x, (nuint)i) + Vector256.LoadUnsafe(ref y, (nuint)i);
                sums += value;
                maxima = Vector256.Max(maxima, value);
                // Equals gives all ones (-1) for zeros.
                zeroCounts -= Vector256.Equals(value, Vector256<uint>.Zero);
            }
            sum = Vector256.Sum(sums);
            zeros = (int)Vector256.Sum(zeroCounts);
            Vector128<uint> m = Vector128.Max(maxima.GetLower(), maxima.GetUpper());
            max = Math.Max(Math.Max(m[0], m[1]), Math.Max(m[2], m[3]));
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<uint> sums = default, maxima = default, zeroCounts = default;
            for (; i + 4 <= length; i += 4)
            {
                Vector128<uint> value = Vector128.LoadUnsafe(ref x, (nuint)i) + Vector128.LoadUnsafe(ref y, (nuint)i);
                sums += value;
                maxima = Vector128.Max(maxima, value);
                zeroCounts -= Vector128.Equals(value, Vector128<uint>.Zero);
            }
            sum = Vector128.Sum(sums);
            zeros = (int)Vector128.Sum(zeroCounts);
            max = Math.Max(Math.Max(maxima[0], maxima[1]), Math.Max(maxima[2], maxima[3]));
        }
        return i;
    }

    // Bit k is set when x[i + k] + y[i + k] differs from the previous sum; needs i >= 1 and i + 8 <= length.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryRunStarts8(ref uint x, ref uint y, int i, out uint starts)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<uint> current = Vector256.LoadUnsafe(ref x, (nuint)i) + Vector256.LoadUnsafe(ref y, (nuint)i);
            Vector256<uint> previous = Vector256.LoadUnsafe(ref x, (nuint)(i - 1)) + Vector256.LoadUnsafe(ref y, (nuint)(i - 1));
            starts = ~Vector256.Equals(current, previous).ExtractMostSignificantBits() & 0xff;
            return true;
        }
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<uint> low = Vector128.LoadUnsafe(ref x, (nuint)i) + Vector128.LoadUnsafe(ref y, (nuint)i);
            Vector128<uint> high = Vector128.LoadUnsafe(ref x, (nuint)(i + 4)) + Vector128.LoadUnsafe(ref y, (nuint)(i + 4));
            Vector128<uint> lowPrevious = Vector128.LoadUnsafe(ref x, (nuint)(i - 1)) + Vector128.LoadUnsafe(ref y, (nuint)(i - 1));
            Vector128<uint> highPrevious = Vector128.LoadUnsafe(ref x, (nuint)(i + 3)) + Vector128.LoadUnsafe(ref y, (nuint)(i + 3));
            starts = ~(Vector128.Equals(low, lowPrevious).ExtractMostSignificantBits()
                | (Vector128.Equals(high, highPrevious).ExtractMostSignificantBits() << 4)) & 0xff;
            return true;
        }
        starts = 0;
        return false;
    }
}
