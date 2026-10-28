using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// The Pillow ports of <see cref="Blur"/> on premultiplied working values (floats in [0, 1]) for the high-precision
/// paths: the same kernels and edge handling, with sums in double, passes stored as float without rounding, and
/// values clipped to [0, 1] where the 8-bit code clips to 0–255. Rows are independent, so they run in parallel
/// without changing the result.
/// </summary>
internal static class BlurWide
{
    // Rows per band of the parallel passes.
    private const int BandRows = 16;

    /// <summary><see cref="Blur.BoxBlur"/>; <paramref name="src"/> is not modified.</summary>
    public static Vector4[] BoxBlur(ReadOnlySpan<Vector4> src, int width, int height, float xRadius, float yRadius, int passes,
        CancellationToken cancellationToken = default)
    {
        var dst = GC.AllocateUninitializedArray<Vector4>(src.Length);
        src.CopyTo(dst);
        if (xRadius != 0)
        {
            for (int i = 0; i < passes; i++)
            {
                HorizontalBoxBlur(dst, width, height, xRadius, cancellationToken);
            }
        }

        if (yRadius != 0)
        {
            Vector4[] transposed = ArrayPool<Vector4>.Shared.Rent(src.Length);
            try
            {
                Transpose(dst, transposed, width, height);
                for (int i = 0; i < passes; i++)
                {
                    HorizontalBoxBlur(transposed, height, width, yRadius, cancellationToken);
                }

                Transpose(transposed, dst, height, width);
            }
            finally
            {
                ArrayPool<Vector4>.Shared.Return(transposed);
            }
        }

        return dst;
    }

    /// <summary><see cref="Blur.GaussianBlur"/>: three box passes of the same radius.</summary>
    public static Vector4[] GaussianBlur(ReadOnlySpan<Vector4> src, int width, int height, float radius,
        CancellationToken cancellationToken = default)
    {
        const int Passes = 3;
        float r = Blur.GaussianBlurRadius(radius, Passes);
        return BoxBlur(src, width, height, r, r, Passes, cancellationToken);
    }

