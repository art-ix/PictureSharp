using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal sealed partial class JpegEncoderComponent
{
    /// <summary>
    /// <see cref="LoadBlock"/> with 128-bit vectors: each row of eight samples is averaged in 16-bit lanes (pairs of
    /// bytes are summed through their 16-bit view) and widened to 32 bits; false leaves the block to the scalar code.
    /// </summary>
    internal static bool LoadBlockSimd(ReadOnlySpan<byte> source, int width, int dx, int dy, Span<int> samples)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return false;
        }

        ref byte s = ref MemoryMarshal.GetReference(source);
        ref int d = ref MemoryMarshal.GetReference(samples[..64]);
        Vector128<ushort> bias = dx == 2
            ? (dy == 2 ? Vector128.Create((ushort)1, 2, 1, 2, 1, 2, 1, 2) : Vector128.Create((ushort)0, 1, 0, 1, 0, 1, 0, 1))
            : Vector128.Create((ushort)(dy - 1));
        int shift = dx + dy - 2;
        for (int y = 0; y < 8; y++)
        {
            nuint row0 = (nuint)(y * dy * width);
            Vector128<ushort> sum = Row(ref Unsafe.Add(ref s, row0), dx);
            if (dy == 2)
            {
                sum += Row(ref Unsafe.Add(ref s, row0 + (nuint)width), dx);
            }

            Vector128<short> value = ((sum + bias) >>> shift).AsInt16() - Vector128.Create((short)128);
            (Vector128<int> lower, Vector128<int> upper) = Vector128.Widen(value);
            lower.StoreUnsafe(ref d, (nuint)(y * 8));
            upper.StoreUnsafe(ref d, (nuint)((y * 8) + 4));
        }

        return true;
    }

    // Eight samples of a row, or eight sums of horizontal pairs when dx is 2.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Row(ref byte source, int dx)
    {
        if (dx == 2)
        {
            Vector128<ushort> pairs = Vector128.LoadUnsafe(ref source).AsUInt16();
            return (pairs & Vector128.Create((ushort)0xFF)) + (pairs >>> 8);
        }

        return Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref source)).AsByte());
    }

    /// <summary>
    /// <see cref="ConvertColorsScalar"/> for eight pixels at a time in 32-bit lanes; groups with a transparent pixel
    /// go through the scalar code. Returns the number of pixels converted.
    /// </summary>
    private static int ConvertColorsSimd(ReadOnlySpan<Rgba32> pixels, Span<byte> y, Span<byte> cb, Span<byte> cr, Rgba32 background)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        ReadOnlySpan<uint> packed = MemoryMarshal.Cast<Rgba32, uint>(pixels);
        Vector128<uint> opaque = Vector128.Create(0xFF000000u);
        int i = 0;
        for (; i + 8 <= packed.Length; i += 8)
        {
            Vector128<uint> p0 = Vector128.Create(packed.Slice(i, 4));
            Vector128<uint> p1 = Vector128.Create(packed.Slice(i + 4, 4));
            if ((p0 & p1 & opaque) != opaque)
            {
                ConvertColorsScalar(pixels.Slice(i, 8), y.Slice(i, 8), cb.IsEmpty ? default : cb.Slice(i, 8), cr.IsEmpty ? default : cr.Slice(i, 8),
                    background);
                continue;
            }

            (Vector128<int> r0, Vector128<int> g0, Vector128<int> b0) = Channels(p0);
            (Vector128<int> r1, Vector128<int> g1, Vector128<int> b1) = Channels(p1);
            StoreBytes(y.Slice(i, 8), Luma(r0, g0, b0), Luma(r1, g1, b1));
            if (!cb.IsEmpty)
            {
                Vector128<int> offset = Vector128.Create((128 << 16) + 32767);
                StoreBytes(cb.Slice(i, 8), Chroma(-11059, -21709, 32768, r0, g0, b0, offset), Chroma(-11059, -21709, 32768, r1, g1, b1, offset));
                StoreBytes(cr.Slice(i, 8), Chroma(32768, -27439, -5329, r0, g0, b0, offset), Chroma(32768, -27439, -5329, r1, g1, b1, offset));
            }
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector128<int> R, Vector128<int> G, Vector128<int> B) Channels(Vector128<uint> pixels)
    {
        Vector128<uint> mask = Vector128.Create(0xFFu);
        return ((pixels & mask).AsInt32(), ((pixels >>> 8) & mask).AsInt32(), ((pixels >>> 16) & mask).AsInt32());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Luma(Vector128<int> r, Vector128<int> g, Vector128<int> b) =>
        ((r * 19595) + (g * 38470) + (b * 7471) + Vector128.Create(32768)) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Chroma(int kr, int kg, int kb, Vector128<int> r, Vector128<int> g, Vector128<int> b, Vector128<int> offset) =>
        ((r * kr) + (g * kg) + (b * kb) + offset) >> 16;

    // Eight values in 0–255 from two vectors of four.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBytes(Span<byte> destination, Vector128<int> lower, Vector128<int> upper)
    {
        Vector128<short> words = Vector128.Narrow(lower, upper);
        ulong bytes = Vector128.Narrow(words, words).AsUInt64().ToScalar();
        MemoryMarshal.Write(destination, in bytes);
    }
}
