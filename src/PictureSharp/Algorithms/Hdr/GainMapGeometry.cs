using PictureSharp.Algorithms.Processing;
using PictureSharp.Domain.Hdr;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>
/// Carries a decoded gain map through the geometric edits of its base image. The map covers the whole base canvas:
/// map pixel i spans base pixels [i·W/mw, (i+1)·W/mw). Quarter turns and flips are the same array operations as on
/// the base; every other edit resamples the map's 8-bit codes from the edit's source box converted to map
/// coordinates, or slices the map when that box is integral and keeps its size (plan.md 0.6.9; model in
/// tests/assets/hdr_reference.py edits()).
/// </summary>
internal static class GainMapGeometry
{
    /// <summary>The map extent for a base extent of <paramref name="newBase"/>: max(1, round(newBase·map/base)), halves up.</summary>
    public static int MapSize(int newBase, int baseSize, int mapSize) =>
        (int)Math.Max(1, ((2L * newBase * mapSize) + baseSize) / (2L * baseSize));

    /// <summary>Rotates by <paramref name="quarterTurns"/> clockwise, then mirrors horizontally when <paramref name="flipX"/> is set.</summary>
    public static DecodedGainMap RotateFlip(DecodedGainMap map, int quarterTurns, bool flipX)
    {
        if ((quarterTurns & 1) != 0)
        {
            return new(Geometry.RotateQuarterPixels(map.Pixels, map.Width, map.Height, quarterTurns, flipX), map.Height, map.Width);
        }

        var pixels = (Rgba32[])map.Pixels.Clone();
        Geometry.RotateFlip(pixels, map.Width, map.Height, quarterTurns, flipX);
        return map with { Pixels = pixels };
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
        if (double.IsInteger(mx0) && double.IsInteger(my0) && double.IsInteger(mx1) && double.IsInteger(my1)
            && mapWidth == (int)(mx1 - mx0) && mapHeight == (int)(my1 - my0))
        {
            return new(Slice(map, (int)mx0, (int)my0, mapWidth, mapHeight), mapWidth, mapHeight);
        }

        Rgba32[] pixels = Resizer.Resize(map.Pixels, map.Width, map.Height, mapWidth, mapHeight, filter,
            (float)mx0, (float)my0, (float)mx1, (float)my1, cancellationToken);
        return new(pixels, mapWidth, mapHeight);
    }

    private static Rgba32[] Slice(DecodedGainMap map, int x, int y, int width, int height)
    {
        var pixels = GC.AllocateUninitializedArray<Rgba32>(width * height);
        for (int row = 0; row < height; row++)
        {
            map.Pixels.AsSpan(((y + row) * map.Width) + x, width).CopyTo(pixels.AsSpan(row * width, width));
        }

        return pixels;
    }
}
