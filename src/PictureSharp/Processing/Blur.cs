using System.Buffers;
using System.Runtime.InteropServices;

namespace PictureSharp.Processing;

/// <summary>
/// Box blur, Gaussian blur (three box passes), unsharp mask and 3×3 kernels, ported from Pillow. The callers run them on
/// premultiplied pixels, like Pillow filtering an "RGBa" image.
/// </summary>
internal static class Blur
{
    // port: Pillow src/libImaging/BoxBlur.c ImagingBoxBlur
    public static Rgba32[] BoxBlur(ReadOnlySpan<Rgba32> src, int width, int height, float xRadius, float yRadius, int passes)
    {
        var dst = GC.AllocateUninitializedArray<Rgba32>(src.Length);
        if (xRadius != 0)
        {
            HorizontalBoxBlur(src, dst, width, height, xRadius);
            for (int i = 1; i < passes; i++)
            {
                HorizontalBoxBlur(dst, dst, width, height, xRadius);
            }
        }

        if (yRadius != 0)
        {
            Rgba32[] transposed = ArrayPool<Rgba32>.Shared.Rent(src.Length);
            try
            {
                Span<Rgba32> t = transposed.AsSpan(0, src.Length);
                Transpose(xRadius == 0 ? src : dst, t, width, height);
                for (int i = 0; i < passes; i++)
                {
                    HorizontalBoxBlur(t, t, height, width, yRadius);
                }

                Transpose(t, dst, height, width);
            }
            finally
            {
                ArrayPool<Rgba32>.Shared.Return(transposed);
            }
        }

        if (xRadius == 0 && yRadius == 0)
        {
            src.CopyTo(dst);
        }

        return dst;
    }

    // port: Pillow src/libImaging/BoxBlur.c _gaussian_blur_radius
    public static float GaussianBlurRadius(float radius, int passes)
    {
        float sigma2 = radius * radius / passes;
        // from https://www.mia.uni-saarland.de/Publications/gwosdek-ssvm11.pdf
        float l2 = (float)Math.Sqrt((12.0 * sigma2) + 1.0);
        float l = (float)Math.Floor((l2 - 1.0) / 2.0);
        float a = ((2 * l) + 1) * ((l * (l + 1)) - (3 * sigma2));
        a /= 6 * (sigma2 - ((l + 1) * (l + 1)));
        return l + a;
    }

    // port: Pillow src/libImaging/BoxBlur.c ImagingGaussianBlur
    public static Rgba32[] GaussianBlur(ReadOnlySpan<Rgba32> src, int width, int height, float radius)
    {
        const int Passes = 3;
        float r = GaussianBlurRadius(radius, Passes);
        return BoxBlur(src, width, height, r, r, Passes);
    }

    // port: Pillow src/libImaging/UnsharpMask.c ImagingUnsharpMask
    public static Rgba32[] UnsharpMask(ReadOnlySpan<Rgba32> src, int width, int height, float radius, int percent, int threshold)
    {
        Rgba32[] blurred = GaussianBlur(src, width, height, radius);
        ReadOnlySpan<byte> lineIn = MemoryMarshal.AsBytes(src);
        Span<byte> lineOut = MemoryMarshal.AsBytes(blurred.AsSpan());
        for (int i = 0; i < lineIn.Length; i++)
        {
            int diff = lineIn[i] - lineOut[i];
            lineOut[i] = Math.Abs(diff) > threshold ? Clip8(lineIn[i] + (diff * percent / 100)) : lineIn[i];
        }

        return blurred;
    }

    // port: Pillow src/libImaging/Filter.c ImagingFilter3x3 (4 bands); kernel already divided by its divisor.
    // Border rows and columns are copied unchanged.
    public static Rgba32[] Filter3x3(ReadOnlySpan<Rgba32> src, int width, int height, ReadOnlySpan<float> kernel, float offset)
    {
        var dst = GC.AllocateUninitializedArray<Rgba32>(src.Length);
        src.CopyTo(dst);
        if (width < 3 || height < 3)
        {
            return dst;
        }

        // Add one time for rounding
        offset += 0.5f;
        ReadOnlySpan<byte> image = MemoryMarshal.AsBytes(src);
        Span<byte> output = MemoryMarshal.AsBytes(dst.AsSpan());
        int stride = width * 4;
        for (int y = 1; y < height - 1; y++)
        {
            ReadOnlySpan<byte> inM1 = image.Slice((y - 1) * stride, stride);
            ReadOnlySpan<byte> in0 = image.Slice(y * stride, stride);
            ReadOnlySpan<byte> in1 = image.Slice((y + 1) * stride, stride);
            Span<byte> outRow = output.Slice(y * stride, stride);
            for (int x = 4; x < stride - 4; x++)
            {
                float ss = offset;
                ss += Kernel1x3(in1, x, kernel[0..3]);
                ss += Kernel1x3(in0, x, kernel[3..6]);
                ss += Kernel1x3(inM1, x, kernel[6..9]);
                outRow[x] = Clip8(ss);
            }
        }

        return dst;
    }

