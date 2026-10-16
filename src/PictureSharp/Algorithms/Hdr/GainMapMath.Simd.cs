using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Algorithms.Hdr;

// Lane-wise forms of the scalar formulas with the same operations in the same order, so each lane equals the scalar
// result bit for bit. Map samples are read per lane.
internal static partial class GainMapMath
{
    /// <summary>
    /// <see cref="SampleIdw(ReadOnlySpan{Rgba32}, int, int, float, float, int)"/> of four positions for channels 0, 1 and 2 (only channel 0 when
    /// <paramref name="channels"/> is 1); the weights are shared by the channels as the scalar code computes them the same.
    /// </summary>
    public static void SampleIdw(ReadOnlySpan<Rgba32> map, int width, int height, Vector128<float> xMap, Vector128<float> yMap, int channels,
        out Vector128<float> c0, out Vector128<float> c1, out Vector128<float> c2)
    {
        Vector128<int> xLower = Vector128.Min(Vector128.ConvertToInt32(Vector128.Floor(xMap)), Vector128.Create(width - 1));
        Vector128<int> yLower = Vector128.Min(Vector128.ConvertToInt32(Vector128.Floor(yMap)), Vector128.Create(height - 1));
        Vector128<int> xUpper = Vector128.Min(xLower + Vector128<int>.One, Vector128.Create(width - 1));
        Vector128<int> yUpper = Vector128.Min(yLower + Vector128<int>.One, Vector128.Create(height - 1));
        Vector128<float> xl = Vector128.ConvertToSingle(xLower), yl = Vector128.ConvertToSingle(yLower);
        Vector128<float> xu = Vector128.ConvertToSingle(xUpper), yu = Vector128.ConvertToSingle(yUpper);

        Vector128<float> d1 = Distance(xMap - xl, yMap - yl);
        Vector128<float> d2 = Distance(xMap - xl, yMap - yu);
        Vector128<float> d3 = Distance(xMap - xu, yMap - yl);
        Vector128<float> d4 = Distance(xMap - xu, yMap - yu);
        Vector128<float> w1 = Vector128<float>.One / d1, w2 = Vector128<float>.One / d2, w3 = Vector128<float>.One / d3, w4 = Vector128<float>.One / d4;
        Vector128<float> total = w1 + w2 + w3 + w4;
        (w1, w2, w3, w4) = (w1 / total, w2 / total, w3 / total, w4 / total);
        Vector128<float> z1 = Vector128.Equals(d1, Vector128<float>.Zero), z2 = Vector128.Equals(d2, Vector128<float>.Zero);
        Vector128<float> z3 = Vector128.Equals(d3, Vector128<float>.Zero), z4 = Vector128.Equals(d4, Vector128<float>.Zero);

        Vector128<int> i1 = (yLower * width) + xLower, i2 = (yUpper * width) + xLower, i3 = (yLower * width) + xUpper, i4 = (yUpper * width) + xUpper;
        Corners(map, i1, out Vector128<float> e1r, out Vector128<float> e1g, out Vector128<float> e1b);
        Corners(map, i2, out Vector128<float> e2r, out Vector128<float> e2g, out Vector128<float> e2b);
        Corners(map, i3, out Vector128<float> e3r, out Vector128<float> e3g, out Vector128<float> e3b);
        Corners(map, i4, out Vector128<float> e4r, out Vector128<float> e4g, out Vector128<float> e4b);
        c0 = Idw(e1r, e2r, e3r, e4r);
        if (channels == 1)
        {
            c1 = c2 = c0;
            return;
        }

        c1 = Idw(e1g, e2g, e3g, e4g);
        c2 = Idw(e1b, e2b, e3b, e4b);

        // The first sample at distance 0 wins, in the scalar order; the fourth returns e2, as libultrahdr.
        Vector128<float> Idw(Vector128<float> e1, Vector128<float> e2, Vector128<float> e3, Vector128<float> e4)
        {
            Vector128<float> v = (e1 * w1) + (e2 * w2) + (e3 * w3) + (e4 * w4);
            v = Vector128.ConditionalSelect(z4, e2, v);
            v = Vector128.ConditionalSelect(z3, e3, v);
            v = Vector128.ConditionalSelect(z2, e2, v);
            return Vector128.ConditionalSelect(z1, e1, v);
        }
    }

