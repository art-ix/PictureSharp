using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PictureSharp.Processing;

/// <summary>
/// Two-pass separable resampling with precomputed fixed-point weights on premultiplied alpha.
/// Follows Pillow's Resample.c and Image.resize (RGBA is converted to RGBa and back), so results
/// match Pillow pixel for pixel.
/// </summary>
internal static partial class Resizer
{
    // port: Pillow src/libImaging/Resample.c PRECISION_BITS
    private const int PrecisionBits = 32 - 8 - 2;

    // Below this many multiply-adds a pass runs on the calling thread.
    private const long ParallelThreshold = 1 << 18;

    /// <summary>
    /// Resamples the area (<paramref name="x0"/>, <paramref name="y0"/>)–(<paramref name="x1"/>, <paramref name="y1"/>)
    /// of a <paramref name="srcWidth"/>×<paramref name="srcHeight"/> image to <paramref name="width"/>×<paramref name="height"/>.
    /// The box is in source pixels and may be fractional, like Pillow's <c>box</c> argument.
    /// </summary>
    public static Rgba32[] Resize(Rgba32[] src, int srcWidth, int srcHeight, int width, int height,
        ResamplingFilter filter, float x0, float y0, float x1, float y1, CancellationToken cancellationToken = default)
    {
        var dst = GC.AllocateUninitializedArray<Rgba32>(checked(width * height));
        if (width == srcWidth && height == srcHeight && x0 == 0 && y0 == 0 && x1 == srcWidth && y1 == srcHeight)
        {
            src.AsSpan().CopyTo(dst);
        }
        else if (filter == ResamplingFilter.Nearest)
        {
            ScaleNearest(src, srcWidth, srcHeight, dst, width, height, x0, y0, x1, y1, cancellationToken);
        }
        else
        {
            ResampleInner(src, srcWidth, srcHeight, dst, width, height, filter, x0, y0, x1, y1, cancellationToken);
        }

        return dst;
    }

    /// <summary>
    /// The size <see cref="ImageProcessingContext.Thumbnail(int, int, ResamplingFilter)"/> gives an image (0 for one side
    /// of the box means no limit on it); null when the image keeps its size.
    /// </summary>
    public static (int Width, int Height)? ThumbnailSize(int srcWidth, int srcHeight, int maxWidth, int maxHeight)
    {
        if (maxWidth == 0)
        {
            maxWidth = Math.Max(1, (int)Math.Round((double)srcWidth / srcHeight * maxHeight));
        }
        else if (maxHeight == 0)
        {
            maxHeight = Math.Max(1, (int)Math.Round((double)srcHeight / srcWidth * maxWidth));
        }

        (int w, int h) = FitSize(srcWidth, srcHeight, maxWidth, maxHeight);
        return w > srcWidth || h > srcHeight || (w == srcWidth && h == srcHeight) ? null : (w, h);
    }

    /// <summary>The size that fits inside <paramref name="maxWidth"/>×<paramref name="maxHeight"/> keeping the aspect ratio.</summary>
    // port: Pillow src/PIL/ImageOps.py contain (Python round() rounds half to even, as Math.Round does)
    public static (int Width, int Height) FitSize(int srcWidth, int srcHeight, int maxWidth, int maxHeight)
    {
        double imRatio = (double)srcWidth / srcHeight;
        double destRatio = (double)maxWidth / maxHeight;
        if (imRatio == destRatio)
        {
            return (maxWidth, maxHeight);
        }

        if (imRatio > destRatio)
        {
            int h = (int)Math.Round((double)srcHeight / srcWidth * maxWidth);
            return (maxWidth, Math.Max(1, h));
        }

        int w = (int)Math.Round((double)srcWidth / srcHeight * maxHeight);
        return (Math.Max(1, w), maxHeight);
    }

