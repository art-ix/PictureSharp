using System.Buffers;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Edge-preserving smoothing with the guided filter of He, Sun and Tang ("Guided Image Filtering", 2010), each color
/// channel guiding itself. Works on premultiplied values in [0, 1], so transparent pixels do not bleed into their
/// neighbours; alpha is kept, and fully transparent pixels keep their color.
/// </summary>
internal static class GuidedFilter
{
    private const int BandLength = 1 << 16;

    /// <summary>
    /// For each channel I: <c>a = var(I) / (var(I) + ε)</c>, <c>b = mean(I) · (1 − a)</c> over (2r + 1)² windows, and
    /// the result <c>mean(a) · I + mean(b)</c>, with <c>ε = (0.05 · strength)²</c>.
    /// </summary>
    public static void Denoise(Rgba32[] source, Rgba32[] destination, int width, int height,
        float strength, int radius, CancellationToken cancellationToken)
    {
        int length = width * height;
        float epsilon = 0.05f * strength * (0.05f * strength);
        float[] mean = ArrayPool<float>.Shared.Rent(length);
        float[] square = ArrayPool<float>.Shared.Rent(length);
        float[] scratch = ArrayPool<float>.Shared.Rent(length);
        try
        {
            source.AsSpan(0, length).CopyTo(destination);
            for (int channel = 0; channel < 3; channel++)
            {
                int c = channel;
                ForBands(length, cancellationToken, (start, end) =>
                {
                    for (int i = start; i < end; i++)
                    {
                        float v = Premultiplied(source[i], c);
                        mean[i] = v;
                        square[i] = v * v;
                    }
                });

                PlaneBlur.Box(mean, mean, scratch, width, height, radius, cancellationToken);
                PlaneBlur.Box(square, square, scratch, width, height, radius, cancellationToken);
                ForBands(length, cancellationToken, (start, end) =>
                {
                    for (int i = start; i < end; i++)
                    {
                        float m = mean[i];
                        float variance = MathF.Max(0f, square[i] - (m * m));
                        float a = variance / (variance + epsilon);
                        mean[i] = a;
                        square[i] = m * (1f - a);
                    }
                });

                PlaneBlur.Box(mean, mean, scratch, width, height, radius, cancellationToken);
                PlaneBlur.Box(square, square, scratch, width, height, radius, cancellationToken);
                ForBands(length, cancellationToken, (start, end) =>
                {
                    for (int i = start; i < end; i++)
                    {
                        Rgba32 p = source[i];
                        if (p.A == 0)
                        {
                            continue;
                        }

                        float q = (mean[i] * Premultiplied(p, c)) + square[i];
                        byte value = PhotoAdjust.ToByte(q * 65025f / p.A);
                        ref Rgba32 target = ref destination[i];
                        target = c switch
                        {
                            0 => new Rgba32(value, target.G, target.B, target.A),
                            1 => new Rgba32(target.R, value, target.B, target.A),
                            _ => new Rgba32(target.R, target.G, value, target.A),
                        };
                    }
                });
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mean);
            ArrayPool<float>.Shared.Return(square);
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    private static void ForBands(int length, CancellationToken cancellationToken, Action<int, int> body) =>
        Parallelism.ForBands(length, BandLength, length, cancellationToken, body);

    private static float Premultiplied(Rgba32 p, int channel)
    {
        int value = channel switch { 0 => p.R, 1 => p.G, _ => p.B };
        return value * p.A / 65025f;
    }
}
