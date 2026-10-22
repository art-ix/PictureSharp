using System.Buffers;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Color-noise reduction: the chroma differences Cb = B − Y and Cr = R − Y of the premultiplied values (0–1, Rec. 709
/// luma Y) are smoothed with a guided filter (He, Sun and Tang, 2010) that the luma guides, so color blotches in flat
/// areas average out while color edges that coincide with luma edges stay; the luma is not changed. Fully transparent
/// pixels keep their color. Mirrored by tests/assets/photo_adjust_reference.py.
/// </summary>
internal static class ChromaFilter
{
    /// <summary>Regularization of the guided filter: luma variation below about 0.01 counts as flat.</summary>
    public const float Epsilon = 0.01f * 0.01f;

    private const int BandLength = 1 << 16;

    /// <summary>The automatic window radius: 1% of the shorter side, rounded, at least 2.</summary>
    public static int AutomaticRadius(int width, int height) => Math.Max(2, (int)((0.01f * Math.Min(width, height)) + 0.5f));

    /// <summary>
    /// For P = Cb and Cr: <c>a = cov(Y, P) / (var(Y) + ε)</c>, <c>b = mean(P) − a · mean(Y)</c> over (2r + 1)²
    /// windows, <c>Q = mean(a) · Y + mean(b)</c> and <c>P + strength · (Q − P)</c>; then R = Y + Cr, B = Y + Cb and
    /// G = (Y − 0.2126 R − 0.0722 B) / 0.7152.
    /// </summary>
    public static void Reduce(Rgba32[] source, Rgba32[] destination, int width, int height, float strength, int radius,
        CancellationToken cancellationToken)
    {
        int length = width * height;
        float[] guide = ArrayPool<float>.Shared.Rent(length);
        float[] guideMean = ArrayPool<float>.Shared.Rent(length);
        float[] guideVariance = ArrayPool<float>.Shared.Rent(length);
        float[] first = ArrayPool<float>.Shared.Rent(length);
        float[] second = ArrayPool<float>.Shared.Rent(length);
        float[] blue = ArrayPool<float>.Shared.Rent(length);
        float[] scratch = ArrayPool<float>.Shared.Rent(length);
        try
        {
            ForBands(length, cancellationToken, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    float y = Luma(source[i]);
                    guide[i] = y;
                    guideMean[i] = y;
                    guideVariance[i] = y * y;
                }
            });
            PlaneBlur.Box(guideMean, guideMean, scratch, width, height, radius, cancellationToken);
            PlaneBlur.Box(guideVariance, guideVariance, scratch, width, height, radius, cancellationToken);
            ForBands(length, cancellationToken, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    float m = guideMean[i];
                    guideVariance[i] = MathF.Max(0f, guideVariance[i] - (m * m));
                }
            });

            // Blue difference first, kept in its own plane while the red one is filtered into the first plane.
            Filter(source, guide, guideMean, guideVariance, 2, first, second, blue, scratch, width, height, strength, radius, cancellationToken);
            Filter(source, guide, guideMean, guideVariance, 0, first, second, first, scratch, width, height, strength, radius, cancellationToken);
            ForBands(length, cancellationToken, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    Rgba32 p = source[i];
                    if (p.A == 0)
                    {
                        destination[i] = p;
                        continue;
                    }

                    float y = guide[i];
                    float r = y + first[i], b = y + blue[i];
                    float g = (y - (PhotoAdjust.LumaR * r) - (PhotoAdjust.LumaB * b)) / PhotoAdjust.LumaG;
                    float scale = 65025f / p.A;
                    destination[i] = new Rgba32(PhotoAdjust.ToByte(r * scale), PhotoAdjust.ToByte(g * scale), PhotoAdjust.ToByte(b * scale), p.A);
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(guide);
            ArrayPool<float>.Shared.Return(guideMean);
            ArrayPool<float>.Shared.Return(guideVariance);
            ArrayPool<float>.Shared.Return(first);
            ArrayPool<float>.Shared.Return(second);
            ArrayPool<float>.Shared.Return(blue);
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Filters the difference of <paramref name="channel"/> (0 red, 2 blue) and the luma into <paramref name="result"/>,
    /// which may be <paramref name="first"/>.
    /// </summary>
    private static void Filter(Rgba32[] source, float[] guide, float[] guideMean, float[] guideVariance, int channel,
        float[] first, float[] second, float[] result, float[] scratch, int width, int height, float strength, int radius,
        CancellationToken cancellationToken)
    {
        int length = width * height;
        ForBands(length, cancellationToken, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                float p = Difference(source[i], guide[i], channel);
                first[i] = p;
                second[i] = guide[i] * p;
            }
        });
        PlaneBlur.Box(first, first, scratch, width, height, radius, cancellationToken);
        PlaneBlur.Box(second, second, scratch, width, height, radius, cancellationToken);
        ForBands(length, cancellationToken, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                float meanP = first[i], meanI = guideMean[i];
                float a = (second[i] - (meanI * meanP)) / (guideVariance[i] + Epsilon);
                first[i] = a;
                second[i] = meanP - (a * meanI);
            }
        });
        PlaneBlur.Box(first, first, scratch, width, height, radius, cancellationToken);
        PlaneBlur.Box(second, second, scratch, width, height, radius, cancellationToken);
        ForBands(length, cancellationToken, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                float y = guide[i];
                float p = Difference(source[i], y, channel);
                float q = (first[i] * y) + second[i];
                result[i] = p + (strength * (q - p));
            }
        });
    }

    private static float Premultiplied(Rgba32 p, int channel)
    {
        int value = channel switch { 0 => p.R, 1 => p.G, _ => p.B };
        return value * p.A / 65025f;
    }

    private static float Luma(Rgba32 p) =>
        (PhotoAdjust.LumaR * Premultiplied(p, 0)) + (PhotoAdjust.LumaG * Premultiplied(p, 1)) + (PhotoAdjust.LumaB * Premultiplied(p, 2));

    private static float Difference(Rgba32 p, float luma, int channel) => Premultiplied(p, channel) - luma;

    private static void ForBands(int length, CancellationToken cancellationToken, Action<int, int> body) =>
        Parallelism.ForBands(length, BandLength, length, cancellationToken, body);
}
