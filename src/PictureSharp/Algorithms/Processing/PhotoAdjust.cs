using System.Numerics;
using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Pixels;
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
        ChannelGainsLut(lut, r * normalize, g * normalize, b * normalize);
    }

    /// <summary>
    /// Fills <paramref name="lut"/> with channel gains <c>Y / C</c> in linear light, where C is a channel of
    /// <paramref name="neutral"/> (all above 0) and Y its Rec. 709 luma: the neutral becomes gray of the same luminance.
    /// </summary>
    public static void NeutralWhiteBalanceLut(Span<byte> lut, Rgba32 neutral)
    {
        float r = SrgbTransfer.Decode(neutral.R), g = SrgbTransfer.Decode(neutral.G), b = SrgbTransfer.Decode(neutral.B);
        float luma = (LumaR * r) + (LumaG * g) + (LumaB * b);
        ChannelGainsLut(lut, luma / r, luma / g, luma / b);
    }

    /// <summary>Fills <paramref name="lut"/> (R, G, B × 256) with <c>Encode(Decode(v) · gain)</c> per channel.</summary>
    public static void ChannelGainsLut(Span<byte> lut, float red, float green, float blue)
    {
        GainLut(lut[..256], red);
        GainLut(lut.Slice(256, 256), green);
        GainLut(lut.Slice(512, 256), blue);
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
    /// 8-bit pixels go through the transfer tables; others are decoded and encoded continuously and rounded once.
    /// </summary>
    public static void Vignette<TPixel>(TPixel[] pixels, int width, int height, VignetteOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelOps<TPixel>
    {
        var shape = new VignetteShape(width, height, options);
        Parallelism.ForBands(height, Math.Max(1, BandLength / width), (long)width * height, cancellationToken, (top, bottom) =>
        {
            for (int y = top; y < bottom; y++)
            {
                Span<TPixel> row = pixels.AsSpan(y * width, width);
                float dy = y - shape.CenterY;
                for (int x = 0; x < width; x++)
                {
                    float t = shape.Weight(x - shape.CenterX, dy);
                    if (t == 0f)
                    {
                        continue;
                    }

                    float gain = DeterministicMath.Exp2(shape.Scale * (t * t * (3f - (2f * t))));
                    if (typeof(TPixel) == typeof(Rgba32))
                    {
                        Rgba32 p = Unsafe.As<TPixel, Rgba32>(ref row[x]);
                        Unsafe.As<TPixel, Rgba32>(ref row[x]) = new Rgba32(
                            SrgbTransfer.Encode(SrgbTransfer.Decode(p.R) * gain),
                            SrgbTransfer.Encode(SrgbTransfer.Decode(p.G) * gain),
                            SrgbTransfer.Encode(SrgbTransfer.Decode(p.B) * gain),
                            p.A);
                    }
                    else
                    {
                        Vector4 v = LinearUnit.Load(row[x]);
                        row[x] = LinearUnit.Store<TPixel>(new Vector4(new Vector3(v.X, v.Y, v.Z) * gain, v.W));
                    }
                }
            }
        });
    }

    /// <summary>The geometry of <see cref="Vignette"/>.</summary>
    private readonly struct VignetteShape(int width, int height, VignetteOptions options)
    {
        private readonly float _inner = options.Midpoint;
        private readonly float _outer = options.Midpoint + (options.Feather * (1f - options.Midpoint));
        private readonly float _inverseRadius = 1f / (0.5f * MathF.Sqrt((width * (float)width) + (height * (float)height)));

        public float Scale { get; } = -2f * options.Amount;

        public float CenterX { get; } = (width - 1) * 0.5f;

        public float CenterY { get; } = (height - 1) * 0.5f;

        /// <summary>The smoothstep argument at offset (<paramref name="dx"/>, <paramref name="dy"/>) from the center.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Weight(float dx, float dy)
        {
            float d = MathF.Sqrt((dx * dx) + (dy * dy)) * _inverseRadius;
            return _outer > _inner ? Math.Clamp((d - _inner) / (_outer - _inner), 0f, 1f) : d >= _inner ? 1f : 0f;
        }
    }

    private const int BandLength = 1 << 16;

    /// <summary>Rounds half up and clamps to 0–255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte(float value) => value <= 0f ? (byte)0 : value >= 255f ? (byte)255 : (byte)(int)(value + 0.5f);
}
