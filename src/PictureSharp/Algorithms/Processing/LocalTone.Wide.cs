using System.Buffers;
using System.Numerics;
using PictureSharp.Domain.Pixels;
using PictureSharp.Primitives.Numerics;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Processing;

internal static partial class LocalTone
{
    /// <summary>
    /// <see cref="ShadowsHighlights"/> at any depth: the luma plane from unrounded values, the gain applied in
    /// continuous linear light (<see cref="LinearUnit"/>) and rounded once to the storage depth.
    /// </summary>
    public static void ShadowsHighlightsWide<TPixel>(TPixel[] source, TPixel[] destination, int width, int height,
        float shadows, float highlights, float sigma, CancellationToken cancellationToken) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        int length = width * height;
        float[] blurred = ArrayPool<float>.Shared.Rent(length);
        try
        {
            BlurredLumaWide(source, blurred, width, height, sigma, cancellationToken);
            Parallelism.ForBands(length, BandLength, length, cancellationToken, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    float b = blurred[i], dark = 1f - b;
                    float stops = ShadowsHighlightsStops * ((shadows * dark * dark) + (highlights * b * b));
                    float gain = DeterministicMath.Exp2(stops);
                    Vector4 v = LinearUnit.Load(source[i]);
                    destination[i] = TPixel.WithAlpha(LinearUnit.Store<TPixel>(new Vector4(new Vector3(v.X, v.Y, v.Z) * gain, v.W)), TPixel.Alpha(source[i]));
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(blurred);
        }
    }

    /// <summary>
    /// <see cref="Clarity"/> at any depth: <c>amount · (P − B) · 4P(1 − P)</c> (in units of the range) added to the
    /// unrounded channels, clamped and rounded once.
    /// </summary>
    public static void ClarityWide<TPixel>(TPixel[] source, TPixel[] destination, int width, int height,
        float amount, float sigma, CancellationToken cancellationToken) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        int length = width * height;
        float[] blurred = ArrayPool<float>.Shared.Rent(length);
        try
        {
            BlurredLumaWide(source, blurred, width, height, sigma, cancellationToken);
            Parallelism.ForBands(length, BandLength, length, cancellationToken, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    Vector4 p = TPixel.ToUnit(source[i]);
                    float luma = LumaUnit(p);
                    float delta = amount * (luma - blurred[i]) * (4f * luma * (1f - luma));
                    destination[i] = TPixel.WithAlpha(TPixel.FromUnit(new Vector4(new Vector3(p.X, p.Y, p.Z) + new Vector3(delta), 0f)), TPixel.Alpha(source[i]));
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(blurred);
        }
    }

    private static void BlurredLumaWide<TPixel>(TPixel[] source, float[] plane, int width, int height, float sigma,
        CancellationToken cancellationToken) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        int length = width * height;
        Parallelism.ForBands(length, BandLength, length, cancellationToken, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                plane[i] = LumaUnit(TPixel.ToUnit(source[i]));
            }
        });

        float[] scratch = ArrayPool<float>.Shared.Rent(length);
        try
        {
            PlaneBlur.Gaussian(plane, scratch, width, height, sigma, cancellationToken);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    private static float LumaUnit(Vector4 p) => (PhotoAdjust.LumaR * p.X) + (PhotoAdjust.LumaG * p.Y) + (PhotoAdjust.LumaB * p.Z);
}
