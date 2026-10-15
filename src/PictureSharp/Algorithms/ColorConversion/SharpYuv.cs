using System.Buffers;

namespace PictureSharp.Algorithms.ColorConversion;

internal static partial class SharpYuv
{
    // port: sharpyuv/sharpyuv.c DoSharpArgbToYuv (8-bit RGB, WebP matrix, sRGB transfer)
    public static void Convert(ReadOnlySpan<Rgba32> pixels, RgbToYuv output, CancellationToken cancellationToken = default)
    {
        if (output.Width < 4 || output.Height < 4) { output.Convert(pixels); return; }
        int w = (output.Width + 1) & ~1, h = (output.Height + 1) & ~1, uvw = w >> 1;
        int size = checked(w * h), uvSize = checked(3 * uvw * (h >> 1));
        ushort[] temporary = ArrayPool<ushort>.Shared.Rent(6 * w);
        ushort[] bestY = ArrayPool<ushort>.Shared.Rent(size), targetY = ArrayPool<ushort>.Shared.Rent(size), rgbY = ArrayPool<ushort>.Shared.Rent(2 * w);
        short[] bestUv = ArrayPool<short>.Shared.Rent(uvSize), targetUv = ArrayPool<short>.Shared.Rent(uvSize), rgbUv = ArrayPool<short>.Shared.Rent(3 * uvw);
        try
        {
            for (int y = 0; y < h; y += 2)
            {
                Span<ushort> first = temporary.AsSpan(0, 3 * w), second = temporary.AsSpan(3 * w, 3 * w);
                ImportRow(pixels.Slice(Math.Min(y, output.Height - 1) * output.Width, output.Width), first, w);
                ImportRow(pixels.Slice(Math.Min(y + 1, output.Height - 1) * output.Width, output.Width), second, w);
                StoreGray(first, bestY.AsSpan(y * w, w), w); StoreGray(second, bestY.AsSpan((y + 1) * w, w), w);
                UpdateW(first, targetY.AsSpan(y * w, w), w); UpdateW(second, targetY.AsSpan((y + 1) * w, w), w);
                Span<short> chroma = targetUv.AsSpan((y >> 1) * 3 * uvw, 3 * uvw);
                UpdateChroma(first, second, chroma, uvw);
                chroma.CopyTo(bestUv.AsSpan((y >> 1) * 3 * uvw));
            }
            long previousDifference = long.MaxValue;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long difference = 0;
                for (int y = 0; y < h; y += 2)
                {
                    int offset = (y >> 1) * 3 * uvw;
                    Span<ushort> first = temporary.AsSpan(0, 3 * w), second = temporary.AsSpan(3 * w, 3 * w);
                    Interpolate(bestY.AsSpan(y * w, 2 * w), bestUv.AsSpan(Math.Max(offset - 3 * uvw, 0), 3 * uvw), bestUv.AsSpan(offset, 3 * uvw), bestUv.AsSpan(Math.Min(offset + 3 * uvw, uvSize - 3 * uvw), 3 * uvw), first, second, w);
                    UpdateW(first, rgbY.AsSpan(0, w), w); UpdateW(second, rgbY.AsSpan(w, w), w);
                    UpdateChroma(first, second, rgbUv, uvw);
                    for (int i = 0; i < 2 * w; i++)
                    {
                        int delta = targetY[y * w + i] - rgbY[i];
                        bestY[y * w + i] = (ushort)Math.Clamp(bestY[y * w + i] + delta, 0, 1023);
                        difference += Math.Abs(delta);
                    }
                    for (int i = 0; i < 3 * uvw; i++) bestUv[offset + i] += (short)(targetUv[offset + i] - rgbUv[i]);
                }
                if (iteration > 0 && (difference < 3L * w * h || difference > previousDifference)) break;
                previousDifference = difference;
            }
            ConvertToYuv(bestY, bestUv, output, w, uvw);
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(temporary); ArrayPool<ushort>.Shared.Return(bestY);
            ArrayPool<ushort>.Shared.Return(targetY); ArrayPool<ushort>.Shared.Return(rgbY);
            ArrayPool<short>.Shared.Return(bestUv); ArrayPool<short>.Shared.Return(targetUv); ArrayPool<short>.Shared.Return(rgbUv);
        }
    }

    // port: sharpyuv/sharpyuv.c ImportOneRow
    private static void ImportRow(ReadOnlySpan<Rgba32> pixels, Span<ushort> output, int width)
    {
        for (int x = 0; x < width; x++)
        {
            Rgba32 p = pixels[Math.Min(x, pixels.Length - 1)];
            output[x] = (ushort)(p.R << 2); output[x + width] = (ushort)(p.G << 2); output[x + 2 * width] = (ushort)(p.B << 2);
        }
    }
    // port: sharpyuv/sharpyuv.c RGBToGray, StoreGray
    private static int Gray(long r, long g, long b) => (int)((13933 * r + 46871 * g + 4732 * b + 32768) >> 16);
    private static void StoreGray(ReadOnlySpan<ushort> rgb, Span<ushort> y, int width)
    {
        for (int i = 0; i < width; i++) y[i] = (ushort)Gray(rgb[i], rgb[width + i], rgb[2 * width + i]);
    }
    // port: sharpyuv/sharpyuv.c UpdateW
    private static void UpdateW(ReadOnlySpan<ushort> rgb, Span<ushort> y, int width)
    {
        for (int i = 0; i < width; i++) y[i] = FromLinear(Gray(ToLinear(rgb[i]), ToLinear(rgb[width + i]), ToLinear(rgb[2 * width + i])));
    }
    // port: sharpyuv/sharpyuv.c ScaleDown, UpdateChroma
    private static int ScaleDown(int a, int b, int c, int d) => FromLinear((ToLinear(a) + ToLinear(b) + ToLinear(c) + ToLinear(d) + 2) >> 2);
    private static void UpdateChroma(ReadOnlySpan<ushort> first, ReadOnlySpan<ushort> second, Span<short> output, int uvWidth)
    {
        for (int i = 0; i < uvWidth; i++)
        {
            int r = ScaleDown(first[2 * i], first[2 * i + 1], second[2 * i], second[2 * i + 1]);
            int g = ScaleDown(first[2 * uvWidth + 2 * i], first[2 * uvWidth + 2 * i + 1], second[2 * uvWidth + 2 * i], second[2 * uvWidth + 2 * i + 1]);
            int b = ScaleDown(first[4 * uvWidth + 2 * i], first[4 * uvWidth + 2 * i + 1], second[4 * uvWidth + 2 * i], second[4 * uvWidth + 2 * i + 1]);
            int gray = Gray(r, g, b);
            output[i] = (short)(r - gray); output[uvWidth + i] = (short)(g - gray); output[2 * uvWidth + i] = (short)(b - gray);
        }
    }
    // port: sharpyuv/sharpyuv.c InterpolateTwoRows, Filter2; sharpyuv/sharpyuv_dsp.c SharpYuvFilterRow_C
    private static void Interpolate(ReadOnlySpan<ushort> bestY, ReadOnlySpan<short> previous, ReadOnlySpan<short> current, ReadOnlySpan<short> next, Span<ushort> first, Span<ushort> second, int width)
    {
        int uvw = width >> 1;
        for (int channel = 0; channel < 3; channel++)
        {
            int c = channel * uvw, o = channel * width;
            first[o] = (ushort)Math.Clamp(((3 * current[c] + previous[c] + 2) >> 2) + bestY[0], 0, 1023);
            second[o] = (ushort)Math.Clamp(((3 * current[c] + next[c] + 2) >> 2) + bestY[width], 0, 1023);
            for (int i = 0; i < uvw - 1; i++)
            {
                int a0 = current[c + i], a1 = current[c + i + 1];
                int b0 = previous[c + i], b1 = previous[c + i + 1];
                first[o + 2 * i + 1] = (ushort)Math.Clamp(bestY[2 * i + 1] + ((9 * a0 + 3 * a1 + 3 * b0 + b1 + 8) >> 4), 0, 1023);
                first[o + 2 * i + 2] = (ushort)Math.Clamp(bestY[2 * i + 2] + ((9 * a1 + 3 * a0 + 3 * b1 + b0 + 8) >> 4), 0, 1023);
                b0 = next[c + i]; b1 = next[c + i + 1];
                second[o + 2 * i + 1] = (ushort)Math.Clamp(bestY[width + 2 * i + 1] + ((9 * a0 + 3 * a1 + 3 * b0 + b1 + 8) >> 4), 0, 1023);
                second[o + 2 * i + 2] = (ushort)Math.Clamp(bestY[width + 2 * i + 2] + ((9 * a1 + 3 * a0 + 3 * b1 + b0 + 8) >> 4), 0, 1023);
            }
            first[o + width - 1] = (ushort)Math.Clamp(((3 * current[c + uvw - 1] + previous[c + uvw - 1] + 2) >> 2) + bestY[width - 1], 0, 1023);
            second[o + width - 1] = (ushort)Math.Clamp(((3 * current[c + uvw - 1] + next[c + uvw - 1] + 2) >> 2) + bestY[2 * width - 1], 0, 1023);
        }
    }
    // port: sharpyuv/sharpyuv.c ConvertWRGBToYUV, RGBToYUVComponent; sharpyuv/sharpyuv_csp.c kWebpMatrix
    private static void ConvertToYuv(ReadOnlySpan<ushort> bestY, ReadOnlySpan<short> bestUv, RgbToYuv output, int width, int uvWidth)
    {
        for (int y = 0; y < output.Height; y++)
            for (int x = 0; x < output.Width; x++)
            {
                int o = (y >> 1) * 3 * uvWidth + (x >> 1), gray = bestY[y * width + x];
                int r = bestUv[o] + gray, g = bestUv[o + uvWidth] + gray, b = bestUv[o + 2 * uvWidth] + gray;
                output.Y[y * output.Width + x] = (byte)Math.Clamp((16839 * r + 33059 * g + 6420 * b + (16 << 18) + 131072) >> 18, 0, 255);
            }
        for (int y = 0; y < output.UvHeight; y++)
            for (int x = 0; x < uvWidth; x++)
            {
                int o = y * 3 * uvWidth + x, r = bestUv[o], g = bestUv[o + uvWidth], b = bestUv[o + 2 * uvWidth];
                output.U[y * uvWidth + x] = (byte)Math.Clamp((-9719 * r - 19081 * g + 28800 * b + (128 << 18) + 131072) >> 18, 0, 255);
                output.V[y * uvWidth + x] = (byte)Math.Clamp((28800 * r - 24116 * g - 4684 * b + (128 << 18) + 131072) >> 18, 0, 255);
            }
    }
    // port: sharpyuv/sharpyuv_gamma.c ToLinearSrgb, FromLinearSrgb, FixedPointInterpolation
    private static int ToLinear(int value) => (int)GammaToLinear[value];
    private static ushort FromLinear(int value)
    {
        int position = value >> 7, fraction = value & 127;
        uint a = LinearToGamma[position] >> 6, b = LinearToGamma[position + 1] >> 6;
        return (ushort)(a + (((b - a) * (uint)fraction + 64) >> 7));
    }
}
