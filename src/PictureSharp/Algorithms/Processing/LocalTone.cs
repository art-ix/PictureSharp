using System.Buffers;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Primitives.Numerics;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Shadows/highlights and clarity: both compare each pixel with a Gaussian blur <c>B</c> of the luma plane
/// <c>P = (0.2126 R + 0.7152 G + 0.0722 B) / 255</c> of the encoded values. Transparent pixels count like opaque ones.
/// </summary>
internal static partial class LocalTone
{
    // Shadows or highlights at ±1 change the darkest or lightest surroundings by this many stops.
    private const float ShadowsHighlightsStops = 1.5f;
    private const int BandLength = 1 << 16;

    /// <summary>
    /// Multiplies linear light by <c>2^(1.5 · (shadows · (1 − B)² + highlights · B²))</c>.
    /// </summary>
    public static void ShadowsHighlights(Rgba32[] source, Rgba32[] destination, int width, int height,
        float shadows, float highlights, float sigma, CancellationToken cancellationToken)
    {
        int length = width * height;
        float[] blurred = ArrayPool<float>.Shared.Rent(length);
        try
        {
            BlurredLuma(source, blurred, width, height, sigma, cancellationToken);
            Parallelism.ForBands(length, BandLength, length, cancellationToken, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    float b = blurred[i], dark = 1f - b;
                    float stops = ShadowsHighlightsStops * ((shadows * dark * dark) + (highlights * b * b));
                    float gain = DeterministicMath.Exp2(stops);
                    Rgba32 p = source[i];
                    destination[i] = new Rgba32(
                        SrgbTransfer.Encode(SrgbTransfer.Decode(p.R) * gain),
                        SrgbTransfer.Encode(SrgbTransfer.Decode(p.G) * gain),
                        SrgbTransfer.Encode(SrgbTransfer.Decode(p.B) * gain),
                        p.A);
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(blurred);
        }
    }

    /// <summary>
    /// Adds <c>255 · amount · (P − B) · 4P(1 − P)</c> to each color channel: local contrast, faded out towards black and
    /// white.
    /// </summary>
    public static void Clarity(Rgba32[] source, Rgba32[] destination, int width, int height,
        float amount, float sigma, CancellationToken cancellationToken)
    {
        int length = width * height;
        float[] blurred = ArrayPool<float>.Shared.Rent(length);
        try
        {
            BlurredLuma(source, blurred, width, height, sigma, cancellationToken);
            Parallelism.ForBands(length, BandLength, length, cancellationToken, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    Rgba32 p = source[i];
                    float luma = Luma(p);
                    float delta = 255f * amount * (luma - blurred[i]) * (4f * luma * (1f - luma));
                    destination[i] = new Rgba32(
                        PhotoAdjust.ToByte(p.R + delta), PhotoAdjust.ToByte(p.G + delta), PhotoAdjust.ToByte(p.B + delta), p.A);
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(blurred);
        }
    }

    /// <summary>The automatic blur: 2% of the shorter side, at least 1 pixel.</summary>
    public static float AutomaticSigma(int width, int height) => MathF.Max(1f, 0.02f * Math.Min(width, height));

    private static void BlurredLuma(Rgba32[] source, float[] plane, int width, int height, float sigma, CancellationToken cancellationToken)
    {
        int length = width * height;
        Parallelism.ForBands(length, BandLength, length, cancellationToken, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                plane[i] = Luma(source[i]);
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

    private static float Luma(Rgba32 p) => ((PhotoAdjust.LumaR * p.R) + (PhotoAdjust.LumaG * p.G) + (PhotoAdjust.LumaB * p.B)) / 255f;
}
