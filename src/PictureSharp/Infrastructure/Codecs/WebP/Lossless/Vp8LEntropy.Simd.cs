using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

internal static partial class Vp8LEntropy
{
    // Bit i is set when counts[i] != 0 (CombinedShannonEntropy_SSE2/AVX2 iterate the same mask).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint NonZeroMask32(ReadOnlySpan<uint> counts)
    {
        ref uint c = ref MemoryMarshal.GetReference(counts[..32]);
        if (Vector256.IsHardwareAccelerated)
        {
            uint mask = 0;
            for (int i = 0; i < 32; i += 8)
                mask |= Vector256.Equals(Vector256.LoadUnsafe(ref c, (nuint)i), Vector256<uint>.Zero).ExtractMostSignificantBits() << i;
            return ~mask;
        }
        if (Vector128.IsHardwareAccelerated)
        {
            uint mask = 0;
            for (int i = 0; i < 32; i += 4)
                mask |= Vector128.Equals(Vector128.LoadUnsafe(ref c, (nuint)i), Vector128<uint>.Zero).ExtractMostSignificantBits() << i;
            return ~mask;
        }
        return NonZeroMask32Scalar(counts);
    }

    internal static uint NonZeroMask32Scalar(ReadOnlySpan<uint> counts)
    {
        uint mask = 0;
        for (int i = 0; i < 32; i++) if (counts[i] != 0) mask |= 1u << i;
        return mask;
    }
}
