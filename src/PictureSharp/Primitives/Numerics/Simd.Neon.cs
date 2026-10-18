using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Primitives.Numerics;

// 128-bit SSE operations with exact ARM64 (AdvSimd) equivalents. Each helper is the SSE instruction on x86 and the
// matching NEON sequence on ARM64; the JIT keeps only one branch. Results are bit-identical on both architectures.
internal static partial class Simd
{
    /// <summary>True when the 128-bit helpers have an SSE2 or AdvSimd (ARM64) implementation.</summary>
    public static bool IsSupported128
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Sse2.IsSupported || AdvSimd.Arm64.IsSupported;
    }

    /// <summary>As <see cref="IsSupported128"/>, but requiring SSSE3 on x86.</summary>
    public static bool IsSupportedSsse3
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;
    }

    /// <summary>As <see cref="IsSupported128"/>, but requiring SSE4.1 on x86.</summary>
    public static bool IsSupportedSse41
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Sse41.IsSupported || AdvSimd.Arm64.IsSupported;
    }

    /// <summary>Interleaves the low halves of <paramref name="a"/> and <paramref name="b"/> (punpckl / zip1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> UnpackLow<T>(Vector128<T> a, Vector128<T> b)
    {
        if (Sse2.IsSupported)
        {
            return Unsafe.SizeOf<T>() switch
            {
                1 => Sse2.UnpackLow(a.AsByte(), b.AsByte()).As<byte, T>(),
                2 => Sse2.UnpackLow(a.AsInt16(), b.AsInt16()).As<short, T>(),
                4 => Sse2.UnpackLow(a.AsInt32(), b.AsInt32()).As<int, T>(),
                _ => Sse2.UnpackLow(a.AsInt64(), b.AsInt64()).As<long, T>(),
            };
        }

        return Unsafe.SizeOf<T>() switch
        {
            1 => AdvSimd.Arm64.ZipLow(a.AsByte(), b.AsByte()).As<byte, T>(),
            2 => AdvSimd.Arm64.ZipLow(a.AsInt16(), b.AsInt16()).As<short, T>(),
            4 => AdvSimd.Arm64.ZipLow(a.AsInt32(), b.AsInt32()).As<int, T>(),
            _ => AdvSimd.Arm64.ZipLow(a.AsInt64(), b.AsInt64()).As<long, T>(),
        };
    }

    /// <summary>Interleaves the high halves of <paramref name="a"/> and <paramref name="b"/> (punpckh / zip2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> UnpackHigh<T>(Vector128<T> a, Vector128<T> b)
    {
        if (Sse2.IsSupported)
        {
            return Unsafe.SizeOf<T>() switch
            {
                1 => Sse2.UnpackHigh(a.AsByte(), b.AsByte()).As<byte, T>(),
                2 => Sse2.UnpackHigh(a.AsInt16(), b.AsInt16()).As<short, T>(),
                4 => Sse2.UnpackHigh(a.AsInt32(), b.AsInt32()).As<int, T>(),
                _ => Sse2.UnpackHigh(a.AsInt64(), b.AsInt64()).As<long, T>(),
            };
        }

        return Unsafe.SizeOf<T>() switch
        {
            1 => AdvSimd.Arm64.ZipHigh(a.AsByte(), b.AsByte()).As<byte, T>(),
            2 => AdvSimd.Arm64.ZipHigh(a.AsInt16(), b.AsInt16()).As<short, T>(),
            4 => AdvSimd.Arm64.ZipHigh(a.AsInt32(), b.AsInt32()).As<int, T>(),
            _ => AdvSimd.Arm64.ZipHigh(a.AsInt64(), b.AsInt64()).As<long, T>(),
        };
    }

    /// <summary><c>(a + b + 1) &gt;&gt; 1</c> per byte (pavgb / urhadd).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AverageRounded(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.Average(a, b) : AdvSimd.FusedAddRoundedHalving(a, b);

    /// <summary>Signed saturating narrowing of <paramref name="a"/> then <paramref name="b"/> (packssdw / sqxtn).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> PackSignedSaturate(Vector128<int> a, Vector128<int> b) =>
        Sse2.IsSupported ? Sse2.PackSignedSaturate(a, b)
            : AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(a), b);

    /// <summary>Signed saturating narrowing of <paramref name="a"/> then <paramref name="b"/> (packsswb / sqxtn).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<sbyte> PackSignedSaturate(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.PackSignedSaturate(a, b)
            : AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(a), b);

    /// <summary>Signed to unsigned saturating narrowing (packuswb / sqxtun).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> PackUnsignedSaturate(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.PackUnsignedSaturate(a, b)
            : AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(a), b);

    /// <summary>Signed to unsigned saturating narrowing (packusdw / sqxtun).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> PackUnsignedSaturate(Vector128<int> a, Vector128<int> b) =>
        Sse41.IsSupported ? Sse41.PackUnsignedSaturate(a, b)
            : AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(a), b);

    /// <summary>High 16 bits of the unsigned 32-bit products (pmulhuw).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> MultiplyHigh(Vector128<ushort> a, Vector128<ushort> b) =>
        Sse2.IsSupported ? Sse2.MultiplyHigh(a, b)
            : AdvSimd.Arm64.UnzipOdd(AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower()).AsUInt16(),
                AdvSimd.MultiplyWideningUpper(a, b).AsUInt16());

    /// <summary>High 16 bits of the signed 32-bit products (pmulhw).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> MultiplyHigh(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.MultiplyHigh(a, b)
            : AdvSimd.Arm64.UnzipOdd(AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower()).AsInt16(),
                AdvSimd.MultiplyWideningUpper(a, b).AsInt16());

    /// <summary>Sums of adjacent signed 16-bit products, wrapping like pmaddwd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MultiplyAddAdjacent(Vector128<short> a, Vector128<short> b) =>
        Sse2.IsSupported ? Sse2.MultiplyAddAdjacent(a, b)
            : AdvSimd.Arm64.AddPairwise(AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower()), AdvSimd.MultiplyWideningUpper(a, b));

    /// <summary>Sums of absolute byte differences of each 8-byte half, in 16-bit lanes 0 and 4 (psadbw).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> SumAbsoluteDifferences(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.SumAbsoluteDifferences(a, b)
            : AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AbsoluteDifference(a, b)))).AsUInt16();

    /// <summary>Shifts the whole register toward lane 0 by <paramref name="count"/> bytes, filling with zeros (psrldq / ext).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ShiftRightBytes<T>(Vector128<T> value, [ConstantExpected(Min = 1, Max = 15)] byte count) =>
        Sse2.IsSupported ? Sse2.ShiftRightLogical128BitLane(value.AsByte(), count).As<byte, T>()
            : AdvSimd.ExtractVector128(value.AsByte(), Vector128<byte>.Zero, count).As<byte, T>();

    /// <summary>Shifts the whole register away from lane 0 by <paramref name="count"/> bytes, filling with zeros (pslldq / ext).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ShiftLeftBytes<T>(Vector128<T> value, [ConstantExpected(Min = 1, Max = 15)] byte count) =>
        Sse2.IsSupported ? Sse2.ShiftLeftLogical128BitLane(value.AsByte(), count).As<byte, T>()
