using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace RasterSharp.Compression;

internal static partial class Crc32
{
    private const int SimdMinimumLength = 64;

    /// <summary>Selects the vector body for this CPU; returns the number of bytes consumed (0 without one).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UpdateSimd(ref uint crc, ReadOnlySpan<byte> data)
    {
        if (data.Length < SimdMinimumLength)
        {
            return 0;
        }

        if (Pclmulqdq.IsSupported && Sse41.IsSupported)
        {
            return UpdateX86(ref crc, data);
        }

        return ArmCrc32.Arm64.IsSupported ? UpdateArm(ref crc, data) : 0;
    }
}
