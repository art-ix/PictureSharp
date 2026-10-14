using System.Runtime.CompilerServices;

namespace RasterSharp.Formats.WebP.Lossy;

/// <summary>VP8 decoder DSP: inverse transforms, intra predictors and in-loop filters (scalar C versions).</summary>
// port: src/dsp/dec.c
internal static partial class Vp8Dsp
{
    /// <summary>Stride of the prediction work buffer.</summary>
    public const int Bps = 32;

    // ------------------------------------------------------------------ clipping helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Clip8b(int v) => (v & ~0xff) == 0 ? (byte)v : v < 0 ? (byte)0 : (byte)255;

    // VP8ksclip1: clips [-1020, 1020] to [-128, 127]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SClip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

    // VP8ksclip2: clips [-112, 112] to [-16, 15]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;

    // VP8kclip1: clips [-255, 511] to [0, 255]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip1(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    // VP8kabs0
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Abs0(int v) => v < 0 ? -v : v;

    // ------------------------------------------------------------------ transforms (Paragraph 14.4)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul1(int a) => ((a * 20091) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul2(int a) => (a * 35468) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(byte[] dst, int off, int x, int y, int v)
    {
        int i = off + x + (y * Bps);
        dst[i] = Clip8b(dst[i] + (v >> 3));
    }

    // port: TransformOne_C
    internal static void TransformOneScalar(ReadOnlySpan<short> input, byte[] dst, int off)
    {
        Span<int> c = stackalloc int[16];
        int tmp = 0;
        for (int i = 0; i < 4; ++i)
        {
            // vertical pass
            int a = input[i] + input[8 + i];
            int b = input[i] - input[8 + i];
            int cc = Mul2(input[4 + i]) - Mul1(input[12 + i]);
            int d = Mul1(input[4 + i]) + Mul2(input[12 + i]);
            c[tmp + 0] = a + d;
            c[tmp + 1] = b + cc;
            c[tmp + 2] = b - cc;
            c[tmp + 3] = a - d;
            tmp += 4;
        }

        for (int i = 0; i < 4; ++i)
        {
            // horizontal pass
            int dc = c[i] + 4;
            int a = dc + c[8 + i];
            int b = dc - c[8 + i];
            int cc = Mul2(c[4 + i]) - Mul1(c[12 + i]);
            int d = Mul1(c[4 + i]) + Mul2(c[12 + i]);
            Store(dst, off, 0, 0, a + d);
            Store(dst, off, 1, 0, b + cc);
            Store(dst, off, 2, 0, b - cc);
            Store(dst, off, 3, 0, a - d);
            off += Bps;
        }
    }

    // port: TransformAC3_C. Simplified transform when only in[0], in[1] and in[4] are non-zero.
    public static void TransformAC3(ReadOnlySpan<short> input, byte[] dst, int off)
    {
        int a = input[0] + 4;
        int c4 = Mul2(input[4]);
        int d4 = Mul1(input[4]);
        int c1 = Mul2(input[1]);
        int d1 = Mul1(input[1]);
        Store2(dst, off, 0, a + d4, d1, c1);
        Store2(dst, off, 1, a + c4, d1, c1);
        Store2(dst, off, 2, a - c4, d1, c1);
        Store2(dst, off, 3, a - d4, d1, c1);

        static void Store2(byte[] dst, int off, int y, int dc, int d, int c)
        {
            Store(dst, off, 0, y, dc + d);
            Store(dst, off, 1, y, dc + c);
            Store(dst, off, 2, y, dc - c);
            Store(dst, off, 3, y, dc - d);
        }
    }

    // port: TransformDC_C
    public static void TransformDC(ReadOnlySpan<short> input, byte[] dst, int off)
    {
        int dc = input[0] + 4;
        for (int j = 0; j < 4; ++j)
        {
            for (int i = 0; i < 4; ++i)
            {
                Store(dst, off, i, j, dc);
            }
        }
    }

    // port: TransformUV_C
    public static void TransformUV(ReadOnlySpan<short> input, byte[] dst, int off)
    {
        TransformOne(input, dst, off);
        TransformOne(input[16..], dst, off + 4);
        TransformOne(input[32..], dst, off + (4 * Bps));
        TransformOne(input[48..], dst, off + (4 * Bps) + 4);
    }

