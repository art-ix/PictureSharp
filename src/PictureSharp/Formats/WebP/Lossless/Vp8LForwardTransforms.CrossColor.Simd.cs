using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Internal;

namespace PictureSharp.Formats.WebP.Lossless;

// port: src/dsp/lossless_enc_sse2.c, src/dsp/lossless_enc_avx2.c CollectColorRedTransforms, CollectColorBlueTransforms
// AVX2 on x86; 128-bit code on SSE2 or ARM64 through the Simd helpers. Each routine returns how many pixels it consumed; the caller finishes the tail in scalar code.
internal static partial class Vp8LForwardTransforms
{
    // Green and red bytes in the high byte of the low and high words (perm of CollectColorBlueTransforms_AVX2).
    private static readonly Vector256<byte> GreenRedHigh = Vector256.Create(
        Vector128.Create((byte)0xFF, 1, 0xFF, 2, 0xFF, 5, 0xFF, 6, 0xFF, 9, 0xFF, 10, 0xFF, 13, 0xFF, 14),
        Vector128.Create((byte)0xFF, 1, 0xFF, 2, 0xFF, 5, 0xFF, 6, 0xFF, 9, 0xFF, 10, 0xFF, 13, 0xFF, 14));

    // port: src/dsp/lossless_enc_sse2.c CST_5b
    // Sign-extended multiplier pre-shifted by 5, for mulhi against a byte in the high half of a word.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Cst5b(int value) => (short)((short)(ushort)(value << 8) >> 5);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Cst16(int high, int low) => Vector128.Create((int)(((uint)high << 16) | ((uint)low & 0xffff))).AsInt16();

    private static int CollectColorRedTransformsSimd(ReadOnlySpan<uint> argb, int greenToRed, Span<uint> histo)
    {
        if (!Simd.IsSupported128) return 0;
        ref uint src = ref MemoryMarshal.GetReference(argb);
        ref uint counts = ref MemoryMarshal.GetReference(histo[..256]);
        int x = 0;
        if (Avx2.IsSupported)
        {
            // Red minus the delta lands in byte 2 of each pixel.
            Vector256<short> mult = Vector256.Create(Cst16(0, Cst5b(greenToRed)), Cst16(0, Cst5b(greenToRed)));
            Vector256<uint> maskGreen = Vector256.Create(0x0000ff00u);
            for (; x + 8 <= argb.Length; x += 8)
            {
                Vector256<uint> a = Vector256.LoadUnsafe(ref src, (nuint)x);
                Vector256<int> c = Avx2.MultiplyAddAdjacent((a & maskGreen).AsInt16(), mult);
                Vector256<uint> d = Avx2.Subtract(a.AsInt16(), c.AsInt16()).AsUInt32();
                Count8(ref counts, Avx2.ShiftRightLogical(d, 16) & Vector256.Create(0xffu));
            }
        }
        Vector128<short> multGreen = Cst16(0, Cst5b(greenToRed));
        Vector128<uint> maskG = Vector128.Create(0x00ff00u), mask = Vector128.Create(0xffu);
        for (; x + 4 <= argb.Length; x += 4)
        {
            Vector128<uint> input = Vector128.LoadUnsafe(ref src, (nuint)x);
            Vector128<short> c = Simd.MultiplyHigh((input & maskG).AsInt16(), multGreen);
            Vector128<uint> e = ((input >>> 16).AsByte() - c.AsByte()).AsUInt32() & mask;
            Count4(ref counts, e);
        }
        return x;
    }

    private static int CollectColorBlueTransformsSimd(ReadOnlySpan<uint> argb, int greenToBlue, int redToBlue, Span<uint> histo)
    {
        if (!Simd.IsSupported128) return 0;
        ref uint src = ref MemoryMarshal.GetReference(argb);
        ref uint counts = ref MemoryMarshal.GetReference(histo[..256]);
        int x = 0;
        if (Avx2.IsSupported)
        {
            // The red multiplier carries an extra 256 so that the high word also subtracts the signed red, whose
            // difference from the unsigned red is a multiple of 256; adding both words leaves blue minus the deltas.
            Vector128<short> half = Cst16(Cst5b(redToBlue) + 256, Cst5b(greenToBlue));
            Vector256<short> mult = Vector256.Create(half, half);
            for (; x + 8 <= argb.Length; x += 8)
            {
                Vector256<uint> a = Vector256.LoadUnsafe(ref src, (nuint)x);
                Vector256<short> c = Avx2.MultiplyHigh(Avx2.Shuffle(a.AsByte(), GreenRedHigh).AsInt16(), mult);
                Vector256<short> d = Avx2.Subtract(a.AsInt16(), c);
                Vector256<uint> e = Avx2.Add(Avx2.ShiftRightLogical(d.AsUInt32(), 16).AsInt16(), d).AsUInt32();
                Count8(ref counts, e & Vector256.Create(0xffu));
            }
        }
        Vector128<short> multRed = Cst16(Cst5b(redToBlue), 0), multGreen = Cst16(0, Cst5b(greenToBlue));
        Vector128<uint> maskG = Vector128.Create(0x00ff00u), maskB = Vector128.Create(0x0000ffu);
        for (; x + 4 <= argb.Length; x += 4)
        {
            Vector128<uint> input = Vector128.LoadUnsafe(ref src, (nuint)x);
            Vector128<short> c = Simd.MultiplyHigh((input.AsInt16() << 8), multRed);
            Vector128<short> d = Simd.MultiplyHigh((input & maskG).AsInt16(), multGreen);
            Vector128<byte> e = input.AsByte() - d.AsByte();
            Vector128<uint> g = (e - (c.AsUInt32() >>> 16).AsByte()).AsUInt32() & maskB;
            Count4(ref counts, g);
        }
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Count8(ref uint counts, Vector256<uint> indices)
    {
        Count4(ref counts, indices.GetLower());
        Count4(ref counts, indices.GetUpper());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Count4(ref uint counts, Vector128<uint> indices)
    {
        ulong low = indices.AsUInt64().ToScalar(), high = indices.AsUInt64().GetElement(1);
        Unsafe.Add(ref counts, (int)(uint)low)++;
        Unsafe.Add(ref counts, (int)(low >> 32))++;
        Unsafe.Add(ref counts, (int)(uint)high)++;
        Unsafe.Add(ref counts, (int)(high >> 32))++;
    }
}
