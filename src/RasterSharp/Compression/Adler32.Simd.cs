using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace RasterSharp.Compression;

internal static partial class Adler32
{
    private const int BlockSize = 32;

    /// <summary>Selects the vector body for this CPU; returns the number of bytes consumed (0 without one).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UpdateSimd(ref uint adler, ReadOnlySpan<byte> data)
    {
        if (data.Length < BlockSize)
        {
            return 0;
        }

        if (Ssse3.IsSupported)
        {
            return UpdateX86(ref adler, data);
        }

        return AdvSimd.IsSupported ? UpdateArm(ref adler, data) : 0;
    }
}
