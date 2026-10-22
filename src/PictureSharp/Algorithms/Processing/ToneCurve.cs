using System.Drawing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// A tone curve through control points, interpolated with the shape-preserving piecewise cubic Hermite (PCHIP) of
/// Fritsch and Butland, as <c>scipy.interpolate.PchipInterpolator</c>; mirrored by tests/assets/photo_adjust_reference.py.
/// </summary>
internal static class ToneCurve
{
    /// <summary>
    /// Fills <paramref name="map"/> (256 entries) with the curve through <paramref name="points"/> (strictly increasing
    /// X, values 0–255), constant outside the first and last point, in double precision and rounded half up.
    /// </summary>
    public static void Fill(Span<byte> map, ReadOnlySpan<PointF> points)
    {
        int n = points.Length;
        Span<double> slopes = n <= 64 ? stackalloc double[n] : new double[n];
        Span<double> derivatives = n <= 64 ? stackalloc double[n] : new double[n];
        for (int k = 0; k < n - 1; k++)
        {
            slopes[k] = ((double)points[k + 1].Y - points[k].Y) / ((double)points[k + 1].X - points[k].X);
        }

        Derivatives(points, slopes[..(n - 1)], derivatives);
        int segment = 0;
        for (int x = 0; x < 256; x++)
        {
            double value;
            if (x <= points[0].X)
            {
                value = points[0].Y;
            }
            else if (x >= points[n - 1].X)
            {
                value = points[n - 1].Y;
            }
            else
            {
                while (x > points[segment + 1].X)
                {
                    segment++;
                }

                value = Hermite(points[segment], points[segment + 1], derivatives[segment], derivatives[segment + 1], x);
            }

            map[x] = (byte)(int)Math.Floor(Math.Clamp(value, 0.0, 255.0) + 0.5);
        }
    }

    // Fritsch and Butland (1984) interior derivatives with the end conditions of Moler, "Numerical Computing with
    // MATLAB", ch. 3 (pchip), which SciPy also uses; two points give a straight line.
    private static void Derivatives(ReadOnlySpan<PointF> points, ReadOnlySpan<double> m, Span<double> d)
    {
        int n = points.Length;
        if (n == 2)
        {
            d[0] = d[1] = m[0];
            return;
        }

        for (int k = 1; k < n - 1; k++)
        {
            double h0 = (double)points[k].X - points[k - 1].X, h1 = (double)points[k + 1].X - points[k].X;
            if (Math.Sign(m[k - 1]) != Math.Sign(m[k]) || m[k - 1] == 0 || m[k] == 0)
            {
                d[k] = 0;
                continue;
            }

            // Weighted harmonic mean of the neighbouring slopes.
            double w1 = (2 * h1) + h0, w2 = h1 + (2 * h0);
            d[k] = 1.0 / (((w1 / m[k - 1]) + (w2 / m[k])) / (w1 + w2));
        }

        d[0] = EdgeCase((double)points[1].X - points[0].X, (double)points[2].X - points[1].X, m[0], m[1]);
        d[n - 1] = EdgeCase((double)points[n - 1].X - points[n - 2].X, (double)points[n - 2].X - points[n - 3].X, m[n - 2], m[n - 3]);
    }

    // One-sided three-point estimate, set to 0 when its sign differs from the slope and limited to 3 · slope at a turn.
    private static double EdgeCase(double h0, double h1, double m0, double m1)
    {
        double d = (((2 * h0) + h1) * m0 - (h0 * m1)) / (h0 + h1);
        if (Math.Sign(d) != Math.Sign(m0))
        {
            return 0;
        }

        return Math.Sign(m0) != Math.Sign(m1) && Math.Abs(d) > 3 * Math.Abs(m0) ? 3 * m0 : d;
    }

    private static double Hermite(PointF p0, PointF p1, double d0, double d1, int x)
    {
        double h = (double)p1.X - p0.X;
        double t = (x - (double)p0.X) / h, t2 = t * t, t3 = t2 * t;
        return (((2 * t3) - (3 * t2) + 1) * p0.Y) + ((t3 - (2 * t2) + t) * h * d0) + (((-2 * t3) + (3 * t2)) * p1.Y)
            + ((t3 - t2) * h * d1);
    }
}
