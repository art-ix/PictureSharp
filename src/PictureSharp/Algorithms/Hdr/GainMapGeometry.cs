using PictureSharp.Algorithms.Processing;
using PictureSharp.Domain.Hdr;
using PictureSharp.Domain.Pixels;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>
/// Carries a decoded gain map through the geometric edits of its base image. The map covers the whole base canvas:
/// map pixel i spans base pixels [i·W/mw, (i+1)·W/mw). Quarter turns and flips are the same array operations as on
/// the base; every other edit resamples the map's codes from the edit's source box converted to map coordinates, or
/// slices the map when that box is integral and keeps its size (plan.md 0.6.9; model in tests/assets/hdr_reference.py
/// edits()). A 16-bit map stays 16-bit: it is resampled at its own precision.
/// </summary>
internal static class GainMapGeometry
{
    /// <summary>The map extent for a base extent of <paramref name="newBase"/>: max(1, round(newBase·map/base)), halves up.</summary>
    public static int MapSize(int newBase, int baseSize, int mapSize) =>
        (int)Math.Max(1, ((2L * newBase * mapSize) + baseSize) / (2L * baseSize));

    /// <summary>Rotates by <paramref name="quarterTurns"/> clockwise, then mirrors horizontally when <paramref name="flipX"/> is set.</summary>
    public static DecodedGainMap RotateFlip(DecodedGainMap map, int quarterTurns, bool flipX)
    {
        var operation = new RotateFlipMap(map, quarterTurns, flipX);
        return PixelDispatch.Run<RotateFlipMap, DecodedGainMap>(map.PixelType, ref operation);
    }

    /// <summary>
    /// The map of the base region (<paramref name="x0"/>, <paramref name="y0"/>)–(<paramref name="x1"/>, <paramref name="y1"/>)
    /// of a <paramref name="baseWidth"/>×<paramref name="baseHeight"/> base, scaled to a base of
    /// <paramref name="width"/>×<paramref name="height"/>.
    /// </summary>
    public static DecodedGainMap Region(DecodedGainMap map, int baseWidth, int baseHeight, float x0, float y0, float x1, float y1,
        int width, int height, ResamplingFilter filter, CancellationToken cancellationToken)
    {
        int mapWidth = MapSize(width, baseWidth, map.Width);
        int mapHeight = MapSize(height, baseHeight, map.Height);
        double scaleX = (double)map.Width / baseWidth, scaleY = (double)map.Height / baseHeight;
        double mx0 = x0 * scaleX, my0 = y0 * scaleY, mx1 = x1 * scaleX, my1 = y1 * scaleY;
        var operation = new RegionOfMap(map, mapWidth, mapHeight, mx0, my0, mx1, my1, filter, cancellationToken);
        return PixelDispatch.Run<RegionOfMap, DecodedGainMap>(map.PixelType, ref operation);
    }

    private readonly struct RotateFlipMap(DecodedGainMap map, int quarterTurns, bool flipX) : IPixelOperation<DecodedGainMap>
    {
        public DecodedGainMap Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            TPixel[] source = map.Pixels.As<TPixel>();
            if ((quarterTurns & 1) != 0)
            {
                return new(PixelBuffer.From(Geometry.RotateQuarterPixels(source, map.Width, map.Height, quarterTurns, flipX)), map.Height, map.Width);
            }

            var pixels = (TPixel[])source.Clone();
            Geometry.RotateFlip(pixels, map.Width, map.Height, quarterTurns, flipX);
            return map with { Pixels = PixelBuffer.From(pixels) };
        }
    }

    private readonly struct RegionOfMap(DecodedGainMap map, int mapWidth, int mapHeight, double mx0, double my0, double mx1, double my1,
        ResamplingFilter filter, CancellationToken cancellationToken) : IPixelOperation<DecodedGainMap>
    {
        public DecodedGainMap Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            TPixel[] source = map.Pixels.As<TPixel>();
            if (double.IsInteger(mx0) && double.IsInteger(my0) && double.IsInteger(mx1) && double.IsInteger(my1)
                && mapWidth == (int)(mx1 - mx0) && mapHeight == (int)(my1 - my0))
            {
                var slice = GC.AllocateUninitializedArray<TPixel>(mapWidth * mapHeight);
                for (int row = 0; row < mapHeight; row++)
                {
                    source.AsSpan((((int)my0 + row) * map.Width) + (int)mx0, mapWidth).CopyTo(slice.AsSpan(row * mapWidth, mapWidth));
                }

                return new(PixelBuffer.From(slice), mapWidth, mapHeight);
            }

            TPixel[] pixels = Resizer.Resize(source, map.Width, map.Height, mapWidth, mapHeight, filter,
                (float)mx0, (float)my0, (float)mx1, (float)my1, cancellationToken);
            return new(PixelBuffer.From(pixels), mapWidth, mapHeight);
        }
    }
}
