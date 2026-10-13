using System.Drawing;

namespace RasterSharp.Processing;

/// <summary>Per-pixel color operations on frame buffers.</summary>
internal static class ColorOps
{
    /// <summary>Replaces the color with its BT.601 luma (Pillow's "L" conversion); alpha is kept.</summary>
    public static void Grayscale(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            byte l = PixelConverter.Luma(p.R, p.G, p.B);
            pixels[i] = new Rgba32(l, l, l, p.A);
        }
    }

    /// <summary>Inverts the color channels; alpha is kept.</summary>
    public static void Invert(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            pixels[i] = new Rgba32((byte)(255 - p.R), (byte)(255 - p.G), (byte)(255 - p.B), p.A);
        }
    }

    /// <summary>Overwrites <paramref name="rect"/> (already clipped to the frame) with <paramref name="value"/>.</summary>
    public static void Fill(ImageFrame frame, Rectangle rect, Rgba32 value)
    {
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            frame.GetRowSpan(y).Slice(rect.X, rect.Width).Fill(value);
        }
    }

    /// <summary>Sets alpha to 0 on pixels equal to <paramref name="key"/>, keeping their color.</summary>
    public static void MakeTransparent(Span<Rgba32> pixels, Rgba32 key)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] == key)
            {
                pixels[i].A = 0;
            }
        }
    }
}
