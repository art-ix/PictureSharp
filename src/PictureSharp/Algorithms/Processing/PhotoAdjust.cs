using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Primitives.Numerics;
using PictureSharp.Primitives.Parallelism;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Photographic adjustments without neighbourhoods: exposure and white balance as channel curves in linear light,
/// vibrance on the encoded values and a radial vignette in linear light. The formulas are documented in
/// docs/image-processing.md and mirrored by tests/assets/photo_adjust_reference.py.
/// </summary>
internal static class PhotoAdjust
{
    /// <summary>Rec. 709 luma weights.</summary>
    public const float LumaR = 0.2126f, LumaG = 0.7152f, LumaB = 0.0722f;

    // White balance at |temperature| = 1 or |tint| = 1 changes a channel by this many stops before normalization.
    private const float WhiteBalanceStops = 0.5f;

    /// <summary>Fills <paramref name="lut"/> (R, G, B × 256) with <c>Encode(Decode(v) · 2^stops)</c>.</summary>
    public static void ExposureLut(Span<byte> lut, float stops)
    {
        float gain = DeterministicMath.Exp2(stops);
        GainLut(lut[..256], gain);
        lut[..256].CopyTo(lut.Slice(256, 256));
        lut[..256].CopyTo(lut.Slice(512, 256));
    }

    /// <summary>
    /// Fills <paramref name="lut"/> with channel gains <c>2^(k·t)</c>, <c>2^(−k·tint)</c> and <c>2^(−k·t)</c> for red,
    /// green and blue, divided by their luma so that gray keeps its luminance.
    /// </summary>
    public static void WhiteBalanceLut(Span<byte> lut, float temperature, float tint)
    {
        float r = DeterministicMath.Exp2(WhiteBalanceStops * temperature);
        float g = DeterministicMath.Exp2(-WhiteBalanceStops * tint);
        float b = DeterministicMath.Exp2(-WhiteBalanceStops * temperature);
        float normalize = 1f / ((LumaR * r) + (LumaG * g) + (LumaB * b));
        GainLut(lut[..256], r * normalize);
        GainLut(lut.Slice(256, 256), g * normalize);
        GainLut(lut.Slice(512, 256), b * normalize);
    }

    private static void GainLut(Span<byte> map, float gain)
    {
        for (int v = 0; v < map.Length; v++)
        {
            map[v] = SrgbTransfer.Encode(SrgbTransfer.Decode((byte)v) * gain);
        }
    }

    /// <summary>
    /// Scales the chroma around the luma: by <c>1 + amount · (1 − s)</c> with saturation <c>s = (max − min) / max</c>
    /// when raising, by <c>1 + amount</c> when lowering. Alpha is kept.
    /// </summary>
    public static void Vibrance(Rgba32[] pixels, int length, float amount, CancellationToken cancellationToken) =>
        Parallelism.ForBands(length, BandLength, length, cancellationToken, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                Rgba32 p = pixels[i];
                float r = p.R, g = p.G, b = p.B;
                float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
                if (max == min)
                {
                    continue;
                }

                float gain = amount >= 0f ? 1f + (amount * (1f - ((max - min) / max))) : 1f + amount;
                float y = (LumaR * r) + (LumaG * g) + (LumaB * b);
                pixels[i] = new Rgba32(ToByte(y + (gain * (r - y))), ToByte(y + (gain * (g - y))), ToByte(y + (gain * (b - y))), p.A);
            }
        });

    /// <summary>
    /// Multiplies linear light by <c>2^(−2 · Amount · w)</c>, where <c>w</c> is a smoothstep of the distance from the
    /// center (relative to half the diagonal) from <c>Midpoint</c> to <c>Midpoint + Feather · (1 − Midpoint)</c>.
    /// </summary>
    public static void Vignette(Rgba32[] pixels, int width, int height, VignetteOptions options, CancellationToken cancellationToken)
    {
        float inner = options.Midpoint;
        float outer = inner + (options.Feather * (1f - inner));
        float scale = -2f * options.Amount;
        float inverseRadius = 1f / (0.5f * MathF.Sqrt((width * (float)width) + (height * (float)height)));
        float centerX = (width - 1) * 0.5f, centerY = (height - 1) * 0.5f;
        Parallelism.ForBands(height, Math.Max(1, BandLength / width), (long)width * height, cancellationToken, (top, bottom) =>
        {
            for (int y = top; y < bottom; y++)
            {
                Span<Rgba32> row = pixels.AsSpan(y * width, width);
                float dy = y - centerY;
                for (int x = 0; x < width; x++)
                {
                    float dx = x - centerX;
                    float d = MathF.Sqrt((dx * dx) + (dy * dy)) * inverseRadius;
                    float t = outer > inner ? Math.Clamp((d - inner) / (outer - inner), 0f, 1f) : d >= inner ? 1f : 0f;
                    if (t == 0f)
                    {
                        continue;
                    }

                    float gain = DeterministicMath.Exp2(scale * (t * t * (3f - (2f * t))));
                    Rgba32 p = row[x];
                    row[x] = new Rgba32(
                        SrgbTransfer.Encode(SrgbTransfer.Decode(p.R) * gain),
                        SrgbTransfer.Encode(SrgbTransfer.Decode(p.G) * gain),
                        SrgbTransfer.Encode(SrgbTransfer.Decode(p.B) * gain),
                        p.A);
                }
            }
        });
    }

    private const int BandLength = 1 << 16;

    /// <summary>Rounds half up and clamps to 0–255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte(float value) => value <= 0f ? (byte)0 : value >= 255f ? (byte)255 : (byte)(int)(value + 0.5f);
}
