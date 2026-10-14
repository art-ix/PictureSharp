using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using RasterSharp.Internal;

namespace RasterSharp.Formats.WebP.Lossless;

// port: src/dsp/lossless_sse2.c, src/dsp/lossless_avx2.c, src/dsp/lossless_enc_sse2.c, src/dsp/lossless_enc_avx2.c
// x86 only. Each routine returns how many pixels it consumed; the caller finishes the tail in scalar code.
internal static partial class LosslessTransforms
{
    // _MM_SHUFFLE(2, 2, 0, 0): duplicate words 0 and 2 inside a 64-bit half.
    private const byte ShuffleDuplicatePairs = (2 << 6) | (2 << 4);

    // pshufb is lane-local, so both 128-bit halves use the same indices. 0xFF writes a zero.
    // Green byte (index 1, 5, 9, 13) lands on the blue and red bytes.
    private static readonly Vector256<byte> GreenOnRedBlue = Vector256.Create(
        Vector128.Create((byte)1, 0xFF, 1, 0xFF, 5, 0xFF, 5, 0xFF, 9, 0xFF, 9, 0xFF, 13, 0xFF, 13, 0xFF),
        Vector128.Create((byte)1, 0xFF, 1, 0xFF, 5, 0xFF, 5, 0xFF, 9, 0xFF, 9, 0xFF, 13, 0xFF, 13, 0xFF));

    // Green in the high byte of each 16-bit lane (perm1 of TransformColorInverse_AVX2).
    private static readonly Vector256<byte> GreenHigh = Vector256.Create(
        Vector128.Create((byte)0xFF, 1, 0xFF, 1, 0xFF, 5, 0xFF, 5, 0xFF, 9, 0xFF, 9, 0xFF, 13, 0xFF, 13),
        Vector128.Create((byte)0xFF, 1, 0xFF, 1, 0xFF, 5, 0xFF, 5, 0xFF, 9, 0xFF, 9, 0xFF, 13, 0xFF, 13));

    // Red byte (index 2, 6, 10, 14) in the high byte of the low word (perm2).
    private static readonly Vector256<byte> RedHigh = Vector256.Create(
        Vector128.Create((byte)0xFF, 2, 0xFF, 0xFF, 0xFF, 6, 0xFF, 0xFF, 0xFF, 10, 0xFF, 0xFF, 0xFF, 14, 0xFF, 0xFF),
        Vector128.Create((byte)0xFF, 2, 0xFF, 0xFF, 0xFF, 6, 0xFF, 0xFF, 0xFF, 10, 0xFF, 0xFF, 0xFF, 14, 0xFF, 0xFF));

