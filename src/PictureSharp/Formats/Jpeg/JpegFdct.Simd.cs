using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Formats.Jpeg;

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
}
