using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using RasterSharp.Formats.WebP.Lossy;

namespace RasterSharp.Formats.WebP.ColorConversion;

// SSE2 versions of the conversion; for byte inputs they give exactly the scalar results.
internal static partial class YuvToRgb
{
    // port: src/dsp/yuv_sse2.c ConvertYUV444ToRGB_SSE2. Inputs are shifted left by 8, so the high half of the
    // unsigned product is MultHi; the 16-bit sums wrap only where the final values still fit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertYuv(Vector128<ushort> y, Vector128<ushort> u, Vector128<ushort> v,
        out Vector128<short> r, out Vector128<short> g, out Vector128<short> b)
    {
        Vector128<ushort> y1 = Sse2.MultiplyHigh(y, Vector128.Create((ushort)19077));
        Vector128<ushort> r0 = Sse2.MultiplyHigh(v, Vector128.Create((ushort)26149));
        r = (y1 - Vector128.Create((ushort)14234) + r0).AsInt16() >> 6;
        Vector128<ushort> g0 = Sse2.MultiplyHigh(u, Vector128.Create((ushort)6419));
        Vector128<ushort> g1 = Sse2.MultiplyHigh(v, Vector128.Create((ushort)13320));
        g = (y1 + Vector128.Create((ushort)8708) - (g0 + g1)).AsInt16() >> 6;
        // B can exceed 32767: saturating unsigned arithmetic and a logical shift (negative results clip to 0 anyway).
        Vector128<ushort> b0 = Sse2.MultiplyHigh(u, Vector128.Create((ushort)33050));
        b = Vector128.SubtractSaturate(Vector128.AddSaturate(b0, y1), Vector128.Create((ushort)17685)).AsInt16();
        b = (b.AsUInt16() >> 6).AsInt16();
    }

    // Converts 16 pixels: y holds 16 luma bytes, u and v the matching chroma as two halves of 8 lanes each.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store16(Vector128<byte> y, Vector128<short> uLow, Vector128<short> uHigh, Vector128<short> vLow, Vector128<short> vHigh, Span<Rgba32> dst)
    {
        ConvertYuv(Sse2.UnpackLow(Vector128<byte>.Zero, y).AsUInt16(), (uLow << 8).AsUInt16(), (vLow << 8).AsUInt16(),
            out Vector128<short> r0, out Vector128<short> g0, out Vector128<short> b0);
        ConvertYuv(Sse2.UnpackHigh(Vector128<byte>.Zero, y).AsUInt16(), (uHigh << 8).AsUInt16(), (vHigh << 8).AsUInt16(),
            out Vector128<short> r1, out Vector128<short> g1, out Vector128<short> b1);
        Vector128<byte> r = Sse2.PackUnsignedSaturate(r0, r1), g = Sse2.PackUnsignedSaturate(g0, g1), b = Sse2.PackUnsignedSaturate(b0, b1);
        Vector128<ushort> rgLow = Sse2.UnpackLow(r, g).AsUInt16(), rgHigh = Sse2.UnpackHigh(r, g).AsUInt16();
        Vector128<ushort> baLow = Sse2.UnpackLow(b, Vector128<byte>.AllBitsSet).AsUInt16();
        Vector128<ushort> baHigh = Sse2.UnpackHigh(b, Vector128<byte>.AllBitsSet).AsUInt16();
        Span<byte> bytes = MemoryMarshal.AsBytes(dst[..16]);
        Sse2.UnpackLow(rgLow, baLow).AsByte().CopyTo(bytes);
        Sse2.UnpackHigh(rgLow, baLow).AsByte().CopyTo(bytes[16..]);
        Sse2.UnpackLow(rgHigh, baHigh).AsByte().CopyTo(bytes[32..]);
        Sse2.UnpackHigh(rgHigh, baHigh).AsByte().CopyTo(bytes[48..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadBytes8(byte[] source, int offset) =>
        Vector128.WidenLower(Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(offset, 8))).AsByte()).AsInt16();

    // port: src/dsp/upsampling.c UPSAMPLE_FUNC body for chroma columns x..x+7 of one plane (same rounding steps).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Upsample(Vector128<short> topLeft, Vector128<short> top, Vector128<short> left, Vector128<short> current,
        out Vector128<short> topOdd, out Vector128<short> topEven, out Vector128<short> bottomOdd, out Vector128<short> bottomEven)
    {
        Vector128<short> average = topLeft + top + left + current + Vector128.Create((short)8);
        Vector128<short> diag12 = (average + ((top + left) << 1)) >> 3;
        Vector128<short> diag03 = (average + ((topLeft + current) << 1)) >> 3;
        topOdd = (diag12 + topLeft) >> 1;
        topEven = (diag03 + top) >> 1;
        bottomOdd = (diag03 + left) >> 1;
        bottomEven = (diag12 + current) >> 1;
    }

    /// <summary>Upsamples chroma columns 1.. in blocks of 8 (pixels 1.. in blocks of 16); returns the next column.</summary>
    private static int UpsampleSse2(Vp8Frame f, int topYOff, int botYOff, int topUvOff, int curUvOff,
        Span<Rgba32> topDst, Span<Rgba32> botDst, int lastPixelPair)
    {
        bool hasBottom = !botDst.IsEmpty;
        int x = 1;
        for (; x + 7 <= lastPixelPair; x += 8)
        {
            Upsample(LoadBytes8(f.U, topUvOff + x - 1), LoadBytes8(f.U, topUvOff + x), LoadBytes8(f.U, curUvOff + x - 1), LoadBytes8(f.U, curUvOff + x),
                out Vector128<short> uTopOdd, out Vector128<short> uTopEven, out Vector128<short> uBottomOdd, out Vector128<short> uBottomEven);
            Upsample(LoadBytes8(f.V, topUvOff + x - 1), LoadBytes8(f.V, topUvOff + x), LoadBytes8(f.V, curUvOff + x - 1), LoadBytes8(f.V, curUvOff + x),
                out Vector128<short> vTopOdd, out Vector128<short> vTopEven, out Vector128<short> vBottomOdd, out Vector128<short> vBottomEven);
            int pixel = (2 * x) - 1;
            Store16(Vector128.Create(f.Y.AsSpan(topYOff + pixel, 16)),
                Sse2.UnpackLow(uTopOdd, uTopEven), Sse2.UnpackHigh(uTopOdd, uTopEven),
                Sse2.UnpackLow(vTopOdd, vTopEven), Sse2.UnpackHigh(vTopOdd, vTopEven), topDst[pixel..]);
            if (hasBottom)
            {
                Store16(Vector128.Create(f.Y.AsSpan(botYOff + pixel, 16)),
                    Sse2.UnpackLow(uBottomOdd, uBottomEven), Sse2.UnpackHigh(uBottomOdd, uBottomEven),
                    Sse2.UnpackLow(vBottomOdd, vBottomEven), Sse2.UnpackHigh(vBottomOdd, vBottomEven), botDst[pixel..]);
            }
        }

        return x;
    }

    /// <summary>Converts the point-sampled row in blocks of 16 pixels; returns the next pixel.</summary>
    private static int ConvertSampledSse2(Vp8Frame f, int yRow, int uvRow, Span<Rgba32> row)
    {
        int x = 0;
        for (; x + 16 <= row.Length; x += 16)
        {
            Vector128<short> u = LoadBytes8(f.U, uvRow + (x >> 1)), v = LoadBytes8(f.V, uvRow + (x >> 1));
            Store16(Vector128.Create(f.Y.AsSpan(yRow + x, 16)), Sse2.UnpackLow(u, u), Sse2.UnpackHigh(u, u), Sse2.UnpackLow(v, v), Sse2.UnpackHigh(v, v), row[x..]);
        }

        return x;
    }
}
