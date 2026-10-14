using System.Numerics;
using System.Runtime.Intrinsics;

namespace RasterSharp.Formats.WebP.Lossy;

internal sealed partial class Vp8EncoderProba
{
    public static int Last(ReadOnlySpan<short> levels)
    {
        if (Vector256.IsHardwareAccelerated && levels.Length >= 16)
        {
            uint nonzero = ~Vector256.Equals(Vector256.Create(levels[..16]), Vector256<short>.Zero).ExtractMostSignificantBits() & 0xFFFF;
            return 31 - BitOperations.LeadingZeroCount(nonzero);
        }

        return LastScalar(levels);
    }
}
