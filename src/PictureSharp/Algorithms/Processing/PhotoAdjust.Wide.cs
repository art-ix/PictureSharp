using System.Numerics;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Pixels;
using PictureSharp.Primitives.Numerics;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Processing;

internal static partial class PhotoAdjust
{
    /// <summary>Fills the 16-bit <paramref name="lut"/> with <c>Encode(Decode(v) · 2^stops)</c>.</summary>
    public static void ExposureLut(Span<ushort> lut, float stops)
    {
        float gain = DeterministicMath.Exp2(stops);
        ChannelGainsLut(lut, gain, gain, gain);
    }

    /// <summary>Fills the 16-bit <paramref name="lut"/> as <see cref="WhiteBalanceLut(Span{byte}, float, float)"/>.</summary>
    public static void WhiteBalanceLut(Span<ushort> lut, float temperature, float tint)
    {
        (float r, float g, float b) = WhiteBalanceGains(temperature, tint);
        ChannelGainsLut(lut, r, g, b);
    }

    /// <summary>
    /// Fills the 16-bit <paramref name="lut"/> (R, G, B × 65536) with <c>Encode(Decode(v) · gain)</c> per channel: the
    /// continuous sRGB decode of every 16-bit value, the deterministic float encode, rounded once.
    /// </summary>
    public static void ChannelGainsLut(Span<ushort> lut, float red, float green, float blue)
    {
        const int N = ToneLut.Channel16;
        GainLut(lut[..N], red);
        GainLut(lut.Slice(N, N), green);
        GainLut(lut.Slice(2 * N, N), blue);
    }

    private static void GainLut(Span<ushort> map, float gain)
    {
        for (int v = 0; v < map.Length; v++)
        {
            map[v] = (ushort)PixelDepth.Quantize(SrgbTransfer.EncodeUnit(SrgbTransfer.Decode16((ushort)v) * gain), ushort.MaxValue);
        }
    }

    /// <summary>
    /// <see cref="Vibrance(Rgba32[], int, float, CancellationToken)"/> on unrounded values in [0, 1] at any depth,
    /// rounded once to the storage depth.
    /// </summary>
    public static void VibranceWide<TPixel>(TPixel[] pixels, float amount, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelOps<TPixel> =>
        Parallelism.ForBands(pixels.Length, BandLength, pixels.Length, cancellationToken, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                Vector4 p = TPixel.ToUnit(pixels[i]);
                float r = p.X, g = p.Y, b = p.Z;
                float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
                if (max == min)
                {
                    continue;
                }

                float gain = amount >= 0f ? 1f + (amount * (1f - ((max - min) / max))) : 1f + amount;
                float y = (LumaR * r) + (LumaG * g) + (LumaB * b);
                pixels[i] = TPixel.FromUnit(new Vector4(y + (gain * (r - y)), y + (gain * (g - y)), y + (gain * (b - y)), p.W));
            }
        });
}
