using System.Buffers;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Edge-preserving smoothing with the guided filter of He, Sun and Tang ("Guided Image Filtering", 2010), each color
/// channel guiding itself. Works on premultiplied values in [0, 1], so transparent pixels do not bleed into their
/// neighbours; alpha is kept, and fully transparent pixels keep their color. Planes are float at every depth, box
/// sums double (<see cref="PlaneBlur"/>); results are rounded once to the storage depth.
/// </summary>
internal static class GuidedFilter
{
    private const int BandLength = 1 << 16;

    /// <summary>
    /// For each channel I: <c>a = var(I) / (var(I) + ε)</c>, <c>b = mean(I) · (1 − a)</c> over (2r + 1)² windows, and
    /// the result <c>mean(a) · I + mean(b)</c>, with <c>ε = (0.05 · strength)²</c>.
    /// </summary>
    public static void Denoise<TPixel>(TPixel[] source, TPixel[] destination, int width, int height,
        float strength, int radius, CancellationToken cancellationToken) where TPixel : unmanaged, IPixelOps<TPixel>
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
                        float v = PremultipliedChannel.Load(source[i], c);
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
                        TPixel p = source[i];
                        int alpha = TPixel.Alpha(p);
                        if (alpha == 0)
                        {
                            continue;
                        }

                        float q = (mean[i] * PremultipliedChannel.Load(p, c)) + square[i];
                        if (typeof(TPixel) == typeof(Rgba32))
                        {
                            byte value = PhotoAdjust.ToByte(q * 65025f / alpha);
                            ref Rgba32 target = ref Unsafe.As<TPixel, Rgba32>(ref destination[i]);
                            target = c switch
                            {
                                0 => new Rgba32(value, target.G, target.B, target.A),
                                1 => new Rgba32(target.R, value, target.B, target.A),
                                _ => new Rgba32(target.R, target.G, value, target.A),
                            };
                        }
                        else
                        {
                            destination[i] = TPixel.FromRgba64(
                                PremultipliedChannel.With(TPixel.ToRgba64(destination[i]), c, PremultipliedChannel.Store16(q, alpha)));
                        }
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
}
