using System.Drawing;
using System.Numerics;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// A tone curve through control points, interpolated with the shape-preserving piecewise cubic Hermite (PCHIP) of
/// Fritsch and Butland, as <c>scipy.interpolate.PchipInterpolator</c>; mirrored by tests/assets/photo_adjust_reference.py.
/// </summary>
internal static class ToneCurve
{
    /// <summary>
    /// Fills <paramref name="map"/> (max + 1 entries) with the curve through <paramref name="points"/> (strictly
    /// increasing X, normalized 0–1) in codes of 0–<paramref name="max"/>, constant outside the first and last point,
    /// in double precision and rounded half up. Points migrated from 8 bits as v / 255f are exactly v at max 255
    /// (<see cref="NormalizedLevel"/>).
    /// </summary>
    public static void Fill<T>(Span<T> map, ReadOnlySpan<PointF> points, int max) where T : unmanaged, IBinaryInteger<T>
    {
        int n = points.Length;
        Span<double> xs = n <= 64 ? stackalloc double[n] : new double[n];
        Span<double> ys = n <= 64 ? stackalloc double[n] : new double[n];
        for (int k = 0; k < n; k++)
        {
            xs[k] = NormalizedLevel.ToCodes(points[k].X, max);
            ys[k] = NormalizedLevel.ToCodes(points[k].Y, max);
        }

        Span<double> slopes = n <= 64 ? stackalloc double[n] : new double[n];
        Span<double> derivatives = n <= 64 ? stackalloc double[n] : new double[n];
        for (int k = 0; k < n - 1; k++)
        {
            slopes[k] = (ys[k + 1] - ys[k]) / (xs[k + 1] - xs[k]);
        }

        Derivatives(xs, slopes[..(n - 1)], derivatives);
        int segment = 0;
        for (int x = 0; x <= max; x++)
        {
            double value;
            if (x <= xs[0])
            {
                value = ys[0];
            }
            else if (x >= xs[n - 1])
            {
                value = ys[n - 1];
            }
            else
            {
                while (x > xs[segment + 1])
                {
                    segment++;
                }

                value = Hermite(xs[segment], ys[segment], xs[segment + 1], ys[segment + 1], derivatives[segment], derivatives[segment + 1], x);
            }

            map[x] = T.CreateTruncating((int)Math.Floor(Math.Clamp(value, 0.0, max) + 0.5));
        }
    }

    // Fritsch and Butland (1984) interior derivatives with the end conditions of Moler, "Numerical Computing with
    // MATLAB", ch. 3 (pchip), which SciPy also uses; two points give a straight line.
    private static void Derivatives(ReadOnlySpan<double> xs, ReadOnlySpan<double> m, Span<double> d)
    {
        int n = xs.Length;
        if (n == 2)
        {
            d[0] = d[1] = m[0];
            return;
        }

        for (int k = 1; k < n - 1; k++)
        {
            double h0 = xs[k] - xs[k - 1], h1 = xs[k + 1] - xs[k];
            if (Math.Sign(m[k - 1]) != Math.Sign(m[k]) || m[k - 1] == 0 || m[k] == 0)
            {
                d[k] = 0;
                continue;
            }

            // Weighted harmonic mean of the neighbouring slopes.
            double w1 = (2 * h1) + h0, w2 = h1 + (2 * h0);
            d[k] = 1.0 / (((w1 / m[k - 1]) + (w2 / m[k])) / (w1 + w2));
        }

        d[0] = EdgeCase(xs[1] - xs[0], xs[2] - xs[1], m[0], m[1]);
        d[n - 1] = EdgeCase(xs[n - 1] - xs[n - 2], xs[n - 2] - xs[n - 3], m[n - 2], m[n - 3]);
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

    private static double Hermite(double x0, double y0, double x1, double y1, double d0, double d1, int x)
    {
        double h = x1 - x0;
        double t = (x - x0) / h, t2 = t * t, t3 = t2 * t;
        return (((2 * t3) - (3 * t2) + 1) * y0) + ((t3 - (2 * t2) + t) * h * d0) + (((-2 * t3) + (3 * t2)) * y1)
            + ((t3 - t2) * h * d1);
    }
}
