using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PictureSharp.Domain.Pixels;
using PictureSharp.Primitives.Parallelism;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Affine
{
    /// <summary>
    /// A frame transformed by <paramref name="matrix"/> onto a <paramref name="width"/>×<paramref name="height"/>
    /// canvas of <paramref name="background"/>: the 8-bit port for <see cref="Rgba32"/>, otherwise the same sampling on
    /// premultiplied working values, rounded once.
    /// </summary>
    public readonly struct TransformFrame(ImageFrame frame, int width, int height, double[] matrix, ResamplingFilter filter, Rgba64 background,
        CancellationToken cancellationToken) : IPixelOperation<PixelBuffer>
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            TPixel[] source = frame.Buffer.As<TPixel>();
            var output = GC.AllocateUninitializedArray<TPixel>(width * height);
            if (filter == ResamplingFilter.Nearest)
            {
                output.AsSpan().Fill(TPixel.FromRgba64(background));
                TransformNearest<TPixel>(source, frame.Width, frame.Height, output, width, height, matrix, cancellationToken);
            }
            else if (typeof(TPixel) == typeof(Rgba32))
            {
                Span<Rgba32> fill = [background.ToRgba32()];
                Premultiplied.FromStraight(fill, fill);
                Rgba32[] narrow = Unsafe.As<Rgba32[]>(output);
                narrow.AsSpan().Fill(fill[0]);
                Rgba32[] scratch = ArrayPool<Rgba32>.Shared.Rent(source.Length);
                try
                {
                    Premultiplied.FromStraight(Unsafe.As<Rgba32[]>(source), scratch);
                    TransformFiltered(scratch, frame.Width, frame.Height, narrow, width, height, matrix, filter, cancellationToken);
                }
                finally
                {
                    ArrayPool<Rgba32>.Shared.Return(scratch);
                }

                Premultiplied.ToStraight(narrow);
            }
            else
            {
                Vector4[] premultiplied = ArrayPool<Vector4>.Shared.Rent(source.Length);
                Vector4[] result = ArrayPool<Vector4>.Shared.Rent(output.Length);
                try
                {
                    UnitRows.Premultiply<TPixel, EncodedUnit>(source, premultiplied);
                    Span<Vector4> fill = [Vector4.Zero];
                    UnitRows.Premultiply<Rgba64, EncodedUnit>([background], fill);
                    result.AsSpan(0, output.Length).Fill(fill[0]);
                    TransformFilteredWide(premultiplied, frame.Width, frame.Height, result, width, height, matrix, filter, cancellationToken);
                    UnitRows.Unpremultiply<TPixel, EncodedUnit>(result.AsSpan(0, output.Length), output);
                }
                finally
                {
                    ArrayPool<Vector4>.Shared.Return(premultiplied);
                    ArrayPool<Vector4>.Shared.Return(result);
                }
            }

            return PixelBuffer.From(output);
        }
    }

    /// <summary>
    /// <see cref="TransformFiltered"/> on premultiplied working values: Pillow's sampling in double precision without
    /// its truncation to 8 bits, clipped to [0, 1] like its bicubic result.
    /// </summary>
    public static void TransformFilteredWide(Vector4[] src, int srcWidth, int srcHeight, Vector4[] dst, int width, int height,
        double[] a, ResamplingFilter filter, CancellationToken cancellationToken = default, int originX = 0, int originY = 0)
    {
        bool bicubic = filter == ResamplingFilter.Bicubic;
        Parallelism.ForBands(height, 16, (long)width * height * 16, cancellationToken, (start, end) =>
        {
            ReadOnlySpan<float> image = MemoryMarshal.Cast<Vector4, float>(src.AsSpan(0, srcWidth * srcHeight));
            Span<float> output = MemoryMarshal.Cast<Vector4, float>(dst.AsSpan(0, width * height));
            for (int y = start; y < end; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    double xin = x + originX + 0.5;
                    double yin = y + originY + 0.5;
                    double xx = (a[0] * xin) + (a[1] * yin) + a[2];
                    double yy = (a[3] * xin) + (a[4] * yin) + a[5];
                    Span<float> pixel = output.Slice(((y * width) + x) * 4, 4);
                    if (bicubic)
                    {
                        BicubicFilterWide(pixel, image, srcWidth, srcHeight, xx, yy);
                    }
                    else
                    {
                        BilinearFilterWide(pixel, image, srcWidth, srcHeight, xx, yy);
                    }
                }
            }
        });
    }

    // BilinearFilter32 on float channels, without the final truncation.
    private static void BilinearFilterWide(Span<float> output, ReadOnlySpan<float> image, int xsize, int ysize, double xin, double yin)
    {
        if (xin < 0.0 || xin >= xsize || yin < 0.0 || yin >= ysize)
        {
            return;
        }

        xin -= 0.5;
        yin -= 0.5;
        int x = Floor(xin);
        int y = Floor(yin);
        double dx = xin - x;
        double dy = yin - y;
        int stride = xsize * 4;
        int x0 = Clip(x, xsize) * 4;
        int x1 = Clip(x + 1, xsize) * 4;
        for (int b = 0; b < 4; b++)
        {
            ReadOnlySpan<float> row = image.Slice(Clip(y, ysize) * stride);
            double v1 = Bilinear(row[x0 + b], row[x1 + b], dx);
            double v2;
            if (y + 1 >= 0 && y + 1 < ysize)
            {
                row = image.Slice((y + 1) * stride);
                v2 = Bilinear(row[x0 + b], row[x1 + b], dx);
            }
            else
            {
                v2 = v1;
            }

            output[b] = (float)Bilinear(v1, v2, dy);
        }
    }

    // BicubicFilter32 on float channels, clipped to [0, 1] instead of 0–255 and not truncated.
    private static void BicubicFilterWide(Span<float> output, ReadOnlySpan<float> image, int xsize, int ysize, double xin, double yin)
    {
        if (xin < 0.0 || xin >= xsize || yin < 0.0 || yin >= ysize)
        {
            return;
        }

        xin -= 0.5;
        yin -= 0.5;
        int x = Floor(xin);
        int y = Floor(yin);
        double dx = xin - x;
        double dy = yin - y;
        x--;
        y--;
        int stride = xsize * 4;
        int x0 = Clip(x, xsize) * 4;
        int x1 = Clip(x + 1, xsize) * 4;
        int x2 = Clip(x + 2, xsize) * 4;
        int x3 = Clip(x + 3, xsize) * 4;
        for (int b = 0; b < 4; b++)
        {
            ReadOnlySpan<float> row = image.Slice(Clip(y, ysize) * stride);
            double v1 = Bicubic(row[x0 + b], row[x1 + b], row[x2 + b], row[x3 + b], dx);
            double v2 = v1, v3, v4;
            if (y + 1 >= 0 && y + 1 < ysize)
            {
                row = image.Slice((y + 1) * stride);
                v2 = Bicubic(row[x0 + b], row[x1 + b], row[x2 + b], row[x3 + b], dx);
            }

            v3 = v2;
            if (y + 2 >= 0 && y + 2 < ysize)
            {
                row = image.Slice((y + 2) * stride);
                v3 = Bicubic(row[x0 + b], row[x1 + b], row[x2 + b], row[x3 + b], dx);
            }

            v4 = v3;
            if (y + 3 >= 0 && y + 3 < ysize)
            {
                row = image.Slice((y + 3) * stride);
                v4 = Bicubic(row[x0 + b], row[x1 + b], row[x2 + b], row[x3 + b], dx);
            }

            v1 = Bicubic(v1, v2, v3, v4, dy);
            output[b] = v1 <= 0.0 ? 0f : v1 >= 1.0 ? 1f : (float)v1;
        }
    }
}