    /// <summary>The smallest size that covers <paramref name="minWidth"/>×<paramref name="minHeight"/> keeping the aspect ratio.</summary>
    // Mirror of FitSize: the side that FitSize shrinks is the one that grows here.
    public static (int Width, int Height) CoverSize(int srcWidth, int srcHeight, int minWidth, int minHeight)
    {
        double imRatio = (double)srcWidth / srcHeight;
        double destRatio = (double)minWidth / minHeight;
        if (imRatio == destRatio)
        {
            return (minWidth, minHeight);
        }

        if (imRatio > destRatio)
        {
            int w = (int)Math.Round((double)srcWidth / srcHeight * minHeight);
            return (Math.Max(1, w), minHeight);
        }

        int h = (int)Math.Round((double)srcHeight / srcWidth * minWidth);
        return (minWidth, Math.Max(1, h));
    }

    /// <summary>
    /// The source box with the aspect ratio of <paramref name="width"/>×<paramref name="height"/>, placed by
    /// <paramref name="centeringX"/>/<paramref name="centeringY"/> (0 = left/top, 1 = right/bottom).
    /// </summary>
    // port: Pillow src/PIL/ImageOps.py fit (bleed 0)
    public static (float X0, float Y0, float X1, float Y1) FillBox(int srcWidth, int srcHeight, int width, int height,
        double centeringX = 0.5, double centeringY = 0.5)
    {
        double liveRatio = (double)srcWidth / srcHeight;
        double outputRatio = (double)width / height;
        double cropWidth, cropHeight;
        if (liveRatio == outputRatio)
        {
            cropWidth = srcWidth;
            cropHeight = srcHeight;
        }
        else if (liveRatio >= outputRatio)
        {
            cropWidth = outputRatio * srcHeight;
            cropHeight = srcHeight;
        }
        else
        {
            cropWidth = srcWidth;
            cropHeight = srcWidth / outputRatio;
        }

        double left = (srcWidth - cropWidth) * centeringX;
        double top = (srcHeight - cropHeight) * centeringY;
        return ((float)left, (float)top, (float)(left + cropWidth), (float)(top + cropHeight));
    }

    // port: Pillow src/libImaging/Geometry.c ImagingScaleAffine (via _imaging.c _resize for NEAREST)
    private static void ScaleNearest(Rgba32[] src, int srcWidth, int srcHeight, Rgba32[] dst, int width, int height,
        float x0, float y0, float x1, float y1, CancellationToken cancellationToken)
    {
        double a0 = (double)(x1 - x0) / width;
        double a4 = (double)(y1 - y0) / height;
        int[] xintab = ArrayPool<int>.Shared.Rent(width);
        try
        {
            int xmin = width;
            int xmax = 0;
            double xo = x0 + (a0 * 0.5);
            for (int x = 0; x < width; x++)
            {
                int xin = Coord(xo);
                if (xin >= 0 && xin < srcWidth)
                {
                    xmax = x + 1;
                    if (x < xmin)
                    {
                        xmin = x;
                    }

                    xintab[x] = xin;
                }

                xo += a0;
            }

            double yo = y0 + (a4 * 0.5);
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<Rgba32> row = dst.AsSpan(y * width, width);
                row.Clear();
                int yi = Coord(yo);
                if (yi >= 0 && yi < srcHeight)
                {
                    ReadOnlySpan<Rgba32> inRow = src.AsSpan(yi * srcWidth, srcWidth);
                    for (int x = xmin; x < xmax; x++)
                    {
                        row[x] = inRow[xintab[x]];
                    }
                }

                yo += a4;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(xintab);
        }

        static int Coord(double v) => v < 0.0 ? -1 : (int)v;
    }

