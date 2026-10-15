using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Formats.WebP.Lossy;
using PictureSharp.Internal;

namespace PictureSharp.Formats.WebP.ColorConversion;

/// <summary>YUV 4:2:0 to RGBA conversion, bit-exact with libwebp.</summary>
// port: src/dsp/yuv.h, src/dsp/upsampling.c, src/dec/io_dec.c (EmitFancyRGB / EmitSampledRGB)
internal static partial class YuvToRgb
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
    public static void Convert(Vp8Frame frame, Rgba32[] dst, bool fancyUpsampling, int maxDegreeOfParallelism = 1, bool allowSimd = true,
        CancellationToken cancellationToken = default)
    {
        // Fancy upsampling works on row 0, then row pairs (2k-1, 2k), then a lone last row; sampling on rows.
        // Units only read the frame, so bands of units convert independently.
        int units = fancyUpsampling ? (frame.Height >> 1) + 1 : frame.Height;
        int bands = Parallelism.IsWorthwhile(maxDegreeOfParallelism, (long)frame.Width * frame.Height, 1 << 18)
            ? Math.Min(Environment.ProcessorCount * 4, Math.Max(1, units / 8)) : 1;
        if (bands == 1)
        {
            ConvertUnits(frame, dst, fancyUpsampling, 0, units, allowSimd);
            return;
        }

        Parallel.For(0, bands, Parallelism.Options(maxDegreeOfParallelism, cancellationToken),
            band => ConvertUnits(frame, dst, fancyUpsampling, (int)((long)units * band / bands), (int)((long)units * (band + 1) / bands), allowSimd));
    }

    private static void ConvertUnits(Vp8Frame frame, Span<Rgba32> dst, bool fancyUpsampling, int start, int end, bool allowSimd)
    {
        bool simd = allowSimd && Simd.IsSupported128;
        if (fancyUpsampling)
        {
            ConvertFancy(frame, dst, start, end, simd);
        }
        else
        {
            ConvertSampled(frame, dst, start, end, simd);
        }
    }

    // port: EmitSampledRGB (point-sampled chroma)
    private static void ConvertSampled(Vp8Frame f, Span<Rgba32> dst, int start, int end, bool simd)
    {
        int w = f.Width;
        for (int y = start; y < end; y++)
        {
            int yRow = y * f.YStride;
            int uvRow = (y >> 1) * f.UvStride;
            Span<Rgba32> row = dst.Slice(y * w, w);
            int x = simd ? ConvertSampledSse2(f, yRow, uvRow, row) : 0;
            for (; x < w; x++)
            {
                row[x] = ToRgba(f.Y[yRow + x], f.U[uvRow + (x >> 1)], f.V[uvRow + (x >> 1)]);
            }
        }
    }

    // port: EmitFancyRGB applied to units [start, end): unit 0 is the first row, unit k the row pair (2k-1, 2k),
    // or the last row of an even-sized picture.
    private static void ConvertFancy(Vp8Frame f, Span<Rgba32> dst, int start, int end, bool simd)
    {
        int h = f.Height;
        for (int k = start; k < end; k++)
        {
            if (k == 0)
            {
                // The first line is special cased: the u/v samples are mirrored at the boundary.
                UpsampleLinePair(f, 0, -1, 0, 0, dst, -1, simd);
                continue;
            }

            int y = (2 * k) - 1;
            if (y + 1 < h)
            {
                // Rows (2k-1, 2k) interpolate between chroma rows k-1 and k.
                UpsampleLinePair(f, y, y + 1, k - 1, k, dst, y + 1, simd);
            }
            else
            {
                // The very last row of an even-sized picture.
                UpsampleLinePair(f, h - 1, -1, k - 1, k - 1, dst, -1, simd);
            }
        }
    }

    // port: UPSAMPLE_FUNC. Writes row 'topY' (and 'bottomY' when >= 0) using chroma rows
    // 'topUv' (above) and 'curUv' (below).
    private static void UpsampleLinePair(Vp8Frame f, int topY, int bottomY, int topUv, int curUv, Span<Rgba32> dst, int bottomDstRow, bool simd)
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

        int x = 1;
        if (simd && lastPixelPair >= 8)
        {
            x = UpsampleSse2(f, topYOff, botYOff, topUvOff, curUvOff, topDst, botDst, lastPixelPair);
            tlUv = LoadUv(up[topUvOff + x - 1], vp[topUvOff + x - 1]);
            lUv = LoadUv(up[curUvOff + x - 1], vp[curUvOff + x - 1]);
        }

        for (; x <= lastPixelPair; ++x)
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
