using PictureSharp.Domain.Hdr;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>Converts an Apple gain map to the ISO 21496-1 model, so it can be written where only ISO is defined.</summary>
internal static class GainMapConversion
{
    // A headroom of one leaves the map without effect; ISO needs an alternate headroom above the base's.
    private const double MinimumStops = 1.0 / 64;

    /// <summary>
    /// The ISO parameters and map codes that reproduce an Apple map at full headroom H for a base of
    /// <paramref name="baseWidth"/> × <paramref name="baseHeight"/>. Apple multiplies the base by
    /// <c>1 + (H − 1) · Rec709⁻¹(m)</c>, with m the map interpolated bilinearly between pixel centers; ISO with gain
    /// bounds 0 and log2 H, gamma 1 and no offsets by <c>2^(c′ · log2 H)</c>, with c′ interpolated from map pixel
    /// corners (libultrahdr). Each new code is therefore <c>log2(1 + (H − 1) · Rec709⁻¹(m)) / log2 H</c> with m sampled
    /// half a base pixel earlier, which aligns the two sampling grids. Below full headroom the two models weight the
    /// map differently by design. The new codes have the map's depth.
    /// </summary>
    public static (GainMapParameters Parameters, DecodedGainMap Map) AppleToIso(GainMapParameters apple, DecodedGainMap map, int baseWidth, int baseHeight)
    {
        double headroom = double.Exp2(apple.AlternateHeadroom);
        double stops = Math.Max(apple.AlternateHeadroom, MinimumStops);

        // Apple samples the map at (x + 0.5) / s − 0.5 for base pixel x, ISO at x / s.
        float shiftX = (0.5f * map.Width / baseWidth) - 0.5f, shiftY = (0.5f * map.Height / baseHeight) - 0.5f;
        var operation = new Remap(map, headroom, stops, shiftX, shiftY);
        PixelBuffer pixels = PixelDispatch.Run<Remap, PixelBuffer>(map.PixelType, ref operation);

        float max = (float)stops;
        var parameters = new GainMapParameters
        {
            Kind = HdrGainMapKinds.Iso21496,
            ChannelCount = 1,
            GainMapMax = [max, max, max],
            AlternateHeadroom = max,
            MultiplicationSpace = apple.MultiplicationSpace,
            UseBaseColorSpace = true,
        };
        return (parameters, map with { Pixels = pixels });
    }

    // The new codes at the map's depth, rounded once.
    private readonly struct Remap(DecodedGainMap map, double headroom, double stops, float shiftX, float shiftY) : IPixelOperation<PixelBuffer>
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            ReadOnlySpan<TPixel> source = map.Pixels.As<TPixel>();
            TPixel[] pixels = GC.AllocateUninitializedArray<TPixel>(source.Length);
            int max = TPixel.MaxValue;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    // Apple maps are gray; the gray value is R.
                    float m = GainMapMath.SampleBilinear(source, map.Width, map.Height, x + shiftX, y + shiftY, 0);
                    double factor = 1 + ((headroom - 1) * GainMapMath.Rec709ToLinear(m));
                    int v = (int)Math.Clamp(Math.Round(max * Math.Log2(factor) / stops), 0, max);
                    pixels[(y * map.Width) + x] = max == byte.MaxValue
                        ? TPixel.FromRgba32(new Rgba32((byte)v, (byte)v, (byte)v, 255))
                        : TPixel.FromRgba64(new Rgba64((ushort)v, (ushort)v, (ushort)v, ushort.MaxValue));
                }
            }

            return PixelBuffer.From(pixels);
        }
    }
}