    private static int ApplyGreenSimd(Span<uint> pixels, bool add)
    {
        if (!Sse2.IsSupported)
        {
            return 0;
        }

        ref uint p = ref MemoryMarshal.GetReference(pixels);
        int i = 0;
        if (Avx2.IsSupported)
        {
            for (; i + 8 <= pixels.Length; i += 8)
            {
                ref byte at = ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref p, i));
                Vector256<byte> input = Vector256.LoadUnsafe(ref at);
                Vector256<byte> green = Avx2.Shuffle(input, GreenOnRedBlue);
                (add ? Avx2.Add(input, green) : Avx2.Subtract(input, green)).StoreUnsafe(ref at);
            }
        }

        for (; i + 4 <= pixels.Length; i += 4)
        {
            ApplyGreen4(ref Unsafe.Add(ref p, i), add);
        }

        return i;
    }

    private static void ApplyGreen4(ref uint pixels, bool add)
    {
        ref byte at = ref Unsafe.As<uint, byte>(ref pixels);
        Vector128<byte> input = Vector128.LoadUnsafe(ref at);
        Vector128<short> shifted = Sse2.ShiftRightLogical(input.AsInt16(), 8);
        Vector128<byte> green = Sse2.ShuffleHigh(Sse2.ShuffleLow(shifted, ShuffleDuplicatePairs), ShuffleDuplicatePairs).AsByte();
        (add ? Sse2.Add(input, green) : Sse2.Subtract(input, green)).StoreUnsafe(ref at);
    }

    // ((int16_t)(component << 8)) >> 5, the multiplier libwebp feeds to mulhi.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short FixedMultiplier(int component) => (short)((short)(component << 8) >> 5);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PackMultiplier(short high, short low) => (high << 16) | (low & 0xffff);

    private static int TransformColorInverseSimd(sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue, Span<uint> pixels)
    {
        if (!Sse2.IsSupported)
        {
            return 0;
        }

        short toRed = FixedMultiplier(greenToRed), toBlue = FixedMultiplier(greenToBlue), toBlue2 = FixedMultiplier(redToBlue);
        ref uint p = ref MemoryMarshal.GetReference(pixels);
        int i = 0;
        if (Avx2.IsSupported)
        {
            Vector256<short> multsRb = Vector256.Create(PackMultiplier(toRed, toBlue)).AsInt16();
            // Red is shuffled into word 0 here (RedHigh), so its multiplier sits in word 0. SSE2 keeps red in word 1.
            Vector256<short> multsB2 = Vector256.Create((int)toBlue2).AsInt16();
            Vector256<byte> maskAg = Vector256.Create(0xff00ff00u).AsByte();
            for (; i + 8 <= pixels.Length; i += 8)
            {
                ref byte at = ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref p, i));
                Vector256<byte> argb = Vector256.LoadUnsafe(ref at);
                Vector256<byte> delta = Avx2.MultiplyHigh(Avx2.Shuffle(argb, GreenHigh).AsInt16(), multsRb).AsByte();
                Vector256<byte> withGreen = Avx2.Add(argb, delta);
                Vector256<byte> delta2 = Avx2.MultiplyHigh(Avx2.Shuffle(withGreen, RedHigh).AsInt16(), multsB2).AsByte();
                Avx2.BlendVariable(Avx2.Add(withGreen, delta2), argb, maskAg).StoreUnsafe(ref at);
            }
        }

        Vector128<short> multsRb4 = Vector128.Create(PackMultiplier(toRed, toBlue)).AsInt16();
        Vector128<short> multsB24 = Vector128.Create(PackMultiplier(toBlue2, 0)).AsInt16();
        Vector128<byte> maskAg4 = Vector128.Create(0xff00ff00u).AsByte();
        for (; i + 4 <= pixels.Length; i += 4)
        {
            Inverse4(ref Unsafe.Add(ref p, i), multsRb4, multsB24, maskAg4);
        }

        return i;
    }

    // port: TransformColorInverse_SSE2
    private static void Inverse4(ref uint pixels, Vector128<short> multsRb, Vector128<short> multsB2, Vector128<byte> maskAg)
    {
        ref byte at = ref Unsafe.As<uint, byte>(ref pixels);
        Vector128<byte> input = Vector128.LoadUnsafe(ref at);
        Vector128<short> ag = Sse2.And(input, maskAg).AsInt16();
        Vector128<short> greens = Sse2.ShuffleHigh(Sse2.ShuffleLow(ag, ShuffleDuplicatePairs), ShuffleDuplicatePairs);
        Vector128<byte> withGreen = Sse2.Add(input, Sse2.MultiplyHigh(greens, multsRb).AsByte());
        Vector128<short> shifted = Sse2.ShiftLeftLogical(withGreen.AsInt16(), 8);
        Vector128<byte> placed = Sse2.ShiftRightLogical(Sse2.MultiplyHigh(shifted, multsB2).AsInt32(), 8).AsByte();
        Vector128<short> summed = Sse2.Add(placed, shifted.AsByte()).AsInt16();
        Sse2.Or(Sse2.ShiftRightLogical(summed, 8).AsByte(), ag.AsByte()).StoreUnsafe(ref at);
    }

    private static int TransformColorSimd(sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue, Span<uint> pixels)
    {
        if (!Sse2.IsSupported)
        {
            return 0;
        }

        short toRed = FixedMultiplier(greenToRed), toBlue = FixedMultiplier(greenToBlue), toBlue2 = FixedMultiplier(redToBlue);
        ref uint p = ref MemoryMarshal.GetReference(pixels);
        int i = 0;
        if (Avx2.IsSupported)
        {
            Vector256<short> multsRb = Vector256.Create(PackMultiplier(toRed, toBlue)).AsInt16();
            Vector256<short> multsB2 = Vector256.Create(PackMultiplier(toBlue2, 0)).AsInt16();
            Vector256<byte> maskRb = Vector256.Create(0x00ff00ffu).AsByte();
            for (; i + 8 <= pixels.Length; i += 8)
            {
                ref byte at = ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref p, i));
                Vector256<byte> input = Vector256.LoadUnsafe(ref at);
                Vector256<short> fromGreen = Avx2.MultiplyHigh(Avx2.Shuffle(input, GreenHigh).AsInt16(), multsRb);
                Vector256<short> shifted = Avx2.ShiftLeftLogical(input.AsInt16(), 8);
                Vector256<byte> fromRed = Avx2.ShiftRightLogical(Avx2.MultiplyHigh(shifted, multsB2).AsInt32(), 16).AsByte();
                Vector256<byte> delta = Avx2.And(Avx2.Add(fromRed, fromGreen.AsByte()), maskRb);
                Avx2.Subtract(input, delta).StoreUnsafe(ref at);
            }
        }

        Vector128<short> multsRb4 = Vector128.Create(PackMultiplier(toRed, toBlue)).AsInt16();
        Vector128<short> multsB24 = Vector128.Create(PackMultiplier(toBlue2, 0)).AsInt16();
        Vector128<byte> maskRb4 = Vector128.Create(0x00ff00ffu).AsByte();
        for (; i + 4 <= pixels.Length; i += 4)
        {
            Forward4(ref Unsafe.Add(ref p, i), multsRb4, multsB24, maskRb4);
        }

        return i;
    }

    // port: TransformColor_SSE2
    private static void Forward4(ref uint pixels, Vector128<short> multsRb, Vector128<short> multsB2, Vector128<byte> maskRb)
    {
        ref byte at = ref Unsafe.As<uint, byte>(ref pixels);
        Vector128<byte> input = Vector128.LoadUnsafe(ref at);
        Vector128<short> ag = Sse2.And(input, Vector128.Create(0xff00ff00u).AsByte()).AsInt16();
        Vector128<short> greens = Sse2.ShuffleHigh(Sse2.ShuffleLow(ag, ShuffleDuplicatePairs), ShuffleDuplicatePairs);
        Vector128<short> shifted = Sse2.ShiftLeftLogical(input.AsInt16(), 8);
        Vector128<byte> fromRed = Sse2.ShiftRightLogical(Sse2.MultiplyHigh(shifted, multsB2).AsInt32(), 16).AsByte();
        Vector128<byte> delta = Sse2.And(Sse2.Add(fromRed, Sse2.MultiplyHigh(greens, multsRb).AsByte()), maskRb);
        Sse2.Subtract(input, delta).StoreUnsafe(ref at);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Load8(ref uint pixels, int index) => Vector256.LoadUnsafe(ref Unsafe.Add(ref pixels, index)).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(ref uint pixels, int index, Vector256<byte> value) =>
        value.AsUInt32().StoreUnsafe(ref Unsafe.Add(ref pixels, index));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load4(ref uint pixels, int index) => Vector128.LoadUnsafe(ref Unsafe.Add(ref pixels, index)).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4(ref uint pixels, int index, Vector128<byte> value) =>
        value.AsUInt32().StoreUnsafe(ref Unsafe.Add(ref pixels, index));
}
