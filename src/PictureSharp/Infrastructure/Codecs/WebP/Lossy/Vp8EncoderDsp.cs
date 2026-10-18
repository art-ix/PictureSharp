using System.Runtime.CompilerServices;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal static partial class Vp8EncoderDsp
{
    public const int Bps = 32;
    public static ReadOnlySpan<int> Scan => [0, 4, 8, 12, 128, 132, 136, 140, 256, 260, 264, 268, 384, 388, 392, 396, 0, 4, 128, 132, 8, 12, 136, 140];
    public static ReadOnlySpan<int> Pred16Offsets => [0, 16, 512, 528];
    public static ReadOnlySpan<int> Pred8Offsets => [1024, 1040, 1280, 1296];
    public static ReadOnlySpan<int> Pred4Offsets => [1536, 1540, 1544, 1548, 1552, 1556, 1560, 1564, 1664, 1668];

    // port: src/dsp/enc.c FTransform_C
    internal static void FTransformScalar(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prediction, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int o = i * Bps, t = i * 4;
            int d0 = source[o] - prediction[o], d1 = source[o + 1] - prediction[o + 1];
            int d2 = source[o + 2] - prediction[o + 2], d3 = source[o + 3] - prediction[o + 3];
            int a0 = d0 + d3, a1 = d1 + d2, a2 = d1 - d2, a3 = d0 - d3;
            tmp[t] = (a0 + a1) * 8;
            tmp[t + 1] = (a2 * 2217 + a3 * 5352 + 1812) >> 9;
            tmp[t + 2] = (a0 - a1) * 8;
            tmp[t + 3] = (a3 * 2217 - a2 * 5352 + 937) >> 9;
        }
        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[i] + tmp[12 + i], a1 = tmp[4 + i] + tmp[8 + i];
            int a2 = tmp[4 + i] - tmp[8 + i], a3 = tmp[i] - tmp[12 + i];
            output[i] = (short)((a0 + a1 + 7) >> 4);
            output[4 + i] = (short)(((a2 * 2217 + a3 * 5352 + 12000) >> 16) + (a3 != 0 ? 1 : 0));
            output[8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[12 + i] = (short)((a3 * 2217 - a2 * 5352 + 51000) >> 16);
        }
    }

    // port: src/dsp/enc.c FTransformWHT_C
    public static void FTransformWht(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int o = i * 64, t = i * 4;
            int a0 = input[o] + input[o + 32], a1 = input[o + 16] + input[o + 48];
            int a2 = input[o + 16] - input[o + 48], a3 = input[o] - input[o + 32];
            tmp[t] = a0 + a1; tmp[t + 1] = a3 + a2; tmp[t + 2] = a3 - a2; tmp[t + 3] = a0 - a1;
        }
        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[i] + tmp[8 + i], a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i], a3 = tmp[i] - tmp[8 + i];
            output[i] = (short)((a0 + a1) >> 1); output[4 + i] = (short)((a3 + a2) >> 1);
            output[8 + i] = (short)((a3 - a2) >> 1); output[12 + i] = (short)((a0 - a1) >> 1);
        }
    }

    // port: src/dsp/enc.c ITransformOne
    internal static void ITransformScalar(ReadOnlySpan<byte> prediction, ReadOnlySpan<short> input, byte[] output, int offset)
    {
        Copy(prediction, output.AsSpan(offset), 4, 4);
        Vp8Dsp.TransformOneScalar(input, output, offset);
    }

    // port: src/dsp/enc.c Copy
    public static void Copy(ReadOnlySpan<byte> source, Span<byte> output, int width, int height)
    {
        for (int y = 0; y < height; y++) source.Slice(y * Bps, width).CopyTo(output.Slice(y * Bps, width));
    }

    // port: src/dsp/enc.c GetSSE
    internal static int SseScalar(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height)
    {
        int sum = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) { int d = a[y * Bps + x] - b[y * Bps + x]; sum += d * d; }
        return sum;
    }

    // port: src/dsp/enc.c TTransform
    private static int TTransform(ReadOnlySpan<byte> input, ReadOnlySpan<ushort> weights)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int o = i * Bps, t = i * 4;
            int a0 = input[o] + input[o + 2], a1 = input[o + 1] + input[o + 3];
            int a2 = input[o + 1] - input[o + 3], a3 = input[o] - input[o + 2];
            tmp[t] = a0 + a1; tmp[t + 1] = a3 + a2; tmp[t + 2] = a3 - a2; tmp[t + 3] = a0 - a1;
        }
        int sum = 0;
        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[i] + tmp[8 + i], a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i], a3 = tmp[i] - tmp[8 + i];
            sum += weights[i] * Math.Abs(a0 + a1) + weights[4 + i] * Math.Abs(a3 + a2)
                 + weights[8 + i] * Math.Abs(a3 - a2) + weights[12 + i] * Math.Abs(a0 - a1);
        }
        return sum;
    }

    // port: src/dsp/enc.c Disto4x4_C, Disto16x16_C
    internal static int DistoScalar(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<ushort> weights, int size)
    {
        int sum = 0;
        for (int y = 0; y < size; y += 4)
            for (int x = 0; x < size; x += 4)
            {
                int o = y * Bps + x;
                sum += Math.Abs(TTransform(a[o..], weights) - TTransform(b[o..], weights)) >> 5;
            }
        return sum;
    }

    // port: src/dsp/enc.c CollectHistogram_C, VP8SetHistogramData
    internal static (int MaxValue, int LastNonZero) CollectHistogramScalar(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prediction, int start, int end)
    {
        Span<int> distribution = stackalloc int[32]; distribution.Clear();
        Span<short> coefficients = stackalloc short[16];
        for (int b = start; b < end; b++)
        {
            FTransformScalar(source[Scan[b]..], prediction[Scan[b]..], coefficients);
            foreach (short c in coefficients) distribution[Math.Min(Math.Abs((int)c) >> 3, 31)]++;
        }
        return HistogramStats(distribution);
    }

    private static (int MaxValue, int LastNonZero) HistogramStats(ReadOnlySpan<int> distribution)
    {
        int max = 0, last = 1;
        for (int i = 0; i < distribution.Length; i++)
            if (distribution[i] > 0) { max = Math.Max(max, distribution[i]); last = i; }
        return (max, last);
    }

    // port: src/dsp/enc.c QuantizeBlock_C
    internal static bool QuantizeBlockScalar(Span<short> input, Span<short> output, Vp8Matrix matrix)
    {
        bool nonzero = false;
        for (int n = 0; n < 16; n++)
        {
            int j = Vp8Tables.Zigzag[n];
            bool sign = input[j] < 0;
            uint coefficient = (uint)(Math.Abs((int)input[j]) + matrix.Sharpen[j]);
            if (coefficient > matrix.ZeroThreshold[j])
            {
                int level = Math.Min(2047, (int)((coefficient * matrix.Iq[j] + matrix.Bias[j]) >> 17));
                if (sign) level = -level;
                input[j] = (short)(level * matrix.Q[j]); output[n] = (short)level;
                nonzero |= level != 0;
            }
            else { input[j] = 0; output[n] = 0; }
        }
        return nonzero;
    }

    // port: src/dsp/enc.c DCMode, VerticalPred, HorizontalPred, TrueMotion
    private static void Predict(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int size, int mode)
    {
        switch (mode)
        {
            case 0:
                int dc = 128;
                if (!left.IsEmpty || !top.IsEmpty)
                {
                    int sum = 0, count = 0;
                    if (!left.IsEmpty) { for (int i = 1; i <= size; i++) sum += left[i]; count += size; }
                    if (!top.IsEmpty) { for (int i = 0; i < size; i++) sum += top[i]; count += size; }
                    dc = (sum + count / 2) / count;
                }
                Fill(output, size, (byte)dc);
                break;
            case 2:
                VerticalPred(output, top, size);
                break;
            case 3:
                HorizontalPred(output, left, size);
                break;
            default:
                if (left.IsEmpty) VerticalPred(output, top.IsEmpty ? default : top, size, 129);
                else if (top.IsEmpty) HorizontalPred(output, left, size);
                else TrueMotionScalar(output, left, top, size);
                break;
        }
    }

    private static void Fill(Span<byte> output, int size, byte value)
    {
        for (int y = 0; y < size; y++) output.Slice(y * Bps, size).Fill(value);
    }

    private static void VerticalPred(Span<byte> output, ReadOnlySpan<byte> top, int size, byte fallback = 127)
    {
        if (top.IsEmpty) { Fill(output, size, fallback); return; }
        top = top[..size];
        for (int y = 0; y < size; y++) top.CopyTo(output.Slice(y * Bps, size));
    }

    private static void HorizontalPred(Span<byte> output, ReadOnlySpan<byte> left, int size)
    {
        if (left.IsEmpty) { Fill(output, size, 129); return; }
        for (int y = 0; y < size; y++) output.Slice(y * Bps, size).Fill(left[y + 1]);
    }

    private static void TrueMotionScalar(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int size)
    {
        for (int y = 0; y < size; y++)
        {
            int delta = left[y + 1] - left[0];
            Span<byte> row = output.Slice(y * Bps, size);
            for (int x = 0; x < size; x++) row[x] = (byte)Math.Clamp(top[x] + delta, 0, 255);
        }
    }

    // port: src/dsp/enc.c Intra16Preds_C
    internal static void PredLuma16Scalar(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        for (int mode = 0; mode < 4; mode++) Predict(output[Pred16Offsets[mode]..], left, top, 16, mode);
    }

    // port: src/dsp/enc.c IntraChromaPreds_C
    internal static void PredChroma8Scalar(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        for (int mode = 0; mode < 4; mode++)
        {
            Predict(output[Pred8Offsets[mode]..], left, top, 8, mode);
            Predict(output[(Pred8Offsets[mode] + 8)..], left.IsEmpty ? left : left[16..], top.IsEmpty ? top : top[8..], 8, mode);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    // port: src/dsp/enc.c Intra4Preds_C
    internal static void PredLuma4Scalar(Span<byte> output, ReadOnlySpan<byte> boundary)
    {
        DC4(output[1536..], boundary);
        TM4(output[1540..], boundary);
        VE4(output[1544..], boundary);
        HE4(output[1548..], boundary);
        RD4(output[1552..], boundary);
        VR4(output[1556..], boundary);
        LD4(output[1560..], boundary);
        VL4(output[1564..], boundary);
        HD4(output[1664..], boundary);
        HU4(output[1668..], boundary);
    }

    // port: src/dsp/enc.c RD4
    private static void RD4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        int X = top[4];
        int I = top[3];
        int J = top[2];
        int K = top[1];
        int L = top[0];
        int A = top[5];
        int B = top[6];
        int C = top[7];
        int D = top[8];
        dst[0 + 3 * Bps] = Avg3(J, K, L);
        dst[0 + 2 * Bps] = dst[1 + 3 * Bps] = Avg3(I, J, K);
        dst[0 + 1 * Bps] = dst[1 + 2 * Bps] = dst[2 + 3 * Bps] = Avg3(X, I, J);
        dst[0 + 0 * Bps] = dst[1 + 1 * Bps] = dst[2 + 2 * Bps] = dst[3 + 3 * Bps] = Avg3(A, X, I);
        dst[1 + 0 * Bps] = dst[2 + 1 * Bps] = dst[3 + 2 * Bps] = Avg3(B, A, X);
        dst[2 + 0 * Bps] = dst[3 + 1 * Bps] = Avg3(C, B, A);
        dst[3 + 0 * Bps] = Avg3(D, C, B);
    }

    // port: src/dsp/enc.c LD4
    private static void LD4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        int A = top[5];
        int B = top[6];
        int C = top[7];
        int D = top[8];
        int E = top[9];
        int F = top[10];
        int G = top[11];
        int H = top[12];
        dst[0 + 0 * Bps] = Avg3(A, B, C);
        dst[1 + 0 * Bps] = dst[0 + 1 * Bps] = Avg3(B, C, D);
        dst[2 + 0 * Bps] = dst[1 + 1 * Bps] = dst[0 + 2 * Bps] = Avg3(C, D, E);
        dst[3 + 0 * Bps] = dst[2 + 1 * Bps] = dst[1 + 2 * Bps] = dst[0 + 3 * Bps] = Avg3(D, E, F);
        dst[3 + 1 * Bps] = dst[2 + 2 * Bps] = dst[1 + 3 * Bps] = Avg3(E, F, G);
        dst[3 + 2 * Bps] = dst[2 + 3 * Bps] = Avg3(F, G, H);
        dst[3 + 3 * Bps] = Avg3(G, H, H);
    }

    // port: src/dsp/enc.c VR4
    private static void VR4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        int X = top[4];
        int I = top[3];
        int J = top[2];
        int K = top[1];
        int A = top[5];
        int B = top[6];
        int C = top[7];
        int D = top[8];
        dst[0 + 0 * Bps] = dst[1 + 2 * Bps] = Avg2(X, A);
        dst[1 + 0 * Bps] = dst[2 + 2 * Bps] = Avg2(A, B);
        dst[2 + 0 * Bps] = dst[3 + 2 * Bps] = Avg2(B, C);
        dst[3 + 0 * Bps] = Avg2(C, D);

        dst[0 + 3 * Bps] = Avg3(K, J, I);
        dst[0 + 2 * Bps] = Avg3(J, I, X);
        dst[0 + 1 * Bps] = dst[1 + 3 * Bps] = Avg3(I, X, A);
        dst[1 + 1 * Bps] = dst[2 + 3 * Bps] = Avg3(X, A, B);
        dst[2 + 1 * Bps] = dst[3 + 3 * Bps] = Avg3(A, B, C);
        dst[3 + 1 * Bps] = Avg3(B, C, D);
    }

    // port: src/dsp/enc.c VL4
    private static void VL4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        int A = top[5];
        int B = top[6];
        int C = top[7];
        int D = top[8];
        int E = top[9];
        int F = top[10];
        int G = top[11];
        int H = top[12];
        dst[0 + 0 * Bps] = Avg2(A, B);
        dst[1 + 0 * Bps] = dst[0 + 2 * Bps] = Avg2(B, C);
        dst[2 + 0 * Bps] = dst[1 + 2 * Bps] = Avg2(C, D);
        dst[3 + 0 * Bps] = dst[2 + 2 * Bps] = Avg2(D, E);

        dst[0 + 1 * Bps] = Avg3(A, B, C);
        dst[1 + 1 * Bps] = dst[0 + 3 * Bps] = Avg3(B, C, D);
        dst[2 + 1 * Bps] = dst[1 + 3 * Bps] = Avg3(C, D, E);
        dst[3 + 1 * Bps] = dst[2 + 3 * Bps] = Avg3(D, E, F);
        dst[3 + 2 * Bps] = Avg3(E, F, G);
        dst[3 + 3 * Bps] = Avg3(F, G, H);
    }

    // port: src/dsp/enc.c HU4
    private static void HU4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        int I = top[3];
        int J = top[2];
        int K = top[1];
        int L = top[0];
        dst[0 + 0 * Bps] = Avg2(I, J);
        dst[2 + 0 * Bps] = dst[0 + 1 * Bps] = Avg2(J, K);
        dst[2 + 1 * Bps] = dst[0 + 2 * Bps] = Avg2(K, L);
        dst[1 + 0 * Bps] = Avg3(I, J, K);
        dst[3 + 0 * Bps] = dst[1 + 1 * Bps] = Avg3(J, K, L);
        dst[3 + 1 * Bps] = dst[1 + 2 * Bps] = Avg3(K, L, L);
        dst[3 + 2 * Bps] = dst[2 + 2 * Bps] =
        dst[0 + 3 * Bps] = dst[1 + 3 * Bps] = dst[2 + 3 * Bps] = dst[3 + 3 * Bps] = (byte)L;
    }

    // port: src/dsp/enc.c HD4
    private static void HD4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        int X = top[4];
        int I = top[3];
        int J = top[2];
        int K = top[1];
        int L = top[0];
        int A = top[5];
        int B = top[6];
        int C = top[7];

        dst[0 + 0 * Bps] = dst[2 + 1 * Bps] = Avg2(I, X);
        dst[0 + 1 * Bps] = dst[2 + 2 * Bps] = Avg2(J, I);
        dst[0 + 2 * Bps] = dst[2 + 3 * Bps] = Avg2(K, J);
        dst[0 + 3 * Bps] = Avg2(L, K);

        dst[3 + 0 * Bps] = Avg3(A, B, C);
        dst[2 + 0 * Bps] = Avg3(X, A, B);
        dst[1 + 0 * Bps] = dst[3 + 1 * Bps] = Avg3(I, X, A);
        dst[1 + 1 * Bps] = dst[3 + 2 * Bps] = Avg3(J, I, X);
        dst[1 + 2 * Bps] = dst[3 + 3 * Bps] = Avg3(K, J, I);
        dst[1 + 3 * Bps] = Avg3(L, K, J);
    }

    // port: src/dsp/enc.c VE4
    private static void VE4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) dst[y * Bps + x] = Avg3(top[x + 4], top[x + 5], top[x + 6]);
    }
    // port: src/dsp/enc.c HE4
    private static void HE4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        for (int y = 0; y < 4; y++) dst.Slice(y * Bps, 4).Fill(Avg3(top[4 - y], top[3 - y], top[Math.Max(0, 2 - y)]));
    }
    // port: src/dsp/enc.c DC4
    private static void DC4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        int dc = 4;
        for (int i = 0; i < 4; i++) dc += top[i] + top[5 + i];
        for (int y = 0; y < 4; y++) dst.Slice(y * Bps, 4).Fill((byte)(dc >> 3));
    }
    // port: src/dsp/enc.c TM4
    private static void TM4(Span<byte> dst, ReadOnlySpan<byte> top)
    {
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) dst[y * Bps + x] = (byte)Math.Clamp(top[3 - y] + top[5 + x] - top[4], 0, 255);
    }
}