    // port: Pillow src/libImaging/Resample.c ImagingResampleInner
    private static void ResampleInner(Rgba32[] src, int srcWidth, int srcHeight, Rgba32[] dst, int width, int height,
        ResamplingFilter filter, float x0, float y0, float x1, float y1, CancellationToken cancellationToken)
    {
        bool needHorizontal = width != srcWidth || x0 != 0 || x1 != width;
        bool needVertical = height != srcHeight || y0 != 0 || y1 != height;

        (int ksizeH, int[] boundsH, int[] kkH) = PrecomputeCoeffs(srcWidth, x0, x1, width, filter);
        (int ksizeV, int[] boundsV, int[] kkV) = PrecomputeCoeffs(srcHeight, y0, y1, height, filter);

        int yboxFirst = boundsV[0];
        int yboxLast = boundsV[(height * 2) - 2] + boundsV[(height * 2) - 1];

        // Image.resize works on RGBa: premultiply only the source rows the passes read.
        int firstRow = needHorizontal ? yboxFirst : 0;
        int rowCount = needHorizontal ? yboxLast - yboxFirst : srcHeight;
        // Premultiplying leaves opaque pixels unchanged, so an opaque source is read in place.
        int regionStart = firstRow * srcWidth;
        int regionLength = rowCount * srcWidth;
        bool opaque = Premultiplied.IsOpaque(src.AsSpan(regionStart, regionLength));
        Rgba32[]? premul = opaque ? null : ArrayPool<Rgba32>.Shared.Rent(regionLength);
        Rgba32[]? temp = null;
        try
        {
            Rgba32[] source = src;
            int sourceStart = regionStart;
            if (premul is not null)
            {
                const int band = 64;
                ForRows((rowCount + band - 1) / band, (long)band * srcWidth, cancellationToken, b =>
                {
                    int start = b * band * srcWidth;
                    int length = Math.Min(band * srcWidth, regionLength - start);
                    Premultiplied.FromStraight(src.AsSpan(regionStart + start, length), premul.AsSpan(start, length));
                });
                source = premul;
                sourceStart = 0;
            }

            Rgba32[] current = source;
            int currentWidth = srcWidth;
            if (needHorizontal)
            {
                for (int i = 0; i < height; i++)
                {
                    boundsV[i * 2] -= yboxFirst;
                }

                Rgba32[] target = needVertical ? (temp = ArrayPool<Rgba32>.Shared.Rent(rowCount * width)) : dst;
                ResampleHorizontal(source, sourceStart, srcWidth, target, width, rowCount, ksizeH, boundsH, kkH, cancellationToken);
                current = target;
                currentWidth = width;
            }

            if (needVertical)
            {
                ResampleVertical(current, currentWidth, dst, height, ksizeV, boundsV, kkV, cancellationToken);
            }

            Premultiplied.ToStraight(dst);
        }
        finally
        {
            if (premul is not null)
            {
                ArrayPool<Rgba32>.Shared.Return(premul);
            }

            if (temp is not null)
            {
                ArrayPool<Rgba32>.Shared.Return(temp);
            }
        }
    }