#pragma warning disable CA1857 // 16 - count folds to a constant once this helper is inlined with a constant count.
            : AdvSimd.ExtractVector128(Vector128<byte>.Zero, value.AsByte(), (byte)(16 - count)).As<byte, T>();
#pragma warning restore CA1857

    /// <summary>Byte table lookup; indices with the high bit set give 0 (pshufb / tbl). Other indices must be below 16.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShuffleBytes(Vector128<byte> value, Vector128<byte> indices) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(value, indices) : AdvSimd.Arm64.VectorTableLookup(value, indices);

    /// <summary>Zero-extends the low four bytes to 32-bit lanes (pmovzxbd).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ZeroExtend4(Vector128<byte> value) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int32(value)
            : AdvSimd.ZeroExtendWideningLower(AdvSimd.ZeroExtendWideningLower(value.GetLower()).GetLower()).AsInt32();

    /// <summary>Copies each even 16-bit lane into the odd lane above it (pshuflw + pshufhw 0xA0 / trn1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> DuplicateEvenWords(Vector128<short> value) =>
        Sse2.IsSupported ? Sse2.ShuffleHigh(Sse2.ShuffleLow(value, 0xA0), 0xA0) : AdvSimd.Arm64.TransposeEven(value, value);

    /// <summary>Broadcasts the last 32-bit lane (pshufd 0xFF / dup).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> BroadcastLast(Vector128<int> value) =>
        Sse2.IsSupported ? Sse2.Shuffle(value, 0xFF) : AdvSimd.DuplicateSelectedScalarToVector128(value, 3);
}
