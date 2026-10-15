using System.Drawing;
using System.Runtime.CompilerServices;

namespace PictureSharp.Algorithms.Processing;

/// <summary>Source-over compositing of straight-alpha buffers.</summary>
internal static class Compositor
{
    // port: Pillow src/libImaging/AlphaComposite.c PRECISION_BITS
    private const int PrecisionBits = 7;

    /// <summary>
    /// Draws <paramref name="src"/> over <paramref name="dst"/> with its top-left corner at <paramref name="at"/>,
    /// clipped to the destination. <paramref name="opacity"/> scales the source alpha.
    /// </summary>
    public static void DrawOver(ImageFrame dst, ReadOnlySpan<Rgba32> src, int srcWidth, int srcHeight, Point at, float opacity)
    {
        int x0 = Math.Max(0, at.X);
        int y0 = Math.Max(0, at.Y);
        int x1 = (int)Math.Min(dst.Width, (long)at.X + srcWidth);
        int y1 = (int)Math.Min(dst.Height, (long)at.Y + srcHeight);
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        Span<byte> alphaMap = stackalloc byte[256];
        FillAlphaMap(alphaMap, opacity);

        for (int y = y0; y < y1; y++)
        {
            Span<Rgba32> dstRow = dst.GetRowSpan(y).Slice(x0, x1 - x0);
            ReadOnlySpan<Rgba32> srcRow = src.Slice(((y - at.Y) * srcWidth) + (x0 - at.X), x1 - x0);
            for (int x = 0; x < dstRow.Length; x++)
            {
                Rgba32 s = srcRow[x];
                s.A = alphaMap[s.A];
                dstRow[x] = Over(dstRow[x], s);
            }
        }
    }

    /// <summary>Fills <paramref name="map"/>[a] with a × <paramref name="opacity"/>, rounded half up.</summary>
    public static void FillAlphaMap(Span<byte> map, float opacity)
    {
        for (int a = 0; a < map.Length; a++)
        {
            map[a] = (byte)(int)((a * opacity) + 0.5f);
        }
    }

    // port: Pillow src/libImaging/AlphaComposite.c ImagingAlphaComposite (inner loop)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgba32 Over(Rgba32 dst, Rgba32 src)
    {
        if (src.A == 0)
        {
            return dst;
        }

        uint blend = (uint)dst.A * (uint)(255 - src.A);
        uint outa255 = ((uint)src.A * 255) + blend;
        uint coef1 = (uint)src.A * 255 * 255 * (1 << PrecisionBits) / outa255;
        uint coef2 = (255 * (1 << PrecisionBits)) - coef1;
        uint r = (src.R * coef1) + (dst.R * coef2);
        uint g = (src.G * coef1) + (dst.G * coef2);
        uint b = (src.B * coef1) + (dst.B * coef2);
        return new Rgba32(
            (byte)(ShiftForDiv255(r + (0x80 << PrecisionBits)) >> PrecisionBits),
            (byte)(ShiftForDiv255(g + (0x80 << PrecisionBits)) >> PrecisionBits),
            (byte)(ShiftForDiv255(b + (0x80 << PrecisionBits)) >> PrecisionBits),
            (byte)ShiftForDiv255(outa255 + 0x80));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ShiftForDiv255(uint a) => ((a >> 8) + a) >> 8;
}