    /// <summary>
    /// <see cref="Blur.UnsharpMask"/>: a difference must exceed <paramref name="threshold"/> (normalized), the sharpened
    /// value is unrounded.
    /// </summary>
    public static Vector4[] UnsharpMask(ReadOnlySpan<Vector4> src, int width, int height, float radius, int percent, float threshold,
        CancellationToken cancellationToken = default)
    {
        Vector4[] blurred = GaussianBlur(src, width, height, radius, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<float> lineIn = MemoryMarshal.Cast<Vector4, float>(src);
        Span<float> lineOut = MemoryMarshal.Cast<Vector4, float>(blurred.AsSpan());
        float amount = percent / 100f;
        for (int i = 0; i < lineIn.Length; i++)
        {
            float diff = lineIn[i] - lineOut[i];
            lineOut[i] = MathF.Abs(diff) > threshold ? Math.Clamp(lineIn[i] + (diff * amount), 0f, 1f) : lineIn[i];
        }

        return blurred;
    }

    /// <summary><see cref="Blur.Filter3x3"/> without its rounding offset; border rows and columns are copied unchanged.</summary>
    public static Vector4[] Filter3x3(ReadOnlySpan<Vector4> src, int width, int height, ReadOnlySpan<float> kernel,
        CancellationToken cancellationToken = default)
    {
        var dst = GC.AllocateUninitializedArray<Vector4>(src.Length);
        src.CopyTo(dst);
        if (width < 3 || height < 3)
        {
            return dst;
        }

        ReadOnlySpan<float> image = MemoryMarshal.Cast<Vector4, float>(src);
        Span<float> output = MemoryMarshal.Cast<Vector4, float>(dst.AsSpan());
        int stride = width * 4;
        for (int y = 1; y < height - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<float> inM1 = image.Slice((y - 1) * stride, stride);
            ReadOnlySpan<float> in0 = image.Slice(y * stride, stride);
            ReadOnlySpan<float> in1 = image.Slice((y + 1) * stride, stride);
            Span<float> outRow = output.Slice(y * stride, stride);
            for (int x = 4; x < stride - 4; x++)
            {
                double ss = Kernel1x3(in1, x, kernel[0..3]) + Kernel1x3(in0, x, kernel[3..6]) + Kernel1x3(inM1, x, kernel[6..9]);
                outRow[x] = ss <= 0 ? 0f : ss >= 1 ? 1f : (float)ss;
            }
        }

        return dst;
    }

    private static double Kernel1x3(ReadOnlySpan<float> row, int x, ReadOnlySpan<float> kernel) =>
        ((double)row[x - 4] * kernel[0]) + ((double)row[x] * kernel[1]) + ((double)row[x + 4] * kernel[2]);

    // Blur.HorizontalBoxBlur in place: each row goes through its own line buffer.
    private static void HorizontalBoxBlur(Vector4[] pixels, int width, int height, float floatRadius, CancellationToken cancellationToken)
    {
        int radius = (int)floatRadius;
        // The exact weights that Pillow approximates with 24 fractional bits.
        double ww = 1.0 / ((floatRadius * 2.0) + 1.0);
        double fw = (1.0 - (((radius * 2) + 1) * ww)) / 2;
        int edgeA = Math.Min(radius + 1, width);
        int edgeB = Math.Max(width - radius - 1, 0);
        Parallelism.ForBands(height, BandRows, (long)width * height, cancellationToken, (top, bottom) =>
        {
            Vector4[] line = ArrayPool<Vector4>.Shared.Rent(width);
            try
            {
                for (int y = top; y < bottom; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Span<Vector4> row = pixels.AsSpan(y * width, width);
                    LineBoxBlur(line, row, width - 1, radius, edgeA, edgeB, ww, fw);
                    line.AsSpan(0, width).CopyTo(row);
                }
            }
            finally
            {
                ArrayPool<Vector4>.Shared.Return(line);
            }
        });
    }

    // Blur.LineBoxBlur32 with a double accumulator.
    private static void LineBoxBlur(Span<Vector4> lineOut, ReadOnlySpan<Vector4> lineIn, int lastx, int radius, int edgeA, int edgeB, double ww, double fw)
    {
        ReadOnlySpan<float> px = MemoryMarshal.Cast<Vector4, float>(lineIn);
        Span<float> output = MemoryMarshal.Cast<Vector4, float>(lineOut);
        Span<double> acc = stackalloc double[4];
        for (int c = 0; c < 4; c++)
        {
            acc[c] = px[c] * (double)(radius + 1);
        }

        for (int x = 0; x < edgeA - 1; x++)
        {
            for (int c = 0; c < 4; c++)
            {
                acc[c] += px[(x * 4) + c];
            }
        }

        for (int c = 0; c < 4; c++)
        {
            acc[c] += px[(lastx * 4) + c] * (double)(radius - edgeA + 1);
        }

        if (edgeA <= edgeB)
        {
            for (int x = 0; x < edgeA; x++)
            {
                Step(px, output, acc, x, 0, x + radius, 0, x + radius + 1, ww, fw);
            }

            for (int x = edgeA; x < edgeB; x++)
            {
                Step(px, output, acc, x, x - radius - 1, x + radius, x - radius - 1, x + radius + 1, ww, fw);
            }

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

    private static void Step(ReadOnlySpan<float> px, Span<float> output, Span<double> acc, int x,
        int subtract, int add, int left, int right, double ww, double fw)
    {
        for (int c = 0; c < 4; c++)
        {
            acc[c] += (double)px[(add * 4) + c] - px[(subtract * 4) + c];
            output[(x * 4) + c] = (float)((acc[c] * ww) + (((double)px[(left * 4) + c] + px[(right * 4) + c]) * fw));
        }
    }

    private static void Transpose(ReadOnlySpan<Vector4> src, Span<Vector4> dst, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Vector4> row = src.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                dst[(x * height) + y] = row[x];
            }
        }
    }
}
