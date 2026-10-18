using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal static partial class JpegIdct
{
    // Each pass multiplies its largest input by at most 178219 (the sum of the constant magnitudes along the longest
    // path), so with inputs below this limit every 32-bit intermediate equals the scalar 64-bit one.
    private const int SimdInputLimit = 12000;

    /// <summary>Runs the vector transform for this CPU; false leaves the block to the scalar code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TransformSimd(ReadOnlySpan<short> block, ReadOnlySpan<short> quant, Span<byte> output, int stride)
    {
        if (Avx2.IsSupported)
        {
            return TransformAvx2(block, quant, output, stride, block, quant, output, stride);
        }

        return AdvSimd.Arm64.IsSupported && TransformArm(block, quant, output, stride);
    }

    /// <summary>Runs the vector transform of two blocks at once where this CPU has one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TransformSimd(ReadOnlySpan<short> blockA, ReadOnlySpan<short> quantA, Span<byte> outputA, int strideA,
        ReadOnlySpan<short> blockB, ReadOnlySpan<short> quantB, Span<byte> outputB, int strideB) =>
        Avx2.IsSupported && TransformAvx2(blockA, quantA, outputA, strideA, blockB, quantB, outputB, strideB);
}