    /// <summary><see cref="SampleBilinear(ReadOnlySpan{Rgba32}, int, int, float, float, int)"/> of channel 0 at four positions.</summary>
    public static Vector128<float> SampleBilinear(ReadOnlySpan<Rgba32> map, int width, int height, Vector128<float> x, Vector128<float> y)
    {
        x = Vector128.Clamp(x, Vector128<float>.Zero, Vector128.Create((float)(width - 1)));
        y = Vector128.Clamp(y, Vector128<float>.Zero, Vector128.Create((float)(height - 1)));
        Vector128<int> x0 = Vector128.ConvertToInt32(x), y0 = Vector128.ConvertToInt32(y);
        Vector128<int> x1 = Vector128.Min(x0 + Vector128<int>.One, Vector128.Create(width - 1));
        Vector128<int> y1 = Vector128.Min(y0 + Vector128<int>.One, Vector128.Create(height - 1));
        Vector128<float> fx = x - Vector128.ConvertToSingle(x0), fy = y - Vector128.ConvertToSingle(y0);
        Corners(map, (y0 * width) + x0, out Vector128<float> s00, out _, out _);
        Corners(map, (y0 * width) + x1, out Vector128<float> s01, out _, out _);
        Corners(map, (y1 * width) + x0, out Vector128<float> s10, out _, out _);
        Corners(map, (y1 * width) + x1, out Vector128<float> s11, out _, out _);
        return Lerp(Lerp(s00, s01, fx), Lerp(s10, s11, fx), fy);
    }

    /// <summary>Lane-wise <see cref="IsoGainFactor(float, float, float, float, float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> IsoGainFactor(Vector128<float> gain, float gainMapMin, float gainMapMax, float gamma, float weight)
    {
        if (gamma != 1f)
        {
            gain = DeterministicMath.Pow(gain, Vector128.Create(1f / gamma));
        }

        Vector128<float> logBoost = (Vector128.Create(gainMapMin) * (Vector128<float>.One - gain)) + (Vector128.Create(gainMapMax) * gain);
        return DeterministicMath.Exp2(logBoost * Vector128.Create(weight));
    }

    /// <summary>Lane-wise <see cref="Rec709ToLinear(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Rec709ToLinear(Vector128<float> v) =>
        Vector128.ConditionalSelect(Vector128.LessThan(v, Vector128.Create(0.081f)), v / Vector128.Create(4.5f),
            DeterministicMath.Pow((v + Vector128.Create(0.099f)) / Vector128.Create(1.099f), Vector128.Create(1f / 0.45f)));

    // R, G and B of the map samples at four indices, as v / 255.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Corners(ReadOnlySpan<Rgba32> map, Vector128<int> index, out Vector128<float> r, out Vector128<float> g, out Vector128<float> b)
    {
        Rgba32 p0 = map[index[0]], p1 = map[index[1]], p2 = map[index[2]], p3 = map[index[3]];
        Vector128<float> scale = Vector128.Create(255f);
        r = Vector128.Create((float)p0.R, p1.R, p2.R, p3.R) / scale;
        g = Vector128.Create((float)p0.G, p1.G, p2.G, p3.G) / scale;
        b = Vector128.Create((float)p0.B, p1.B, p2.B, p3.B) / scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Distance(Vector128<float> x, Vector128<float> y) => Vector128.Sqrt((x * x) + (y * y));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Lerp(Vector128<float> a, Vector128<float> b, Vector128<float> t) => a + ((b - a) * t);
}
