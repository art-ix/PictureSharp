using System.Buffers;
using static WebpSharp.Formats.WebP.Lossless.LosslessTransforms;

namespace WebpSharp.Formats.WebP.Lossless;

internal static class Vp8LForwardTransforms
{
    // port: src/dsp/lossless_enc.c SubtractGreenFromBlueAndRed_C
    public static void SubtractGreen(Span<uint> pixels)
    {
        foreach (ref uint p in pixels)
        {
            uint green = (p >> 8) & 255;
            p = (p & 0xff00ff00u) | (((p & 0x00ff00ffu) + 0x01000100u - ((green << 16) | green)) & 0x00ff00ffu);
        }
    }

    // port: src/enc/predictor_enc.c VP8LResidualImage, GetBestPredictorForTile, CopyImageWithPrediction
    public static uint[] Predictor(uint[] pixels, int width, int height, int bits, bool lowEffort, bool exact, int nearLossless = 100, bool subtractGreen = false)
    {
        int tw = Vp8LDecoder.SubSampleSize(width, bits), th = Vp8LDecoder.SubSampleSize(height, bits);
        int quantization = 1 << (nearLossless == 100 ? 0 : 5 - nearLossless / 20);
        var modes = new uint[tw * th];
        uint[] rows = ArrayPool<uint>.Shared.Rent(2 * (width + 1));
        uint[] residuals = ArrayPool<uint>.Shared.Rent(width * height);
        Span<uint> accumulated = stackalloc uint[1024]; accumulated.Clear();
        Span<uint> histo = stackalloc uint[1024];
        Span<uint> bestHistogram = stackalloc uint[1024];
        try
        {
            for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < tw; tx++)
            {
                int best = 11;
                if (!lowEffort)
                {
                    long bestCost = long.MaxValue;
                    int sx = tx << bits, sy = ty << bits, ex = Math.Min(width, sx + (1 << bits)), ey = Math.Min(height, sy + (1 << bits));
                    for (int mode = 0; mode < 14; mode++)
                    {
                        histo.Clear();
                        Span<uint> upper = rows.AsSpan(0, width + 1), current = rows.AsSpan(width + 1, width + 1);
                        if (sy > 0) pixels.AsSpan((sy - 1) * width, width + 1).CopyTo(upper);
                        for (int y = sy; y < ey; y++)
                        {
                            pixels.AsSpan(y * width, width + (y + 1 < height ? 1 : 0)).CopyTo(current);
                            for (int x = sx; x < ex; x++)
                            {
                                uint residual = Residual(upper, current, x, y, width, mode, exact, Quantization(pixels, width, height, x, y, quantization, subtractGreen), subtractGreen);
                                for (int c = 0; c < 4; c++) histo[c * 256 + (int)((residual >> (24 - 8 * c)) & 255)]++;
                            }
                            Span<uint> swap = upper; upper = current; current = swap;
                        }
                        long cost = 0;
                        for (int c = 0; c < 4; c++)
                        {
                            cost += Vp8LEntropy.Spatial(histo.Slice(c * 256, 256), 1, 94);
                            cost += Vp8LEntropy.Combined(histo.Slice(c * 256, 256), accumulated.Slice(c * 256, 256));
                        }
                        if (tx > 0 && mode == ((modes[ty * tw + tx - 1] >> 8) & 255)) cost -= 15 * Vp8LEntropy.One;
                        if (ty > 0 && mode == ((modes[(ty - 1) * tw + tx] >> 8) & 255)) cost -= 15 * Vp8LEntropy.One;
                        if (cost < bestCost) { bestCost = cost; best = mode; histo.CopyTo(bestHistogram); }
                    }
                    for (int i = 0; i < 1024; i++) accumulated[i] += bestHistogram[i];
                }
                modes[ty * tw + tx] = 0xff000000u | (uint)(best << 8);
            }
            Span<uint> up = rows.AsSpan(0, width + 1), row = rows.AsSpan(width + 1, width + 1);
            for (int y = 0; y < height; y++)
            {
                pixels.AsSpan(y * width, width + (y + 1 < height ? 1 : 0)).CopyTo(row);
                for (int x = 0; x < width; x++)
                    residuals[y * width + x] = Residual(up, row, x, y, width, (int)((modes[(y >> bits) * tw + (x >> bits)] >> 8) & 255), exact || lowEffort, Quantization(pixels, width, height, x, y, quantization, subtractGreen), subtractGreen);
                Span<uint> swap = up; up = row; row = swap;
            }
            residuals.AsSpan(0, width * height).CopyTo(pixels);
            return modes;
        }
        finally { ArrayPool<uint>.Shared.Return(rows); ArrayPool<uint>.Shared.Return(residuals); }
    }

    // port: src/enc/predictor_enc.c GetResidual; src/dsp/lossless.c VP8LPredictor0_C .. VP8LPredictor13_C
    private static uint Residual(Span<uint> up, Span<uint> row, int x, int y, int width, int mode, bool exact, int quantization, bool subtractGreen)
    {
        uint prediction;
        if (y == 0) prediction = x == 0 ? 0xff000000u : row[x - 1];
        else if (x == 0) prediction = up[0];
        else
        {
            uint l = row[x - 1], t = up[x], tl = up[x - 1], tr = up[x + 1];
            prediction = mode switch
            {
                1 => l, 2 => t, 3 => tr, 4 => tl, 5 => Average3(l, t, tr), 6 => Average2(l, tl),
                7 => Average2(l, t), 8 => Average2(tl, t), 9 => Average2(t, tr), 10 => Average4(l, tl, t, tr),
                11 => Select(t, l, tl), 12 => ClampedAddSubtractFull(l, t, tl), 13 => ClampedAddSubtractHalf(l, t, tl), _ => 0xff000000u,
            };
        }
        uint residual = SubPixels(row[x], prediction);
        if (!exact && quantization > 1 && mode != 0)
        {
            residual = NearResidual(row[x], prediction, quantization, subtractGreen);
            row[x] = AddPixels(prediction, residual);
        }
        if (!exact && (row[x] >> 24) == 0)
        {
            residual &= 0xff000000u;
            row[x] = prediction & 0xffffff;
            if (x == 0 && y != 0) up[width] = row[0];
        }
        return residual;
    }

    // port: src/enc/predictor_enc.c MaxDiffAroundPixel, NearLossless
    private static int Quantization(uint[] source, int width, int height, int x, int y, int maximum, bool subtractGreen)
    {
        if (maximum == 1 || x == 0 || x == width - 1 || y == 0 || y == height - 1) return 1;
        int i = y * width + x, diff = 0;
        ReadOnlySpan<int> neighbors = [-1, 1, -width, width];
        uint p = RestoreGreen(source[i], subtractGreen);
        foreach (int offset in neighbors)
        {
            uint other = RestoreGreen(source[i + offset], subtractGreen);
            for (int c = 0; c < 4; c++) diff = Math.Max(diff, Math.Abs((int)((p >> (c * 8)) & 255) - (int)((other >> (c * 8)) & 255)));
        }
        if (diff <= 2) return 1;
        while (maximum >= diff) maximum >>= 1;
        return maximum;
    }

    private static uint RestoreGreen(uint p, bool subtractGreen)
    {
        if (!subtractGreen) return p;
        uint g = (p >> 8) & 255;
        return (p & 0xff00ff00u) | (((p & 0x00ff00ffu) + (g << 16) + g) & 0x00ff00ffu);
    }

    // port: src/enc/predictor_enc.c NearLosslessComponent
    private static int NearComponent(int value, int prediction, int boundary, int quantization)
    {
        int residual = (value - prediction) & 255, boundaryResidual = (boundary - prediction) & 255;
        int lower = residual & ~(quantization - 1), upper = lower + quantization;
        int bias = ((boundary - value) & 255) < boundaryResidual ? 1 : 0;
        if (residual - lower < upper - residual + bias)
            return residual > boundaryResidual && lower <= boundaryResidual ? lower + (quantization >> 1) : lower;
        return residual <= boundaryResidual && upper > boundaryResidual ? lower + (quantization >> 1) : upper & 255;
    }

    // port: src/enc/predictor_enc.c NearLossless
    private static uint NearResidual(uint value, uint prediction, int quantization, bool subtractGreen)
    {
        int alpha = (int)(value >> 24), pa = (int)(prediction >> 24);
        int a = alpha is 0 or 255 ? (alpha - pa) & 255 : NearComponent(alpha, pa, 255, quantization);
        int g = NearComponent((int)(value >> 8) & 255, (int)(prediction >> 8) & 255, 255, quantization);
        int newGreen = subtractGreen ? ((int)(prediction >> 8) + g) & 255 : 0;
        int greenDiff = subtractGreen ? (newGreen - (int)(value >> 8)) & 255 : 0;
        int r = NearComponent(((int)(value >> 16) - greenDiff) & 255, (int)(prediction >> 16) & 255, 255 - newGreen, quantization);
        int b = NearComponent(((int)value - greenDiff) & 255, (int)prediction & 255, 255 - newGreen, quantization);
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    // port: src/enc/predictor_enc.c VP8LColorSpaceTransform, GetBestGreenToRed, GetBestGreenRedToBlue
    public static uint[] CrossColor(uint[] pixels, int width, int height, int bits, int quality)
    {
        int tw = Vp8LDecoder.SubSampleSize(width, bits), th = Vp8LDecoder.SubSampleSize(height, bits);
        var transforms = new uint[tw * th];
        Span<uint> red = stackalloc uint[256], blue = stackalloc uint[256]; red.Clear(); blue.Clear();
        uint previous = 0;
        ReadOnlySpan<int> delta = [16, 16, 8, 4, 2, 2, 2];
        ReadOnlySpan<int> axisX = [0, 0, -1, 1, -1, -1, 1, 1], axisY = [-1, 1, 0, 0, -1, 1, -1, 1];
        for (int ty = 0; ty < th; ty++)
        for (int tx = 0; tx < tw; tx++)
        {
            int sx = tx << bits, sy = ty << bits, ex = Math.Min(width, sx + (1 << bits)), ey = Math.Min(height, sy + (1 << bits));
            uint above = ty > 0 ? transforms[(ty - 1) * tw + tx] : 0;
            int gr = 0, gb = 0, rb = 0;
            long best = ColorCost(pixels, width, sx, sy, ex, ey, 0, 0, false, previous, above, red);
            for (int iter = 0; iter < 4 + ((7 * quality) >> 8); iter++)
            {
                int d = 32 >> iter;
                for (int offset = -d; offset <= d; offset += d * 2)
                {
                    int candidate = gr + offset;
                    long cost = ColorCost(pixels, width, sx, sy, ex, ey, candidate, 0, false, previous, above, red);
                    if (cost < best) { best = cost; gr = candidate; }
                }
            }
            best = ColorCost(pixels, width, sx, sy, ex, ey, 0, 0, true, previous, above, blue);
            int iterations = quality < 25 ? 1 : quality > 50 ? 7 : 4;
            for (int iter = 0; iter < iterations; iter++)
            {
                for (int axis = 0; axis < 8; axis++)
                {
                    int cg = gb + axisX[axis] * delta[iter], cr = rb + axisY[axis] * delta[iter];
                    long cost = ColorCost(pixels, width, sx, sy, ex, ey, cg, cr, true, previous, above, blue);
                    if (cost < best) { best = cost; gb = cg; rb = cr; }
                }
                if (delta[iter] == 2 && gb == 0 && rb == 0) break;
            }
            previous = 0xff000000u | (uint)((gr & 255) | ((gb & 255) << 8) | ((rb & 255) << 16));
            transforms[ty * tw + tx] = previous;
            for (int y = sy; y < ey; y++)
            for (int x = sx; x < ex; x++)
            {
                int i = y * width + x;
                uint p = pixels[i];
                int g = (sbyte)(p >> 8), r = (sbyte)(p >> 16);
                pixels[i] = (p & 0xff00ff00u) | (uint)((((int)(p >> 16) - ((sbyte)gr * g >> 5)) & 255) << 16)
                    | (uint)(((int)p - ((sbyte)gb * g >> 5) - ((sbyte)rb * r >> 5)) & 255);
            }
            for (int y = sy; y < ey; y++)
            for (int x = sx; x < ex; x++)
            {
                int i = y * width + x;
                uint p = pixels[i];
                if (i >= 2 && p == pixels[i - 1] && p == pixels[i - 2]) continue;
                if (i >= width + 2 && p == pixels[i - width] && pixels[i - 1] == pixels[i - width - 1] && pixels[i - 2] == pixels[i - width - 2]) continue;
                red[(int)((p >> 16) & 255)]++; blue[(int)(p & 255)]++;
            }
        }
        return transforms;
    }

    // port: src/enc/predictor_enc.c GetPredictionCostCrossColorRed, GetPredictionCostCrossColorBlue
    private static long ColorCost(uint[] pixels, int width, int sx, int sy, int ex, int ey, int green, int red,
        bool blue, uint previous, uint above, ReadOnlySpan<uint> accumulated)
    {
        Span<uint> counts = stackalloc uint[256]; counts.Clear();
        for (int y = sy; y < ey; y++)
        for (int x = sx; x < ex; x++)
        {
            uint p = pixels[y * width + x];
            int transformed = blue ? (int)(p & 255) - ((sbyte)green * (sbyte)(p >> 8) >> 5) - ((sbyte)red * (sbyte)(p >> 16) >> 5)
                : (int)((p >> 16) & 255) - ((sbyte)green * (sbyte)(p >> 8) >> 5);
            counts[transformed & 255]++;
        }
        long cost = Vp8LEntropy.Combined(counts, accumulated) + Vp8LEntropy.Spatial(counts, 3, 240);
        int shift = blue ? 8 : 0;
        if ((byte)green == (byte)(previous >> shift)) cost -= 3 * Vp8LEntropy.One;
        if ((byte)green == (byte)(above >> shift)) cost -= 3 * Vp8LEntropy.One;
        if (green == 0) cost -= 3 * Vp8LEntropy.One;
        if (blue)
        {
            if ((byte)red == (byte)(previous >> 16)) cost -= 3 * Vp8LEntropy.One;
            if ((byte)red == (byte)(above >> 16)) cost -= 3 * Vp8LEntropy.One;
            if (red == 0) cost -= 3 * Vp8LEntropy.One;
        }
        return cost;
    }
}
