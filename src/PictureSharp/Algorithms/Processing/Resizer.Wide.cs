using System.Buffers;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;
using PictureSharp.Primitives.Parallelism;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Resizer
{
    /// <summary>
    /// The Resize operation on one frame at its storage depth: a crop, a resample or neither, then optional padding
    /// with the pad color converted to the storage depth.
    /// </summary>
    public readonly struct ResizeFrame(ImageFrame frame, Rectangle? cropOnly, bool resample, int width, int height, ResizeOptions options,
        float x0, float y0, float x1, float y1, bool pad, int canvasWidth, int canvasHeight, Point offset, CancellationToken cancellationToken)
        : IPixelOperation<PixelBuffer>
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            TPixel[] source = frame.Buffer.As<TPixel>();
            TPixel[] pixels = cropOnly is { } rect ? Geometry.Crop(source, frame.Width, rect)
                : resample ? Resize(source, frame.Width, frame.Height, width, height, options.Filter, x0, y0, x1, y1, cancellationToken, options.LinearLight)
                : source;
            if (pad)
            {
                var canvas = GC.AllocateUninitializedArray<TPixel>(canvasWidth * canvasHeight);
                canvas.AsSpan().Fill(TPixel.FromRgba64(options.PadColor));
                Geometry.Place<TPixel>(pixels, width, height, canvas, canvasWidth, canvasHeight, offset.X, offset.Y);
                pixels = canvas;
            }

            return PixelBuffer.From(pixels);
        }
    }

    /// <summary>The frame resampled from (0, 0)–(<paramref name="x1"/>, <paramref name="y1"/>) to a new size, at its storage depth.</summary>
    public readonly struct ScaleFrame(ImageFrame frame, int width, int height, ResamplingFilter filter, float x1, float y1,
        CancellationToken cancellationToken, int maxDegreeOfParallelism) : IPixelOperation<PixelBuffer>
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel> =>
            PixelBuffer.From(Resize(frame.Buffer.As<TPixel>(), frame.Width, frame.Height, width, height, filter, 0, 0, x1, y1,
                cancellationToken, maxDegreeOfParallelism: maxDegreeOfParallelism));
    }

    /// <summary>
    /// <see cref="Resize(Rgba32[], int, int, int, int, ResamplingFilter, float, float, float, float, CancellationToken, bool, int)"/>
    /// at the storage depth: the 8-bit port for <see cref="Rgba32"/>, otherwise <see cref="ResampleWide{TPixel, TTransfer}"/>.
    /// </summary>
    public static TPixel[] Resize<TPixel>(TPixel[] src, int srcWidth, int srcHeight, int width, int height,
        ResamplingFilter filter, float x0, float y0, float x1, float y1, CancellationToken cancellationToken = default, bool linearLight = false,
        int maxDegreeOfParallelism = -1)
        where TPixel : unmanaged, IPixelOps<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            return Unsafe.As<TPixel[]>(Resize(Unsafe.As<Rgba32[]>(src), srcWidth, srcHeight, width, height, filter, x0, y0, x1, y1,
                cancellationToken, linearLight, maxDegreeOfParallelism));
        }

        var dst = GC.AllocateUninitializedArray<TPixel>(checked(width * height));
        ParallelOptions parallel = Parallelism.Options(maxDegreeOfParallelism, cancellationToken);
        if (width == srcWidth && height == srcHeight && x0 == 0 && y0 == 0 && x1 == srcWidth && y1 == srcHeight)
        {
            src.AsSpan().CopyTo(dst);
        }
        else if (filter == ResamplingFilter.Nearest)
        {
            ScaleNearest(src, srcWidth, srcHeight, dst, width, height, x0, y0, x1, y1, cancellationToken);
        }
        else if (linearLight)
        {
            ResampleWide<TPixel, LinearUnit>(src, srcWidth, srcHeight, dst, width, height, filter, x0, y0, x1, y1, parallel);
        }
        else
        {
            ResampleWide<TPixel, EncodedUnit>(src, srcWidth, srcHeight, dst, width, height, filter, x0, y0, x1, y1, parallel);
        }

        return dst;
    }

    /// <summary>
    /// The geometry and unquantized weights of <see cref="ResampleInner"/> on premultiplied working values: sums in
    /// double, passes stored as float and clipped as the 8-bit passes clip, one rounding at the output.
    /// </summary>
    private static void ResampleWide<TPixel, TTransfer>(TPixel[] src, int srcWidth, int srcHeight, TPixel[] dst, int width, int height,
        ResamplingFilter filter, float x0, float y0, float x1, float y1, ParallelOptions parallel)
        where TPixel : unmanaged, IPixelOps<TPixel>
        where TTransfer : struct, IUnitTransfer
    {
        bool needHorizontal = width != srcWidth || x0 != 0 || x1 != width;
        bool needVertical = height != srcHeight || y0 != 0 || y1 != height;
        (int ksizeH, int[] boundsH, double[] kH) = PrecomputeWeights(srcWidth, x0, x1, width, filter);
        (int ksizeV, int[] boundsV, double[] kV) = PrecomputeWeights(srcHeight, y0, y1, height, filter);

        // Banded as ResampleLinear: rows shared by neighbouring bands are recomputed identically.
        int bands = (height + LinearBandRows - 1) / LinearBandRows;
        long workPerBand = (long)Math.Min(LinearBandRows, height) * ((width * (ksizeV + 1)) + (srcWidth * (srcHeight / Math.Max(1, height))));
        ForRows(bands, workPerBand, parallel, band =>
        {
            int first = band * LinearBandRows, last = Math.Min(height, first + LinearBandRows);
            int sourceStart = needVertical ? boundsV[first * 2] : first;
            int sourceEnd = needVertical ? boundsV[(last - 1) * 2] + boundsV[((last - 1) * 2) + 1] : last;
            int rows = sourceEnd - sourceStart;
            Vector4[] horizontal = ArrayPool<Vector4>.Shared.Rent(rows * width);
            Vector4[]? line = needHorizontal ? ArrayPool<Vector4>.Shared.Rent(srcWidth) : null;
            double[]? sums = needVertical ? ArrayPool<double>.Shared.Rent(width * 4) : null;
            Vector4[]? vertical = needVertical ? ArrayPool<Vector4>.Shared.Rent(width) : null;
            try
            {
                for (int r = 0; r < rows; r++)
                {
                    ReadOnlySpan<TPixel> sourceRow = src.AsSpan((sourceStart + r) * srcWidth, srcWidth);
                    Span<Vector4> outRow = horizontal.AsSpan(r * width, width);
                    if (line is null)
                    {
                        UnitRows.Premultiply<TPixel, TTransfer>(sourceRow, outRow);
                        continue;
                    }

                    Span<Vector4> inRow = line.AsSpan(0, srcWidth);
                    UnitRows.Premultiply<TPixel, TTransfer>(sourceRow, inRow);
                    for (int xx = 0; xx < width; xx++)
                    {
                        ReadOnlySpan<double> w = kH.AsSpan(xx * ksizeH, boundsH[(xx * 2) + 1]);
                        ReadOnlySpan<Vector4> px = inRow.Slice(boundsH[xx * 2], w.Length);
                        double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                        for (int x = 0; x < w.Length; x++)
                        {
                            double c = w[x];
                            Vector4 p = px[x];
                            s0 += p.X * c;
                            s1 += p.Y * c;
                            s2 += p.Z * c;
                            s3 += p.W * c;
                        }

                        outRow[xx] = TTransfer.Intermediate(new Vector4((float)s0, (float)s1, (float)s2, (float)s3));
                    }
                }

                for (int yy = first; yy < last; yy++)
                {
                    Span<TPixel> target = dst.AsSpan(yy * width, width);
                    if (sums is null)
                    {
                        UnitRows.Unpremultiply<TPixel, TTransfer>(horizontal.AsSpan((yy - sourceStart) * width, width), target);
                        continue;
                    }

                    ReadOnlySpan<double> w = kV.AsSpan(yy * ksizeV, boundsV[(yy * 2) + 1]);
                    int ymin = boundsV[yy * 2] - sourceStart;
                    Span<double> acc = sums.AsSpan(0, width * 4);
                    acc.Clear();
                    for (int y = 0; y < w.Length; y++)
                    {
                        ReadOnlySpan<Vector4> inRow = horizontal.AsSpan((ymin + y) * width, width);
                        double c = w[y];
                        for (int x = 0; x < inRow.Length; x++)
                        {
                            Vector4 p = inRow[x];
                            acc[x * 4] += p.X * c;
                            acc[(x * 4) + 1] += p.Y * c;
                            acc[(x * 4) + 2] += p.Z * c;
                            acc[(x * 4) + 3] += p.W * c;
                        }
                    }

                    Span<Vector4> outRow = vertical.AsSpan(0, width);
                    for (int x = 0; x < outRow.Length; x++)
                    {
                        outRow[x] = TTransfer.Intermediate(new Vector4((float)acc[x * 4], (float)acc[(x * 4) + 1], (float)acc[(x * 4) + 2], (float)acc[(x * 4) + 3]));
                    }

                    UnitRows.Unpremultiply<TPixel, TTransfer>(outRow, target);
                }
            }
            finally
            {
                ArrayPool<Vector4>.Shared.Return(horizontal);
                if (line is not null) ArrayPool<Vector4>.Shared.Return(line);
                if (sums is not null) ArrayPool<double>.Shared.Return(sums);
                if (vertical is not null) ArrayPool<Vector4>.Shared.Return(vertical!);
            }
        });
    }
}
