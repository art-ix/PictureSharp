using System.Runtime.CompilerServices;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>Scalar gain map formulas of the ISO 21496-1 and Apple models.</summary>
internal static partial class GainMapMath
{
    /// <summary>
    /// Shepard's inverse-distance weighting of the four map samples around (<paramref name="xMap"/>,
    /// <paramref name="yMap"/>), as libultrahdr samples gain maps: neighbors beyond the last row or column are clamped
    /// and their distance is measured to the clamped position; a sample at distance 0 is returned unchanged.
    /// </summary>
    // port: libultrahdr v2.0.2 lib/src/gainmapmath.cpp sampleMap (float version; its ShepardsIDW tables for integer
    // scale factors give the same weights)
    public static float SampleIdw(ReadOnlySpan<Rgba32> map, int width, int height, float xMap, float yMap, int channel)
    {
        int xLower = Math.Min((int)MathF.Floor(xMap), width - 1);
        int yLower = Math.Min((int)MathF.Floor(yMap), height - 1);
        int xUpper = Math.Min(xLower + 1, width - 1);
        int yUpper = Math.Min(yLower + 1, height - 1);

        float e1 = Sample(map, (yLower * width) + xLower, channel);
        float e1Distance = Distance(xMap - xLower, yMap - yLower);
        if (e1Distance == 0f)
        {
            return e1;
        }

        float e2 = Sample(map, (yUpper * width) + xLower, channel);
        float e2Distance = Distance(xMap - xLower, yMap - yUpper);
        if (e2Distance == 0f)
        {
            return e2;
        }

        float e3 = Sample(map, (yLower * width) + xUpper, channel);
        float e3Distance = Distance(xMap - xUpper, yMap - yLower);
        if (e3Distance == 0f)
        {
            return e3;
        }

        float e4 = Sample(map, (yUpper * width) + xUpper, channel);
        float e4Distance = Distance(xMap - xUpper, yMap - yUpper);
        if (e4Distance == 0f)
        {
            return e2;   // as libultrahdr (unreachable: e1 is at distance 0 first)
        }

        float e1Weight = 1f / e1Distance, e2Weight = 1f / e2Distance, e3Weight = 1f / e3Distance, e4Weight = 1f / e4Distance;
        float totalWeight = e1Weight + e2Weight + e3Weight + e4Weight;
        return (e1 * (e1Weight / totalWeight)) + (e2 * (e2Weight / totalWeight)) + (e3 * (e3Weight / totalWeight)) + (e4 * (e4Weight / totalWeight));
    }

    /// <summary>Bilinear interpolation of the encoded map at pixel-center coordinates (Apple model).</summary>
    public static float SampleBilinear(ReadOnlySpan<Rgba32> map, int width, int height, float x, float y, int channel)
    {
        x = Math.Clamp(x, 0f, width - 1);
        y = Math.Clamp(y, 0f, height - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        float fx = x - x0, fy = y - y0;
        float top = Lerp(Sample(map, (y0 * width) + x0, channel), Sample(map, (y0 * width) + x1, channel), fx);
        float bottom = Lerp(Sample(map, (y1 * width) + x0, channel), Sample(map, (y1 * width) + x1, channel), fx);
        return Lerp(top, bottom, fy);
    }

    /// <summary>
    /// The ISO 21496-1 gain factor of map value <paramref name="gain"/> (0..1): gamma, interpolation between the log2
    /// gain bounds and the headroom weight. Powers use <see cref="DeterministicMath"/>, so results do not depend on the
    /// platform's C runtime and equal the vector path.
    /// </summary>
    // port: libultrahdr v2.0.2 lib/src/gainmapmath.cpp applyGain
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float IsoGainFactor(float gain, float gainMapMin, float gainMapMax, float gamma, float weight)
    {
        if (gamma != 1f)
        {
            gain = DeterministicMath.Pow(gain, 1f / gamma);
        }

        float logBoost = (gainMapMin * (1f - gain)) + (gainMapMax * gain);
        return DeterministicMath.Exp2(logBoost * weight);
    }

    /// <summary>
    /// The weight of the alternate rendition for a display with <paramref name="targetLog2"/> stops of headroom, between
    /// the base and alternate headrooms (log2), clamped to 0..1.
    /// </summary>
    // port: libultrahdr v2.0.2 lib/src/gainmapmath.cpp (gain map weight of applyGainMap)
    public static float IsoWeight(float targetLog2, float baseHeadroom, float alternateHeadroom) =>
        Math.Clamp((targetLog2 - baseHeadroom) / (alternateHeadroom - baseHeadroom), 0f, 1f);

    /// <summary>The inverse of the Rec. 709 OETF, which linearizes Apple gain map values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Rec709ToLinear(float v) => v < 0.081f ? v / 4.5f : DeterministicMath.Pow((v + 0.099f) / 1.099f, 1f / 0.45f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Sample(ReadOnlySpan<Rgba32> map, int index, int channel)
    {
        Rgba32 p = map[index];
        return (channel == 0 ? p.R : channel == 1 ? p.G : p.B) / 255f;
    }

    private static float Distance(float x, float y) => MathF.Sqrt((x * x) + (y * y));

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
