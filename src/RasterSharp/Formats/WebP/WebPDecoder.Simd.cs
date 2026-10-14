using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using RasterSharp.Internal;

namespace RasterSharp.Formats.WebP;

internal sealed partial class WebPDecoder
{
    // Memory order of a 0xAARRGGBB uint is B,G,R,A. Rgba32 is R,G,B,A. pshufb indices stay inside each lane.
    private static readonly Vector256<byte> ArgbToRgbaShuffle8 = Vector256.Create(
        Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15),
        Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15));

    private static readonly Vector128<byte> ArgbToRgbaShuffle4 =
        Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

    private static int ArgbToRgbaSimd(ReadOnlySpan<uint> argb, Span<Rgba32> dst)
    {
        if (!Simd.IsSupportedSsse3 || argb.IsEmpty || dst.IsEmpty)
        {
            return 0;
        }

        int n = Math.Min(argb.Length, dst.Length);
        ref byte source = ref Unsafe.As<uint, byte>(ref MemoryMarshal.GetReference(argb));
        ref byte target = ref Unsafe.As<Rgba32, byte>(ref MemoryMarshal.GetReference(dst));
        int i = 0;
        if (Avx2.IsSupported)
        {
            for (; i + 8 <= n; i += 8)
            {
                Avx2.Shuffle(Vector256.LoadUnsafe(ref source, (nuint)(i * 4)), ArgbToRgbaShuffle8)
                    .StoreUnsafe(ref target, (nuint)(i * 4));
            }
        }

        for (; i + 4 <= n; i += 4)
        {
            Simd.ShuffleBytes(Vector128.LoadUnsafe(ref source, (nuint)(i * 4)), ArgbToRgbaShuffle4)
                .StoreUnsafe(ref target, (nuint)(i * 4));
        }

        return i;
    }
}
