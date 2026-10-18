using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal static partial class Vp8ModeDecision
{
    // IsFlat counting the non-zero AC levels of each block at once; the scalar loop stops early with the same answer.
    private static bool IsFlatSimd(ReadOnlySpan<short> levels, int blocks, int threshold)
    {
        ref short level = ref MemoryMarshal.GetReference(levels);
        int count = 0;
        for (int b = 0; b < blocks; b++)
        {
            uint zero = Vector128.Equals(Vector128.LoadUnsafe(ref level, (nuint)(b * 16)), Vector128<short>.Zero).ExtractMostSignificantBits()
                      | (Vector128.Equals(Vector128.LoadUnsafe(ref level, (nuint)((b * 16) + 8)), Vector128<short>.Zero).ExtractMostSignificantBits() << 8);
            count += BitOperations.PopCount(~zero & 0xFFFE);
        }

        return count <= threshold;
    }
}
