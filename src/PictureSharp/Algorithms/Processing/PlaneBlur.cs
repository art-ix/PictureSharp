using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Box and approximated Gaussian filters on one float plane (the first width × height items of an array). Each box
/// averages the part of its window that lies inside the plane, so edges are not darkened; sums are kept in double
/// precision. Rows, and columns in the vertical pass, run in parallel without changing the result.
/// </summary>
internal static class PlaneBlur
{
    private const int ColumnStrip = 256;

    /// <summary>
    /// The mean of each <c>(2r + 1)²</c> window of <paramref name="source"/>, clipped to the plane, written to
    /// <paramref name="destination"/> (which may be <paramref name="source"/>); <paramref name="scratch"/> holds the
    /// horizontal pass.
    /// </summary>
    public static void Box(float[] source, float[] destination, float[] scratch, int width, int height, int radius,
        CancellationToken cancellationToken)
    {
        int length = width * height;
        if (radius == 0)
        {
            source.AsSpan(0, length).CopyTo(destination);
            return;
        }

        int rows = Math.Max(1, (1 << 16) / width);
        Parallelism.ForBands(height, rows, length, cancellationToken, (top, bottom) =>
        {
            for (int y = top; y < bottom; y++)
            {
                HorizontalMean(source.AsSpan(y * width, width), scratch.AsSpan(y * width, width), radius);
            }
        });
        Parallelism.ForBands(width, ColumnStrip, length, cancellationToken, (left, right) =>
            VerticalMean(scratch, destination, width, height, left, right, radius, cancellationToken));
    }

    /// <summary>
    /// A Gaussian of standard deviation <paramref name="sigma"/> approximated by three box passes whose widths follow
    /// <see cref="BoxRadii"/>; the result replaces <paramref name="plane"/>.
    /// </summary>
    public static void Gaussian(float[] plane, float[] scratch, int width, int height, float sigma, CancellationToken cancellationToken)
    {
        Span<int> radii = stackalloc int[3];
        BoxRadii(sigma, radii);
        foreach (int radius in radii)
        {
            Box(plane, plane, scratch, width, height, radius, cancellationToken);
        }
    }

    /// <summary>
    /// Radii of n box filters whose sequence has variance closest to <paramref name="sigma"/>² (widths wl or wl + 2,
    /// wl the largest odd width not above the ideal one; Kovesi, "Fast almost-Gaussian filtering", 2010).
    /// </summary>
    public static void BoxRadii(float sigma, Span<int> radii)
    {
        int n = radii.Length;
        double variance = 12.0 * sigma * sigma;
        int lower = (int)Math.Floor(Math.Sqrt((variance / n) + 1));
        if (lower % 2 == 0)
        {
            lower--;
        }

        int count = (int)Math.Floor(((variance - (n * lower * lower) - (4 * n * lower) - (3 * n)) / ((-4 * lower) - 4)) + 0.5);
        for (int i = 0; i < n; i++)
        {
            radii[i] = ((i < count ? lower : lower + 2) - 1) / 2;
        }
    }

    private static void HorizontalMean(ReadOnlySpan<float> row, Span<float> output, int radius)
    {
        int last = row.Length - 1;
        double sum = 0;
        for (int x = 0; x <= Math.Min(radius, last); x++)
        {
            sum += row[x];
        }

        for (int x = 0; x <= last; x++)
        {
            int left = Math.Max(0, x - radius), right = Math.Min(last, x + radius);
            output[x] = (float)(sum / (right - left + 1));
            if (x + radius + 1 <= last)
            {
                sum += row[x + radius + 1];
            }

            if (x - radius >= 0)
            {
                sum -= row[x - radius];
            }
        }
    }

    // Columns [left, right) of the vertical pass; each column's sums are independent of the others.
    private static void VerticalMean(float[] source, float[] destination, int width, int height, int left, int right, int radius,
        CancellationToken cancellationToken)
    {
        int strip = right - left;
        Span<double> sum = stackalloc double[ColumnStrip];
        sum = sum[..strip];
        sum.Clear();
        int last = height - 1;
        for (int y = 0; y <= Math.Min(radius, last); y++)
        {
            Add(sum, source.AsSpan((y * width) + left, strip), 1);
        }

        for (int y = 0; y <= last; y++)
        {
            if ((y & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int top = Math.Max(0, y - radius), bottom = Math.Min(last, y + radius);
            double count = bottom - top + 1;
            Span<float> row = destination.AsSpan((y * width) + left, strip);
            for (int x = 0; x < strip; x++)
            {
                row[x] = (float)(sum[x] / count);
            }

            if (y + radius + 1 <= last)
            {
                Add(sum, source.AsSpan(((y + radius + 1) * width) + left, strip), 1);
            }

            if (y - radius >= 0)
            {
                Add(sum, source.AsSpan(((y - radius) * width) + left, strip), -1);
            }
        }
    }

    private static void Add(Span<double> sum, ReadOnlySpan<float> row, int sign)
    {
        for (int x = 0; x < sum.Length; x++)
        {
            sum[x] += sign * row[x];
        }
    }
}