    // port: TransformDCUV_C
    public static void TransformDCUV(ReadOnlySpan<short> input, byte[] dst, int off)
    {
        if (input[0] != 0) TransformDC(input, dst, off);
        if (input[16] != 0) TransformDC(input[16..], dst, off + 4);
        if (input[32] != 0) TransformDC(input[32..], dst, off + (4 * Bps));
        if (input[48] != 0) TransformDC(input[48..], dst, off + (4 * Bps) + 4);
    }

    // port: TransformWHT_C (Paragraph 14.3)
    public static void TransformWHT(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; ++i)
        {
            int a0 = input[0 + i] + input[12 + i];
            int a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i];
            int a3 = input[0 + i] - input[12 + i];
            tmp[0 + i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }

        int o = 0;
        for (int i = 0; i < 4; ++i)
        {
            int dc = tmp[0 + (i * 4)] + 3;  // w/ rounder
            int a0 = dc + tmp[3 + (i * 4)];
            int a1 = tmp[1 + (i * 4)] + tmp[2 + (i * 4)];
            int a2 = tmp[1 + (i * 4)] - tmp[2 + (i * 4)];
            int a3 = dc - tmp[3 + (i * 4)];
            output[o + 0] = (short)((a0 + a1) >> 3);
            output[o + 16] = (short)((a3 + a2) >> 3);
            output[o + 32] = (short)((a0 - a1) >> 3);
            output[o + 48] = (short)((a3 - a2) >> 3);
            o += 64;
        }
    }

    // ------------------------------------------------------------------ intra predictions

    private static void TrueMotion(byte[] dst, int off, int size)
    {
        int top = off - Bps;
        int topLeft = dst[top - 1];
        for (int y = 0; y < size; ++y)
        {
            int left = dst[off - 1] - topLeft;
            for (int x = 0; x < size; ++x)
            {
                dst[off + x] = Clip1(dst[top + x] + left);
            }

            off += Bps;
        }
    }

    private static void Fill(byte[] dst, int off, int size, byte value)
    {
        for (int j = 0; j < size; ++j)
        {
            dst.AsSpan(off + (j * Bps), size).Fill(value);
        }
    }

    /// <summary>16x16 luma prediction; <paramref name="mode"/> includes the special DC modes 4–6.</summary>
    public static void PredLuma16(int mode, byte[] dst, int off)
    {
        switch (mode)
        {
            case 0: // DC
            {
                int dc = 16;
                for (int j = 0; j < 16; ++j)
                {
                    dc += dst[off - 1 + (j * Bps)] + dst[off + j - Bps];
                }

                Fill(dst, off, 16, (byte)(dc >> 5));
                break;
            }

            case 1: // TM
                TrueMotion(dst, off, 16);
                break;
            case 2: // VE
                for (int j = 0; j < 16; ++j)
                {
                    Array.Copy(dst, off - Bps, dst, off + (j * Bps), 16);
                }

                break;
            case 3: // HE
                for (int j = 0; j < 16; ++j)
                {
                    int row = off + (j * Bps);
                    dst.AsSpan(row, 16).Fill(dst[row - 1]);
                }

                break;
            case 4: // DC, no top
            {
                int dc = 8;
                for (int j = 0; j < 16; ++j)
                {
                    dc += dst[off - 1 + (j * Bps)];
                }

                Fill(dst, off, 16, (byte)(dc >> 4));
                break;
            }

            case 5: // DC, no left
            {
                int dc = 8;
                for (int i = 0; i < 16; ++i)
                {
                    dc += dst[off + i - Bps];
                }

                Fill(dst, off, 16, (byte)(dc >> 4));
                break;
            }

            default: // DC, no top and left
                Fill(dst, off, 16, 0x80);
                break;
        }
    }

    /// <summary>8x8 chroma prediction; <paramref name="mode"/> includes the special DC modes 4–6.</summary>
    public static void PredChroma8(int mode, byte[] dst, int off)
    {
        switch (mode)
        {
            case 0: // DC
            {
                int dc0 = 8;
                for (int i = 0; i < 8; ++i)
                {
                    dc0 += dst[off + i - Bps] + dst[off - 1 + (i * Bps)];
                }

                Fill(dst, off, 8, (byte)(dc0 >> 4));
                break;
            }

            case 1: // TM
                TrueMotion(dst, off, 8);
                break;
            case 2: // VE
                for (int j = 0; j < 8; ++j)
                {
                    Array.Copy(dst, off - Bps, dst, off + (j * Bps), 8);
                }

                break;
            case 3: // HE
                for (int j = 0; j < 8; ++j)
                {
                    int row = off + (j * Bps);
                    dst.AsSpan(row, 8).Fill(dst[row - 1]);
                }

                break;
            case 4: // DC, no top
            {
                int dc0 = 4;
                for (int i = 0; i < 8; ++i)
                {
                    dc0 += dst[off - 1 + (i * Bps)];
                }

                Fill(dst, off, 8, (byte)(dc0 >> 3));
                break;
            }

            case 5: // DC, no left
            {
                int dc0 = 4;
                for (int i = 0; i < 8; ++i)
                {
                    dc0 += dst[off + i - Bps];
                }

                Fill(dst, off, 8, (byte)(dc0 >> 3));
                break;
            }

            default:
                Fill(dst, off, 8, 0x80);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg3(int a, int b, int c) => (byte)((a + (2 * b) + c + 2) >> 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    /// <summary>4x4 luma prediction (modes B_DC_PRED .. B_HU_PRED).</summary>
    public static void PredLuma4(int mode, byte[] d, int o)
    {
        int top = o - Bps;
        switch (mode)
        {
            case 0: // DC4
            {
                uint dc = 4;
                for (int i = 0; i < 4; ++i)
                {
                    dc += (uint)(d[top + i] + d[o - 1 + (i * Bps)]);
                }

                dc >>= 3;
                for (int i = 0; i < 4; ++i)
                {
                    d.AsSpan(o + (i * Bps), 4).Fill((byte)dc);
                }

                break;
            }

            case 1: // TM4
                TrueMotion(d, o, 4);
                break;
            case 2: // VE4
            {
                byte v0 = Avg3(d[top - 1], d[top], d[top + 1]);
                byte v1 = Avg3(d[top], d[top + 1], d[top + 2]);
                byte v2 = Avg3(d[top + 1], d[top + 2], d[top + 3]);
                byte v3 = Avg3(d[top + 2], d[top + 3], d[top + 4]);
                for (int i = 0; i < 4; ++i)
                {
                    int r = o + (i * Bps);
                    d[r] = v0;
                    d[r + 1] = v1;
                    d[r + 2] = v2;
                    d[r + 3] = v3;
                }

                break;
            }

            case 3: // HE4
            {
                int a = d[o - 1 - Bps];
                int b = d[o - 1];
                int c = d[o - 1 + Bps];
                int dd = d[o - 1 + (2 * Bps)];
                int e = d[o - 1 + (3 * Bps)];
                d.AsSpan(o, 4).Fill(Avg3(a, b, c));
                d.AsSpan(o + Bps, 4).Fill(Avg3(b, c, dd));
                d.AsSpan(o + (2 * Bps), 4).Fill(Avg3(c, dd, e));
                d.AsSpan(o + (3 * Bps), 4).Fill(Avg3(dd, e, e));
                break;
            }

            case 4: // RD4
            {
                int i = d[o - 1], j = d[o - 1 + Bps], k = d[o - 1 + (2 * Bps)], l = d[o - 1 + (3 * Bps)];
                int x = d[top - 1], a = d[top], b = d[top + 1], c = d[top + 2], dd = d[top + 3];
                Set(d, o, 0, 3, Avg3(j, k, l));
                byte v = Avg3(i, j, k); Set(d, o, 1, 3, v); Set(d, o, 0, 2, v);
                v = Avg3(x, i, j); Set(d, o, 2, 3, v); Set(d, o, 1, 2, v); Set(d, o, 0, 1, v);
                v = Avg3(a, x, i); Set(d, o, 3, 3, v); Set(d, o, 2, 2, v); Set(d, o, 1, 1, v); Set(d, o, 0, 0, v);
                v = Avg3(b, a, x); Set(d, o, 3, 2, v); Set(d, o, 2, 1, v); Set(d, o, 1, 0, v);
                v = Avg3(c, b, a); Set(d, o, 3, 1, v); Set(d, o, 2, 0, v);
                Set(d, o, 3, 0, Avg3(dd, c, b));
                break;
            }

            case 5: // VR4
            {
                int i = d[o - 1], j = d[o - 1 + Bps], k = d[o - 1 + (2 * Bps)];
                int x = d[top - 1], a = d[top], b = d[top + 1], c = d[top + 2], dd = d[top + 3];
                byte v = Avg2(x, a); Set(d, o, 0, 0, v); Set(d, o, 1, 2, v);
                v = Avg2(a, b); Set(d, o, 1, 0, v); Set(d, o, 2, 2, v);
                v = Avg2(b, c); Set(d, o, 2, 0, v); Set(d, o, 3, 2, v);
                Set(d, o, 3, 0, Avg2(c, dd));
                Set(d, o, 0, 3, Avg3(k, j, i));
                Set(d, o, 0, 2, Avg3(j, i, x));
                v = Avg3(i, x, a); Set(d, o, 0, 1, v); Set(d, o, 1, 3, v);
                v = Avg3(x, a, b); Set(d, o, 1, 1, v); Set(d, o, 2, 3, v);
                v = Avg3(a, b, c); Set(d, o, 2, 1, v); Set(d, o, 3, 3, v);
                Set(d, o, 3, 1, Avg3(b, c, dd));
                break;
            }

            case 6: // LD4
            {
                int a = d[top], b = d[top + 1], c = d[top + 2], dd = d[top + 3];
                int e = d[top + 4], f = d[top + 5], g = d[top + 6], h = d[top + 7];
                Set(d, o, 0, 0, Avg3(a, b, c));
                byte v = Avg3(b, c, dd); Set(d, o, 1, 0, v); Set(d, o, 0, 1, v);
                v = Avg3(c, dd, e); Set(d, o, 2, 0, v); Set(d, o, 1, 1, v); Set(d, o, 0, 2, v);
                v = Avg3(dd, e, f); Set(d, o, 3, 0, v); Set(d, o, 2, 1, v); Set(d, o, 1, 2, v); Set(d, o, 0, 3, v);
                v = Avg3(e, f, g); Set(d, o, 3, 1, v); Set(d, o, 2, 2, v); Set(d, o, 1, 3, v);
                v = Avg3(f, g, h); Set(d, o, 3, 2, v); Set(d, o, 2, 3, v);
                Set(d, o, 3, 3, Avg3(g, h, h));
                break;
            }

            case 7: // VL4
            {
                int a = d[top], b = d[top + 1], c = d[top + 2], dd = d[top + 3];
                int e = d[top + 4], f = d[top + 5], g = d[top + 6], h = d[top + 7];
                Set(d, o, 0, 0, Avg2(a, b));
                byte v = Avg2(b, c); Set(d, o, 1, 0, v); Set(d, o, 0, 2, v);
                v = Avg2(c, dd); Set(d, o, 2, 0, v); Set(d, o, 1, 2, v);
                v = Avg2(dd, e); Set(d, o, 3, 0, v); Set(d, o, 2, 2, v);
                Set(d, o, 0, 1, Avg3(a, b, c));
                v = Avg3(b, c, dd); Set(d, o, 1, 1, v); Set(d, o, 0, 3, v);
                v = Avg3(c, dd, e); Set(d, o, 2, 1, v); Set(d, o, 1, 3, v);
                v = Avg3(dd, e, f); Set(d, o, 3, 1, v); Set(d, o, 2, 3, v);
                Set(d, o, 3, 2, Avg3(e, f, g));
                Set(d, o, 3, 3, Avg3(f, g, h));
                break;
            }

            case 8: // HD4
            {
                int i = d[o - 1], j = d[o - 1 + Bps], k = d[o - 1 + (2 * Bps)], l = d[o - 1 + (3 * Bps)];
                int x = d[top - 1], a = d[top], b = d[top + 1], c = d[top + 2];
                byte v = Avg2(i, x); Set(d, o, 0, 0, v); Set(d, o, 2, 1, v);
                v = Avg2(j, i); Set(d, o, 0, 1, v); Set(d, o, 2, 2, v);
                v = Avg2(k, j); Set(d, o, 0, 2, v); Set(d, o, 2, 3, v);
                Set(d, o, 0, 3, Avg2(l, k));
                Set(d, o, 3, 0, Avg3(a, b, c));
                Set(d, o, 2, 0, Avg3(x, a, b));
                v = Avg3(i, x, a); Set(d, o, 1, 0, v); Set(d, o, 3, 1, v);
                v = Avg3(j, i, x); Set(d, o, 1, 1, v); Set(d, o, 3, 2, v);
                v = Avg3(k, j, i); Set(d, o, 1, 2, v); Set(d, o, 3, 3, v);
                Set(d, o, 1, 3, Avg3(l, k, j));
                break;
            }

            default: // HU4
            {
                int i = d[o - 1], j = d[o - 1 + Bps], k = d[o - 1 + (2 * Bps)], l = d[o - 1 + (3 * Bps)];
                Set(d, o, 0, 0, Avg2(i, j));
                byte v = Avg2(j, k); Set(d, o, 2, 0, v); Set(d, o, 0, 1, v);
                v = Avg2(k, l); Set(d, o, 2, 1, v); Set(d, o, 0, 2, v);
                Set(d, o, 1, 0, Avg3(i, j, k));
                v = Avg3(j, k, l); Set(d, o, 3, 0, v); Set(d, o, 1, 1, v);
                v = Avg3(k, l, l); Set(d, o, 3, 1, v); Set(d, o, 1, 2, v);
                byte lv = (byte)l;
                Set(d, o, 3, 2, lv); Set(d, o, 2, 2, lv);
                Set(d, o, 0, 3, lv); Set(d, o, 1, 3, lv); Set(d, o, 2, 3, lv); Set(d, o, 3, 3, lv);
                break;
            }
        }

        static void Set(byte[] d, int o, int x, int y, byte v) => d[o + x + (y * Bps)] = v;
    }

    // ------------------------------------------------------------------ in-loop filters (Paragraph 15)

    // 4 pixels in, 2 pixels out
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter2(byte[] p, int o, int step)
    {
        int p1 = p[o - (2 * step)], p0 = p[o - step], q0 = p[o], q1 = p[o + step];
        int a = (3 * (q0 - p0)) + SClip1(p1 - q1);  // in [-893,892]
        int a1 = SClip2((a + 4) >> 3);              // in [-16,15]
        int a2 = SClip2((a + 3) >> 3);
        p[o - step] = Clip1(p0 + a2);
        p[o] = Clip1(q0 - a1);
    }

    // 4 pixels in, 4 pixels out
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter4(byte[] p, int o, int step)
    {
        int p1 = p[o - (2 * step)], p0 = p[o - step], q0 = p[o], q1 = p[o + step];
        int a = 3 * (q0 - p0);
        int a1 = SClip2((a + 4) >> 3);
        int a2 = SClip2((a + 3) >> 3);
        int a3 = (a1 + 1) >> 1;
        p[o - (2 * step)] = Clip1(p1 + a3);
        p[o - step] = Clip1(p0 + a2);
        p[o] = Clip1(q0 - a1);
        p[o + step] = Clip1(q1 - a3);
    }

    // 6 pixels in, 6 pixels out
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter6(byte[] p, int o, int step)
    {
        int p2 = p[o - (3 * step)], p1 = p[o - (2 * step)], p0 = p[o - step];
        int q0 = p[o], q1 = p[o + step], q2 = p[o + (2 * step)];
        int a = SClip1((3 * (q0 - p0)) + SClip1(p1 - q1));
        // a is in [-128,127], a1 in [-27,27], a2 in [-18,18] and a3 in [-9,9]
        int a1 = ((27 * a) + 63) >> 7;
        int a2 = ((18 * a) + 63) >> 7;
        int a3 = ((9 * a) + 63) >> 7;
        p[o - (3 * step)] = Clip1(p2 + a3);
        p[o - (2 * step)] = Clip1(p1 + a2);
        p[o - step] = Clip1(p0 + a1);
        p[o] = Clip1(q0 - a1);
        p[o + step] = Clip1(q1 - a2);
        p[o + (2 * step)] = Clip1(q2 - a3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Hev(byte[] p, int o, int step, int thresh)
    {
        int p1 = p[o - (2 * step)], p0 = p[o - step], q0 = p[o], q1 = p[o + step];
        return Abs0(p1 - p0) > thresh || Abs0(q1 - q0) > thresh;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsFilter(byte[] p, int o, int step, int t)
    {
        int p1 = p[o - (2 * step)], p0 = p[o - step], q0 = p[o], q1 = p[o + step];
        return ((4 * Abs0(p0 - q0)) + Abs0(p1 - q1)) <= t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsFilter2(byte[] p, int o, int step, int t, int it)
    {
        int p3 = p[o - (4 * step)], p2 = p[o - (3 * step)], p1 = p[o - (2 * step)];
        int p0 = p[o - step], q0 = p[o];
        int q1 = p[o + step], q2 = p[o + (2 * step)], q3 = p[o + (3 * step)];
        if (((4 * Abs0(p0 - q0)) + Abs0(p1 - q1)) > t)
        {
            return false;
        }

        return Abs0(p3 - p2) <= it && Abs0(p2 - p1) <= it &&
               Abs0(p1 - p0) <= it && Abs0(q3 - q2) <= it &&
               Abs0(q2 - q1) <= it && Abs0(q1 - q0) <= it;
    }

    // Simple in-loop filtering (Paragraph 15.2)
    internal static void SimpleVFilter16Scalar(byte[] p, int o, int stride, int thresh)
    {
        int thresh2 = (2 * thresh) + 1;
        for (int i = 0; i < 16; ++i)
        {
            if (NeedsFilter(p, o + i, stride, thresh2))
            {
                DoFilter2(p, o + i, stride);
            }
        }
    }

    internal static void SimpleHFilter16Scalar(byte[] p, int o, int stride, int thresh)
    {
        int thresh2 = (2 * thresh) + 1;
        for (int i = 0; i < 16; ++i)
        {
            if (NeedsFilter(p, o + (i * stride), 1, thresh2))
            {
                DoFilter2(p, o + (i * stride), 1);
            }
        }
    }

    internal static void SimpleVFilter16iScalar(byte[] p, int o, int stride, int thresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4 * stride;
            SimpleVFilter16Scalar(p, o, stride, thresh);
        }
    }

    internal static void SimpleHFilter16iScalar(byte[] p, int o, int stride, int thresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4;
            SimpleHFilter16Scalar(p, o, stride, thresh);
        }
    }

    // Complex in-loop filtering (Paragraph 15.3)
    private static void FilterLoop26(byte[] p, int o, int hstride, int vstride, int size, int thresh, int ithresh, int hevThresh)
    {
        int thresh2 = (2 * thresh) + 1;
        while (size-- > 0)
        {
            if (NeedsFilter2(p, o, hstride, thresh2, ithresh))
            {
                if (Hev(p, o, hstride, hevThresh))
                {
                    DoFilter2(p, o, hstride);
                }
                else
                {
                    DoFilter6(p, o, hstride);
                }
            }

            o += vstride;
        }
    }

    private static void FilterLoop24(byte[] p, int o, int hstride, int vstride, int size, int thresh, int ithresh, int hevThresh)
    {
        int thresh2 = (2 * thresh) + 1;
        while (size-- > 0)
        {
            if (NeedsFilter2(p, o, hstride, thresh2, ithresh))
            {
                if (Hev(p, o, hstride, hevThresh))
                {
                    DoFilter2(p, o, hstride);
                }
                else
                {
                    DoFilter4(p, o, hstride);
                }
            }

            o += vstride;
        }
    }

    // on macroblock edges
    internal static void VFilter16Scalar(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh) =>
        FilterLoop26(p, o, stride, 1, 16, thresh, ithresh, hevThresh);

    internal static void HFilter16Scalar(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh) =>
        FilterLoop26(p, o, 1, stride, 16, thresh, ithresh, hevThresh);

    // on three inner edges
    internal static void VFilter16iScalar(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4 * stride;
            FilterLoop24(p, o, stride, 1, 16, thresh, ithresh, hevThresh);
        }
    }

    internal static void HFilter16iScalar(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4;
            FilterLoop24(p, o, 1, stride, 16, thresh, ithresh, hevThresh);
        }
    }

    // 8-pixels wide variants, for chroma filtering
    internal static void VFilter8Scalar(byte[] u, int uo, byte[] v, int vo, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop26(u, uo, stride, 1, 8, thresh, ithresh, hevThresh);
        FilterLoop26(v, vo, stride, 1, 8, thresh, ithresh, hevThresh);
    }

    internal static void HFilter8Scalar(byte[] u, int uo, byte[] v, int vo, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop26(u, uo, 1, stride, 8, thresh, ithresh, hevThresh);
        FilterLoop26(v, vo, 1, stride, 8, thresh, ithresh, hevThresh);
    }

    internal static void VFilter8iScalar(byte[] u, int uo, byte[] v, int vo, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop24(u, uo + (4 * stride), stride, 1, 8, thresh, ithresh, hevThresh);
        FilterLoop24(v, vo + (4 * stride), stride, 1, 8, thresh, ithresh, hevThresh);
    }

    internal static void HFilter8iScalar(byte[] u, int uo, byte[] v, int vo, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop24(u, uo + 4, 1, stride, 8, thresh, ithresh, hevThresh);
        FilterLoop24(v, vo + 4, 1, stride, 8, thresh, ithresh, hevThresh);
    }
}
