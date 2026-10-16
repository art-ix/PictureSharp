using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Algorithms.Hdr;

// 256-bit forms of GainMapMath.Simd.cs for AVX2: the same lane-wise operations on eight positions.
internal static partial class GainMapMath
{
    /// <summary>
    /// <see cref="SampleIdw(ReadOnlySpan{Rgba32}, int, int, float, float, int)"/> of eight positions for channels 0, 1 and 2 (only channel 0 when
    /// <paramref name="channels"/> is 1); the weights are shared by the channels as the scalar code computes them the same.
    /// </summary>
    public static void SampleIdw(ReadOnlySpan<Rgba32> map, int width, int height, Vector256<float> xMap, Vector256<float> yMap, int channels,
        out Vector256<float> c0, out Vector256<float> c1, out Vector256<float> c2)
    {
        Vector256<int> xLower = Vector256.Min(Vector256.ConvertToInt32(Vector256.Floor(xMap)), Vector256.Create(width - 1));
        Vector256<int> yLower = Vector256.Min(Vector256.ConvertToInt32(Vector256.Floor(yMap)), Vector256.Create(height - 1));
        Vector256<int> xUpper = Vector256.Min(xLower + Vector256<int>.One, Vector256.Create(width - 1));
        Vector256<int> yUpper = Vector256.Min(yLower + Vector256<int>.One, Vector256.Create(height - 1));
        Vector256<float> xl = Vector256.ConvertToSingle(xLower), yl = Vector256.ConvertToSingle(yLower);
        Vector256<float> xu = Vector256.ConvertToSingle(xUpper), yu = Vector256.ConvertToSingle(yUpper);

        Vector256<float> d1 = Distance(xMap - xl, yMap - yl);
        Vector256<float> d2 = Distance(xMap - xl, yMap - yu);
        Vector256<float> d3 = Distance(xMap - xu, yMap - yl);
        Vector256<float> d4 = Distance(xMap - xu, yMap - yu);
        Vector256<float> w1 = Vector256<float>.One / d1, w2 = Vector256<float>.One / d2, w3 = Vector256<float>.One / d3, w4 = Vector256<float>.One / d4;
        Vector256<float> total = w1 + w2 + w3 + w4;
        (w1, w2, w3, w4) = (w1 / total, w2 / total, w3 / total, w4 / total);
        Vector256<float> z1 = Vector256.Equals(d1, Vector256<float>.Zero), z2 = Vector256.Equals(d2, Vector256<float>.Zero);
        Vector256<float> z3 = Vector256.Equals(d3, Vector256<float>.Zero), z4 = Vector256.Equals(d4, Vector256<float>.Zero);

        Vector256<int> i1 = (yLower * width) + xLower, i2 = (yUpper * width) + xLower, i3 = (yLower * width) + xUpper, i4 = (yUpper * width) + xUpper;
        Corners(map, i1, out Vector256<float> e1r, out Vector256<float> e1g, out Vector256<float> e1b);
        Corners(map, i2, out Vector256<float> e2r, out Vector256<float> e2g, out Vector256<float> e2b);
        Corners(map, i3, out Vector256<float> e3r, out Vector256<float> e3g, out Vector256<float> e3b);
        Corners(map, i4, out Vector256<float> e4r, out Vector256<float> e4g, out Vector256<float> e4b);
        c0 = Idw(e1r, e2r, e3r, e4r);
        if (channels == 1)
        {
            c1 = c2 = c0;
            return;
        }

        c1 = Idw(e1g, e2g, e3g, e4g);
        c2 = Idw(e1b, e2b, e3b, e4b);

        // The first sample at distance 0 wins, in the scalar order; the fourth returns e2, as libultrahdr.
        Vector256<float> Idw(Vector256<float> e1, Vector256<float> e2, Vector256<float> e3, Vector256<float> e4)
        {
            Vector256<float> v = (e1 * w1) + (e2 * w2) + (e3 * w3) + (e4 * w4);
            v = Vector256.ConditionalSelect(z4, e2, v);
            v = Vector256.ConditionalSelect(z3, e3, v);
            v = Vector256.ConditionalSelect(z2, e2, v);
            return Vector256.ConditionalSelect(z1, e1, v);
        }
    }