    // port: Pillow src/libImaging/Filter.c KERNEL1x3
    private static float Kernel1x3(ReadOnlySpan<byte> row, int x, ReadOnlySpan<float> kernel) =>
        (row[x - 4] * kernel[0]) + (row[x] * kernel[1]) + (row[x + 4] * kernel[2]);

    // port: Pillow src/libImaging/BoxBlur.c ImagingHorizontalBoxBlur
    private static void HorizontalBoxBlur(ReadOnlySpan<Rgba32> src, Span<Rgba32> dst, int width, int height, float floatRadius)
    {
        int radius = (int)floatRadius;
        uint ww = (uint)((float)(1 << 24) / ((floatRadius * 2) + 1));
        uint fw = ((1u << 24) - ((uint)((radius * 2) + 1) * ww)) / 2;
        int edgeA = Math.Min(radius + 1, width);
        int edgeB = Math.Max(width - radius - 1, 0);
        Rgba32[] line = ArrayPool<Rgba32>.Shared.Rent(width);
        try
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<Rgba32> lineIn = src.Slice(y * width, width);
                LineBoxBlur32(line, lineIn, width - 1, radius, edgeA, edgeB, ww, fw);
                line.AsSpan(0, width).CopyTo(dst.Slice(y * width, width));
            }
        }
        finally
        {
            ArrayPool<Rgba32>.Shared.Return(line);
        }
    }

    // port: Pillow src/libImaging/BoxBlur.c ImagingLineBoxBlur32
    private static void LineBoxBlur32(Span<Rgba32> lineOut, ReadOnlySpan<Rgba32> lineIn, int lastx, int radius, int edgeA, int edgeB, uint ww, uint fw)
    {
        ReadOnlySpan<byte> px = MemoryMarshal.AsBytes(lineIn);
        Span<byte> output = MemoryMarshal.AsBytes(lineOut);
        Span<uint> acc = stackalloc uint[4];

        // Compute acc for -1 pixel (outside of image): from "-radius-1" to "-1" get first pixel,
        // then from "0" to "radius-1".
        for (int c = 0; c < 4; c++)
        {
            acc[c] = (uint)(px[c] * (radius + 1));
        }

        // As radius can be bigger than xsize, iterate to edgeA -1.
        for (int x = 0; x < edgeA - 1; x++)
        {
            for (int c = 0; c < 4; c++)
            {
                acc[c] += px[(x * 4) + c];
            }
        }

        // Then multiply remainder to last x.
        for (int c = 0; c < 4; c++)
        {
            acc[c] += (uint)(px[(lastx * 4) + c] * (radius - edgeA + 1));
        }

        if (edgeA <= edgeB)
        {
            // Subtract pixel from left ("0"). Add pixels from radius.
            for (int x = 0; x < edgeA; x++)
            {
                Step(px, output, acc, x, 0, x + radius, 0, x + radius + 1, ww, fw);
            }

            // Subtract previous pixel from "-radius". Add pixels from radius.
            for (int x = edgeA; x < edgeB; x++)
            {
                Step(px, output, acc, x, x - radius - 1, x + radius, x - radius - 1, x + radius + 1, ww, fw);
            }

            // Subtract previous pixel from "-radius". Add last pixel.
            for (int x = edgeB; x <= lastx; x++)
            {
                Step(px, output, acc, x, x - radius - 1, lastx, x - radius - 1, lastx, ww, fw);
            }
        }
        else
        {
            for (int x = 0; x < edgeB; x++)
            {
                Step(px, output, acc, x, 0, x + radius, 0, x + radius + 1, ww, fw);
            }

            for (int x = edgeB; x < edgeA; x++)
            {
                Step(px, output, acc, x, 0, lastx, 0, lastx, ww, fw);
            }

            for (int x = edgeA; x <= lastx; x++)
            {
                Step(px, output, acc, x, x - radius - 1, lastx, x - radius - 1, lastx, ww, fw);
            }
        }
    }

    // port: Pillow src/libImaging/BoxBlur.c MOVE_ACC, ADD_FAR, SAVE
    private static void Step(ReadOnlySpan<byte> px, Span<byte> output, Span<uint> acc, int x,
        int subtract, int add, int left, int right, uint ww, uint fw)
    {
        for (int c = 0; c < 4; c++)
        {
            acc[c] += (uint)(px[(add * 4) + c] - px[(subtract * 4) + c]);
            uint bulk = (acc[c] * ww) + ((uint)(px[(left * 4) + c] + px[(right * 4) + c]) * fw);
            output[(x * 4) + c] = (byte)((bulk + (1 << 23)) >> 24);
        }
    }

    private static void Transpose(ReadOnlySpan<Rgba32> src, Span<Rgba32> dst, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba32> row = src.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                dst[(x * height) + y] = row[x];
            }
        }
    }

    private static byte Clip8(int v) => v >= 255 ? (byte)255 : v <= 0 ? (byte)0 : (byte)v;

    private static byte Clip8(float v) => v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(int)v;
}
