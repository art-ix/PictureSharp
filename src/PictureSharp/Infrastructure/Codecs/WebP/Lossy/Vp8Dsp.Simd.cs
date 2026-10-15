using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

// SIMD versions of the decoder DSP. They compute in 32-bit lanes like the C code, so their output equals
// the scalar functions for every input (libwebp's 16-bit SSE2 code only does so for valid streams).
internal static partial class Vp8Dsp
{
    /// <summary>Bytes a 4x4 block spans in a buffer with stride <see cref="Bps"/>.</summary>
    internal const int BlockSpan = (3 * Bps) + 4;

    public static void TransformOne(ReadOnlySpan<short> input, byte[] dst, int off)
    {
        if (Simd.IsSupportedSse41)
        {
            CheckBlock(input, dst.Length, off);
            ref byte block = ref dst[off];
            TransformOneSse41(input, ref block, ref block);
            return;
        }

        TransformOneScalar(input, dst, off);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CheckBlock(ReadOnlySpan<short> input, int length, int off)
    {
        if (input.Length < 16 || off < 0 || off > length - BlockSpan) ThrowBlockOutOfRange();
    }

    internal static void ThrowBlockOutOfRange() => throw new ArgumentOutOfRangeException(null, "4x4 block outside of its buffer.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul1(Vector128<int> a) => ((a * 20091) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul2(Vector128<int> a) => (a * 35468) >> 16;

    /// <summary>TransformOne_C adding to the 4x4 block at <paramref name="reference"/> and storing at <paramref name="dst"/> (both stride <see cref="Bps"/>).</summary>
    internal static void TransformOneSse41(ReadOnlySpan<short> input, ref byte reference, ref byte dst)
    {
        ref short coefficients = ref MemoryMarshal.GetReference(input);
        Vector128<short> in01 = Vector128.LoadUnsafe(ref coefficients), in23 = Vector128.LoadUnsafe(ref coefficients, 8);
        Vector128<int> in0 = Vector128.WidenLower(in01), in1 = Vector128.WidenUpper(in01);
        Vector128<int> in2 = Vector128.WidenLower(in23), in3 = Vector128.WidenUpper(in23);

        // Vertical pass, one column per lane.
        Vector128<int> a = in0 + in2, b = in0 - in2;
        Vector128<int> c = Mul2(in1) - Mul1(in3), d = Mul1(in1) + Mul2(in3);
        Vector128<int> t0 = a + d, t1 = b + c, t2 = b - c, t3 = a - d;
        Simd.Transpose(ref t0, ref t1, ref t2, ref t3);

        // Horizontal pass, one row per lane.
        Vector128<int> dc = t0 + Vector128.Create(4);
        a = dc + t2;
        b = dc - t2;
        c = Mul2(t1) - Mul1(t3);
        d = Mul1(t1) + Mul2(t3);
        Vector128<int> o0 = (a + d) >> 3, o1 = (b + c) >> 3, o2 = (b - c) >> 3, o3 = (a - d) >> 3;
        Simd.Transpose(ref o0, ref o1, ref o2, ref o3);

        o0 += Simd.LoadBytes4(ref reference);
        o1 += Simd.LoadBytes4(ref Unsafe.Add(ref reference, Bps));
        o2 += Simd.LoadBytes4(ref Unsafe.Add(ref reference, 2 * Bps));
        o3 += Simd.LoadBytes4(ref Unsafe.Add(ref reference, 3 * Bps));
        // Saturating int -> short -> byte equals clip_8b.
        Vector128<uint> pixels = Simd.PackUnsignedSaturate(Simd.PackSignedSaturate(o0, o1), Simd.PackSignedSaturate(o2, o3)).AsUInt32();
        Unsafe.WriteUnaligned(ref dst, pixels.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Bps), pixels.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2 * Bps), pixels.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 3 * Bps), pixels.GetElement(3));
    }
}
