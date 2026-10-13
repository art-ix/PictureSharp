using System.Runtime.CompilerServices;
using WebpSharp.Formats.WebP.Lossy;

namespace WebpSharp.Formats.WebP.ColorConversion;

/// <summary>YUV 4:2:0 to RGBA conversion, bit-exact with libwebp.</summary>
// port: src/dsp/yuv.h, src/dsp/upsampling.c, src/dec/io_dec.c (EmitFancyRGB / EmitSampledRGB)
internal static class YuvToRgb
{
    private const int YuvFix2 = 6;                        // fixed-point precision for YUV->RGB
    private const int YuvMask2 = (256 << YuvFix2) - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip8(int v) => (v & ~YuvMask2) == 0 ? (byte)(v >> YuvFix2) : v < 0 ? (byte)0 : (byte)255;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte YuvToR(int y, int v) => Clip8(MultHi(y, 19077) + MultHi(v, 26149) - 14234);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte YuvToG(int y, int u, int v) => Clip8(MultHi(y, 19077) - MultHi(u, 6419) - MultHi(v, 13320) + 8708);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte YuvToB(int y, int u) => Clip8(MultHi(y, 19077) + MultHi(u, 33050) - 17685);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Rgba32 ToRgba(int y, int u, int v) => new(YuvToR(y, v), YuvToG(y, u, v), YuvToB(y, u), 255);

    /// <summary>Converts a decoded frame to opaque RGBA pixels (width × height, tightly packed).</summary>
    public static void Convert(Vp8Frame frame, Span<Rgba32> dst, bool fancyUpsampling)
    {
        if (fancyUpsampling)
        {
            ConvertFancy(frame, dst);
        }
        else
        {
            ConvertSampled(frame, dst);
        }
    }

    // port: EmitSampledRGB (point-sampled chroma)
    private static void ConvertSampled(Vp8Frame f, Span<Rgba32> dst)
    {
        int w = f.Width;
        for (int y = 0; y < f.Height; y++)
        {
            int yRow = y * f.YStride;
            int uvRow = (y >> 1) * f.UvStride;
            Span<Rgba32> row = dst.Slice(y * w, w);
            for (int x = 0; x < w; x++)
            {
                row[x] = ToRgba(f.Y[yRow + x], f.U[uvRow + (x >> 1)], f.V[uvRow + (x >> 1)]);
            }
        }
    }

    // port: EmitFancyRGB applied to the whole frame
    private static void ConvertFancy(Vp8Frame f, Span<Rgba32> dst)
    {
        int w = f.Width;
        int h = f.Height;

        // First line is special cased: the u/v samples are mirrored at the boundary.
        UpsampleLinePair(f, 0, -1, 0, 0, dst, -1);

        // Pairs of rows (2k-1, 2k) interpolate between chroma rows k-1 and k.
        int y = 1;
        for (; y + 1 < h; y += 2)
        {
            int uvTop = (y - 1) >> 1;
            UpsampleLinePair(f, y, y + 1, uvTop, uvTop + 1, dst, y + 1);
        }

        // Process the very last row of an even-sized picture.
        if ((h & 1) == 0)
        {
            int uvLast = (h - 1) >> 1;
            UpsampleLinePair(f, h - 1, -1, uvLast, uvLast, dst, -1);
        }
    }

    // port: UPSAMPLE_FUNC. Writes row 'topY' (and 'bottomY' when >= 0) using chroma rows
    // 'topUv' (above) and 'curUv' (below).
    private static void UpsampleLinePair(Vp8Frame f, int topY, int bottomY, int topUv, int curUv, Span<Rgba32> dst, int bottomDstRow)
    {
        int len = f.Width;
        byte[] yp = f.Y;
        byte[] up = f.U;
        byte[] vp = f.V;
        int topYOff = topY * f.YStride;
        int botYOff = bottomY * f.YStride;
        int topUvOff = topUv * f.UvStride;
        int curUvOff = curUv * f.UvStride;
        Span<Rgba32> topDst = dst.Slice(topY * len, len);
        Span<Rgba32> botDst = bottomY >= 0 ? dst.Slice(bottomDstRow * len, len) : default;
        bool hasBottom = bottomY >= 0;

        int lastPixelPair = (len - 1) >> 1;
        uint tlUv = LoadUv(up[topUvOff], vp[topUvOff]);   // top-left sample
        uint lUv = LoadUv(up[curUvOff], vp[curUvOff]);    // left sample
        {
            uint uv0 = ((3 * tlUv) + lUv + 0x00020002u) >> 2;
            topDst[0] = ToRgba(yp[topYOff], (int)(uv0 & 0xff), (int)(uv0 >> 16));
        }

        if (hasBottom)
        {
            uint uv0 = ((3 * lUv) + tlUv + 0x00020002u) >> 2;
            botDst[0] = ToRgba(yp[botYOff], (int)(uv0 & 0xff), (int)(uv0 >> 16));
        }

        for (int x = 1; x <= lastPixelPair; ++x)
        {
            uint tUv = LoadUv(up[topUvOff + x], vp[topUvOff + x]);  // top sample
            uint uv = LoadUv(up[curUvOff + x], vp[curUvOff + x]);   // sample
            // precompute invariant values associated with first and second diagonals
            uint avg = tlUv + tUv + lUv + uv + 0x00080008u;
            uint diag12 = (avg + (2 * (tUv + lUv))) >> 3;
            uint diag03 = (avg + (2 * (tlUv + uv))) >> 3;
            {
                uint uv0 = (diag12 + tlUv) >> 1;
                uint uv1 = (diag03 + tUv) >> 1;
                topDst[(2 * x) - 1] = ToRgba(yp[topYOff + (2 * x) - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16));
                topDst[2 * x] = ToRgba(yp[topYOff + (2 * x)], (int)(uv1 & 0xff), (int)(uv1 >> 16));
            }

            if (hasBottom)
            {
                uint uv0 = (diag03 + lUv) >> 1;
                uint uv1 = (diag12 + uv) >> 1;
                botDst[(2 * x) - 1] = ToRgba(yp[botYOff + (2 * x) - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16));
                botDst[2 * x] = ToRgba(yp[botYOff + (2 * x)], (int)(uv1 & 0xff), (int)(uv1 >> 16));
            }

            tlUv = tUv;
            lUv = uv;
        }

        if ((len & 1) == 0)
        {
            {
                uint uv0 = ((3 * tlUv) + lUv + 0x00020002u) >> 2;
                topDst[len - 1] = ToRgba(yp[topYOff + len - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16));
            }

            if (hasBottom)
            {
                uint uv0 = ((3 * lUv) + tlUv + 0x00020002u) >> 2;
                botDst[len - 1] = ToRgba(yp[botYOff + len - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16));
            }
        }

        static uint LoadUv(byte u, byte v) => u | ((uint)v << 16);
    }
}