    /// <summary><see cref="SampleBilinear(ReadOnlySpan{Rgba32}, int, int, float, float, int)"/> of channel 0 at eight positions.</summary>
    public static Vector256<float> SampleBilinear(ReadOnlySpan<Rgba32> map, int width, int height, Vector256<float> x, Vector256<float> y)
    {
        x = Vector256.Clamp(x, Vector256<float>.Zero, Vector256.Create((float)(width - 1)));
        y = Vector256.Clamp(y, Vector256<float>.Zero, Vector256.Create((float)(height - 1)));
        Vector256<int> x0 = Vector256.ConvertToInt32(x), y0 = Vector256.ConvertToInt32(y);
        Vector256<int> x1 = Vector256.Min(x0 + Vector256<int>.One, Vector256.Create(width - 1));
        Vector256<int> y1 = Vector256.Min(y0 + Vector256<int>.One, Vector256.Create(height - 1));
        Vector256<float> fx = x - Vector256.ConvertToSingle(x0), fy = y - Vector256.ConvertToSingle(y0);
        Corners(map, (y0 * width) + x0, out Vector256<float> s00, out _, out _);
        Corners(map, (y0 * width) + x1, out Vector256<float> s01, out _, out _);
        Corners(map, (y1 * width) + x0, out Vector256<float> s10, out _, out _);
        Corners(map, (y1 * width) + x1, out Vector256<float> s11, out _, out _);
        return Lerp(Lerp(s00, s01, fx), Lerp(s10, s11, fx), fy);
    }

    /// <summary>Lane-wise <see cref="IsoGainFactor(float, float, float, float, float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> IsoGainFactor(Vector256<float> gain, float gainMapMin, float gainMapMax, float gamma, float weight)
    {
        if (gamma != 1f)
        {
            gain = DeterministicMath.Pow(gain, Vector256.Create(1f / gamma));
        }

        Vector256<float> logBoost = (Vector256.Create(gainMapMin) * (Vector256<float>.One - gain)) + (Vector256.Create(gainMapMax) * gain);
        return DeterministicMath.Exp2(logBoost * Vector256.Create(weight));
    }

    /// <summary>Lane-wise <see cref="Rec709ToLinear(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Rec709ToLinear(Vector256<float> v) =>
        Vector256.ConditionalSelect(Vector256.LessThan(v, Vector256.Create(0.081f)), v / Vector256.Create(4.5f),
            DeterministicMath.Pow((v + Vector256.Create(0.099f)) / Vector256.Create(1.099f), Vector256.Create(1f / 0.45f)));

    // R, G and B of the map samples at eight indices, as v / 255.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Corners(ReadOnlySpan<Rgba32> map, Vector256<int> index, out Vector256<float> r, out Vector256<float> g, out Vector256<float> b)
    {
        Rgba32 p0 = map[index[0]], p1 = map[index[1]], p2 = map[index[2]], p3 = map[index[3]];
        Rgba32 p4 = map[index[4]], p5 = map[index[5]], p6 = map[index[6]], p7 = map[index[7]];
        Vector256<float> scale = Vector256.Create(255f);
        r = Vector256.Create((float)p0.R, p1.R, p2.R, p3.R, p4.R, p5.R, p6.R, p7.R) / scale;
        g = Vector256.Create((float)p0.G, p1.G, p2.G, p3.G, p4.G, p5.G, p6.G, p7.G) / scale;
        b = Vector256.Create((float)p0.B, p1.B, p2.B, p3.B, p4.B, p5.B, p6.B, p7.B) / scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Distance(Vector256<float> x, Vector256<float> y) => Vector256.Sqrt((x * x) + (y * y));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Lerp(Vector256<float> a, Vector256<float> b, Vector256<float> t) => a + ((b - a) * t);
}