    // port: Pillow src/libImaging/Resample.c precompute_coeffs + normalize_coeffs_8bpc
    private static (int Ksize, int[] Bounds, int[] Kk) PrecomputeCoeffs(int inSize, float in0, float in1, int outSize, ResamplingFilter filter)
    {
        double filterSupport = filter switch
        {
            ResamplingFilter.Box => 0.5,
            ResamplingFilter.Bilinear or ResamplingFilter.Hamming => 1.0,
            ResamplingFilter.Bicubic => 2.0,
            _ => 3.0,
        };

        double scale = (double)(in1 - in0) / outSize;
        double filterscale = scale < 1.0 ? 1.0 : scale;
        double support = filterSupport * filterscale;
        int ksize = ((int)Math.Ceiling(support) * 2) + 1;
        if ((long)outSize * ksize > Array.MaxLength)
        {
            throw new OutOfMemoryException("Resampling coefficients do not fit in memory.");
        }

        var bounds = new int[outSize * 2];
        var kk = new int[outSize * ksize];
        double[] k = ArrayPool<double>.Shared.Rent(ksize);
        try
        {
            double ss = 1.0 / filterscale;
            for (int xx = 0; xx < outSize; xx++)
            {
                double center = in0 + ((xx + 0.5) * scale);
                double ww = 0.0;
                int xmin = (int)(center - support + 0.5);
                if (xmin < 0)
                {
                    xmin = 0;
                }

                int xmax = (int)(center + support + 0.5);
                if (xmax > inSize)
                {
                    xmax = inSize;
                }

                xmax -= xmin;
                for (int x = 0; x < xmax; x++)
                {
                    double w = Filter(filter, (x + xmin - center + 0.5) * ss);
                    k[x] = w;
                    ww += w;
                }

                Span<int> row = kk.AsSpan(xx * ksize, ksize);
                for (int x = 0; x < xmax; x++)
                {
                    double v = ww != 0.0 ? k[x] / ww : k[x];
                    row[x] = v < 0 ? (int)(-0.5 + (v * (1 << PrecisionBits))) : (int)(0.5 + (v * (1 << PrecisionBits)));
                }

                bounds[xx * 2] = xmin;
                bounds[(xx * 2) + 1] = xmax;
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(k);
        }

        return (ksize, bounds, kk);
    }

    private static double Filter(ResamplingFilter filter, double x) => filter switch
    {
        ResamplingFilter.Box => BoxFilter(x),
        ResamplingFilter.Bilinear => BilinearFilter(x),
        ResamplingFilter.Hamming => HammingFilter(x),
        ResamplingFilter.Bicubic => BicubicFilter(x),
        _ => LanczosFilter(x),
    };

    // port: Pillow src/libImaging/Resample.c box_filter
    private static double BoxFilter(double x) => x > -0.5 && x <= 0.5 ? 1.0 : 0.0;

    // port: Pillow src/libImaging/Resample.c hamming_filter
    private static double HammingFilter(double x)
    {
        if (x < 0.0)
        {
            x = -x;
        }

        if (x == 0.0)
        {
            return 1.0;
        }

        if (x >= 1.0)
        {
            return 0.0;
        }

        x *= Math.PI;
        return Math.Sin(x) / x * (0.54 + (0.46 * Math.Cos(x)));
    }

    // port: Pillow src/libImaging/Resample.c bilinear_filter
    private static double BilinearFilter(double x)
    {
        if (x < 0.0)
        {
            x = -x;
        }

        return x < 1.0 ? 1.0 - x : 0.0;
    }

    // port: Pillow src/libImaging/Resample.c bicubic_filter (a = -0.5)
    private static double BicubicFilter(double x)
    {
        const double a = -0.5;
        if (x < 0.0)
        {
            x = -x;
        }

        if (x < 1.0)
        {
            return ((((a + 2.0) * x) - (a + 3.0)) * x * x) + 1;
        }

        if (x < 2.0)
        {
            return ((((((x - 5) * x) + 8) * x) - 4) * a);
        }

        return 0.0;
    }

    // port: Pillow src/libImaging/Resample.c sinc_filter
    private static double SincFilter(double x)
    {
        if (x == 0.0)
        {
            return 1.0;
        }

        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    // port: Pillow src/libImaging/Resample.c lanczos_filter
    private static double LanczosFilter(double x) =>
        -3.0 <= x && x < 3.0 ? SincFilter(x) * SincFilter(x / 3) : 0.0;

    // port: Pillow src/libImaging/Resample.c ImagingResampleHorizontal_8bpc (4 bands)
    private static void ResampleHorizontal(Rgba32[] src, int srcStart, int srcWidth, Rgba32[] dst, int width, int rows,
        int ksize, int[] bounds, int[] kk, CancellationToken cancellationToken)
    {
        ForRows(rows, (long)width * ksize, cancellationToken, y =>
        {
            ReadOnlySpan<byte> inRow = MemoryMarshal.AsBytes(src.AsSpan(srcStart + (y * srcWidth), srcWidth));
            Span<Rgba32> outRow = dst.AsSpan(y * width, width);
            int done = HorizontalRowSimd(inRow, outRow, ksize, bounds, kk);
            HorizontalRowScalar(inRow, outRow, ksize, bounds, kk, done);
        });
    }

    /// <summary>Output pixels from <paramref name="start"/> on; the vector path is in <c>Resizer.Simd.cs</c>.</summary>
    internal static void HorizontalRowScalar(ReadOnlySpan<byte> inRow, Span<Rgba32> outRow, int ksize, int[] bounds, int[] kk, int start)
    {
        for (int xx = start; xx < outRow.Length; xx++)
        {
            int xmin = bounds[xx * 2];
            int xmax = bounds[(xx * 2) + 1];
            ReadOnlySpan<int> k = kk.AsSpan(xx * ksize, xmax);
            ReadOnlySpan<byte> px = inRow.Slice(xmin * 4, xmax * 4);
            int ss0 = 1 << (PrecisionBits - 1);
            int ss1 = ss0, ss2 = ss0, ss3 = ss0;
            for (int x = 0; x < k.Length; x++)
            {
                int c = k[x];
                ss0 += px[x * 4] * c;
                ss1 += px[(x * 4) + 1] * c;
                ss2 += px[(x * 4) + 2] * c;
                ss3 += px[(x * 4) + 3] * c;
            }

            outRow[xx] = new Rgba32(Clip8(ss0), Clip8(ss1), Clip8(ss2), Clip8(ss3));
        }
    }

    // port: Pillow src/libImaging/Resample.c ImagingResampleVertical_8bpc (4 bands). Integer sums are order
    // independent, so accumulating per source row or per output byte gives the same result.
    private static void ResampleVertical(Rgba32[] src, int width, Rgba32[] dst, int height,
        int ksize, int[] bounds, int[] kk, CancellationToken cancellationToken)
    {
        ForRows(height, (long)width * ksize, cancellationToken, yy =>
        {
            int ymin = bounds[yy * 2];
            int ymax = bounds[(yy * 2) + 1];
            ReadOnlySpan<int> k = kk.AsSpan(yy * ksize, ymax);
            ReadOnlySpan<byte> rows = MemoryMarshal.AsBytes(src.AsSpan(ymin * width, ymax * width));
            Span<byte> outRow = MemoryMarshal.AsBytes(dst.AsSpan(yy * width, width));
            int done = VerticalRowSimd(rows, width * 4, k, outRow);
            VerticalRowScalar(rows, width * 4, k, outRow, done);
        });
    }

    /// <summary>Output bytes from <paramref name="start"/> on; <paramref name="rows"/> holds the source rows the taps cover.</summary>
    internal static void VerticalRowScalar(ReadOnlySpan<byte> rows, int stride, ReadOnlySpan<int> k, Span<byte> outRow, int start)
    {
        int n = outRow.Length - start;
        if (n <= 0)
        {
            return;
        }

        int[] acc = ArrayPool<int>.Shared.Rent(n);
        try
        {
            Span<int> sums = acc.AsSpan(0, n);
            sums.Fill(1 << (PrecisionBits - 1));
            for (int y = 0; y < k.Length; y++)
            {
                int c = k[y];
                ReadOnlySpan<byte> inRow = rows.Slice((y * stride) + start, n);
                for (int i = 0; i < sums.Length; i++)
                {
                    sums[i] += inRow[i] * c;
                }
            }

            for (int i = 0; i < sums.Length; i++)
            {
                outRow[start + i] = Clip8(sums[i]);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(acc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip8(int v)
    {
        v >>= PrecisionBits;
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    private static void ForRows(int rows, long workPerRow, CancellationToken cancellationToken, Action<int> body)
    {
        if (rows * workPerRow < ParallelThreshold || Environment.ProcessorCount == 1)
        {
            for (int y = 0; y < rows; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                body(y);
            }
        }
        else
        {
            Parallel.For(0, rows, new ParallelOptions { CancellationToken = cancellationToken }, body);
        }
    }
}
