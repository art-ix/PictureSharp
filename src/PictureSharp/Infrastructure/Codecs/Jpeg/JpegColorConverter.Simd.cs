using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal static partial class JpegColorConverter
{
    // The multipliers of build_ycc_rgb_table; the tables hold exactly these products.
    private static readonly int FixCrToR = Fix(1.40200);
    private static readonly int FixCbToB = Fix(1.77200);
    private static readonly int FixCrToG = Fix(0.71414);
    private static readonly int FixCbToG = Fix(0.34414);

    /// <summary>
    /// The interior of <see cref="H1V2"/> eight samples at a time in 16-bit lanes; returns the number of samples written.
    /// </summary>
    private static int H1V2Simd(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, int bias, Span<byte> dst)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        ref byte n = ref MemoryMarshal.GetReference(near);
        ref byte f = ref MemoryMarshal.GetReference(far);
        Vector128<ushort> b = Vector128.Create((ushort)bias);
        int x = 0;
        for (; x + 8 <= near.Length; x += 8)
        {
            Vector128<ushort> value = ((Load8(ref n, x) * 3) + Load8(ref f, x) + b) >>> 2;
            Store8(dst.Slice(x, 8), value);
        }

        return x;
    }

    /// <summary>
    /// Output pairs 1 … m of <see cref="H2V1"/> eight input samples at a time; returns m. Each 16-bit lane holds the
    /// even output in its low byte and the odd one in its high byte, so a little-endian store interleaves them.
    /// </summary>
    private static int H2V1Simd(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        ref byte s = ref MemoryMarshal.GetReference(src);
        int x = 1;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; x + 17 <= src.Length; x += 16)
            {
                Vector256<ushort> v = Load16(ref s, x) * 3;
                Vector256<ushort> even = (v + Load16(ref s, x - 1) + Vector256.Create((ushort)1)) >>> 2;
                Vector256<ushort> odd = (v + Load16(ref s, x + 1) + Vector256.Create((ushort)2)) >>> 2;
                (even | (odd << 8)).AsByte().StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)(2 * x));
            }
        }

        for (; x + 9 <= src.Length; x += 8)
        {
            Vector128<ushort> v = Load8(ref s, x) * 3;
            Vector128<ushort> even = (v + Load8(ref s, x - 1) + Vector128.Create((ushort)1)) >>> 2;
            Vector128<ushort> odd = (v + Load8(ref s, x + 1) + Vector128.Create((ushort)2)) >>> 2;
            (even | (odd << 8)).AsByte().StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)(2 * x));
        }

        return x - 1;
    }

    /// <summary>Output pairs 1 … m of <see cref="H2V2"/>, laid out like <see cref="H2V1Simd"/>; returns m.</summary>
    private static int H2V2Simd(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, Span<byte> dst)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        ref byte n = ref MemoryMarshal.GetReference(near);
        ref byte f = ref MemoryMarshal.GetReference(far);
        int x = 1;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; x + 17 <= near.Length; x += 16)
            {
                Vector256<ushort> thisSum = (Load16(ref n, x) * 3) + Load16(ref f, x);
                Vector256<ushort> lastSum = (Load16(ref n, x - 1) * 3) + Load16(ref f, x - 1);
                Vector256<ushort> nextSum = (Load16(ref n, x + 1) * 3) + Load16(ref f, x + 1);
                Vector256<ushort> even = ((thisSum * 3) + lastSum + Vector256.Create((ushort)8)) >>> 4;
                Vector256<ushort> odd = ((thisSum * 3) + nextSum + Vector256.Create((ushort)7)) >>> 4;
                (even | (odd << 8)).AsByte().StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)(2 * x));
            }
        }

        for (; x + 9 <= near.Length; x += 8)
        {
            Vector128<ushort> thisSum = (Load8(ref n, x) * 3) + Load8(ref f, x);
            Vector128<ushort> lastSum = (Load8(ref n, x - 1) * 3) + Load8(ref f, x - 1);
            Vector128<ushort> nextSum = (Load8(ref n, x + 1) * 3) + Load8(ref f, x + 1);
            Vector128<ushort> even = ((thisSum * 3) + lastSum + Vector128.Create((ushort)8)) >>> 4;
            Vector128<ushort> odd = ((thisSum * 3) + nextSum + Vector128.Create((ushort)7)) >>> 4;
            (even | (odd << 8)).AsByte().StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)(2 * x));
        }

        return x - 1;
    }

    /// <summary>
    /// <see cref="YccToRgb"/> for eight pixels at a time, computing the table entries in 32-bit lanes; returns the
    /// number of pixels converted.
    /// </summary>
    private static int YccToRgbSimd(ReadOnlySpan<byte> yRow, ReadOnlySpan<byte> cbRow, ReadOnlySpan<byte> crRow, Span<Rgba32> dst)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        ref byte yr = ref MemoryMarshal.GetReference(yRow);
        ref byte cbr = ref MemoryMarshal.GetReference(cbRow);
        ref byte crr = ref MemoryMarshal.GetReference(crRow);
        ref uint d = ref Unsafe.As<Rgba32, uint>(ref MemoryMarshal.GetReference(dst));
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<short> center256 = Vector256.Create((short)128);
            for (; x + 16 <= dst.Length; x += 16)
            {
                (Vector256<int> y0, Vector256<int> y1) = Vector256.Widen(Load16(ref yr, x).AsInt16());
                (Vector256<int> cb0, Vector256<int> cb1) = Vector256.Widen(Load16(ref cbr, x).AsInt16() - center256);
                (Vector256<int> cr0, Vector256<int> cr1) = Vector256.Widen(Load16(ref crr, x).AsInt16() - center256);
                YccToRgba(y0, cb0, cr0).StoreUnsafe(ref d, (nuint)x);
                YccToRgba(y1, cb1, cr1).StoreUnsafe(ref d, (nuint)(x + 8));
            }
        }

        Vector128<short> center = Vector128.Create((short)128);
        for (; x + 8 <= dst.Length; x += 8)
        {
            (Vector128<int> y0, Vector128<int> y1) = Vector128.Widen(Load8(ref yr, x).AsInt16());
            (Vector128<int> cb0, Vector128<int> cb1) = Vector128.Widen(Load8(ref cbr, x).AsInt16() - center);
            (Vector128<int> cr0, Vector128<int> cr1) = Vector128.Widen(Load8(ref crr, x).AsInt16() - center);
            YccToRgba(y0, cb0, cr0).StoreUnsafe(ref d, (nuint)x);
            YccToRgba(y1, cb1, cr1).StoreUnsafe(ref d, (nuint)(x + 4));
        }

        return x;
    }

    // port: libjpeg-turbo jdcolor.c ycc_rgb_convert with the table entries of build_ycc_rgb_table computed in place
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> YccToRgba(Vector128<int> y, Vector128<int> cb, Vector128<int> cr)
    {
        Vector128<int> half = Vector128.Create(OneHalf);
        Vector128<int> r = y + (((cr * FixCrToR) + half) >> ScaleBits);
        Vector128<int> g = y + (((cb * -FixCbToG) + half + (cr * -FixCrToG)) >> ScaleBits);
        Vector128<int> b = y + (((cb * FixCbToB) + half) >> ScaleBits);
        Vector128<int> max = Vector128.Create(255);
        r = Vector128.Min(Vector128.Max(r, Vector128<int>.Zero), max);
        g = Vector128.Min(Vector128.Max(g, Vector128<int>.Zero), max);
        b = Vector128.Min(Vector128.Max(b, Vector128<int>.Zero), max);
        return (r | (g << 8) | (b << 16)).AsUInt32() | Vector128.Create(0xFF000000u);
    }

    // The same computation on eight pixels.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> YccToRgba(Vector256<int> y, Vector256<int> cb, Vector256<int> cr)
    {
        Vector256<int> half = Vector256.Create(OneHalf);
        Vector256<int> r = y + (((cr * FixCrToR) + half) >> ScaleBits);
        Vector256<int> g = y + (((cb * -FixCbToG) + half + (cr * -FixCrToG)) >> ScaleBits);
        Vector256<int> b = y + (((cb * FixCbToB) + half) >> ScaleBits);
        Vector256<int> max = Vector256.Create(255);
        r = Vector256.Min(Vector256.Max(r, Vector256<int>.Zero), max);
        g = Vector256.Min(Vector256.Max(g, Vector256<int>.Zero), max);
        b = Vector256.Min(Vector256.Max(b, Vector256<int>.Zero), max);
        return (r | (g << 8) | (b << 16)).AsUInt32() | Vector256.Create(0xFF000000u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> Load16(ref byte source, int offset) =>
        Vector256.WidenLower(Vector128.LoadUnsafe(ref source, (nuint)offset).ToVector256Unsafe());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Load8(ref byte source, int offset) =>
        Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, offset))).AsByte());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(Span<byte> destination, Vector128<ushort> values)
    {
        ulong bytes = Vector128.Narrow(values, values).AsUInt64().ToScalar();
        MemoryMarshal.Write(destination, in bytes);
    }
}
