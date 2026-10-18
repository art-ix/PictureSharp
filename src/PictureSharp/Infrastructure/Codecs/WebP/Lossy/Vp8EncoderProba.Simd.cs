using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal sealed partial class Vp8EncoderProba
{
    public static int Last(ReadOnlySpan<short> levels)
    {
        if (Vector128.IsHardwareAccelerated && levels.Length >= 16)
        {
            ref short level = ref MemoryMarshal.GetReference(levels);
            uint zero = Vector128.Equals(Vector128.LoadUnsafe(ref level), Vector128<short>.Zero).ExtractMostSignificantBits()
                      | (Vector128.Equals(Vector128.LoadUnsafe(ref level, 8), Vector128<short>.Zero).ExtractMostSignificantBits() << 8);
            return 31 - BitOperations.LeadingZeroCount(~zero & 0xFFFF);
        }

        return LastScalar(levels);
    }
}
