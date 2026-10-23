using PictureSharp.Domain.Hdr;

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
    /// map differently by design.
    /// </summary>
    public static (GainMapParameters Parameters, DecodedGainMap Map) AppleToIso(GainMapParameters apple, DecodedGainMap map, int baseWidth, int baseHeight)
    {
        double headroom = double.Exp2(apple.AlternateHeadroom);
        double stops = Math.Max(apple.AlternateHeadroom, MinimumStops);

        // Apple samples the map at (x + 0.5) / s − 0.5 for base pixel x, ISO at x / s.
        float shiftX = (0.5f * map.Width / baseWidth) - 0.5f, shiftY = (0.5f * map.Height / baseHeight) - 0.5f;
        Rgba32[] pixels = GC.AllocateUninitializedArray<Rgba32>(map.Pixels.Length);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                // Apple maps are gray; the gray value is R.
                float m = GainMapMath.SampleBilinear(map.Pixels, map.Width, map.Height, x + shiftX, y + shiftY, 0);
                double factor = 1 + ((headroom - 1) * GainMapMath.Rec709ToLinear(m));
                byte v = (byte)Math.Clamp(Math.Round(255 * Math.Log2(factor) / stops), 0, 255);
                pixels[(y * map.Width) + x] = new Rgba32(v, v, v, 255);
            }
        }

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
}
