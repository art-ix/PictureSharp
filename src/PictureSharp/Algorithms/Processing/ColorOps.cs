using System.Drawing;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

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

    /// <summary>Composites the pixels over <paramref name="background"/>.</summary>
    public static void BackgroundColor(Span<Rgba32> pixels, Rgba32 background)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = Compositor.Over(background, pixels[i]);
        }
    }

    /// <summary>Scales alpha by <paramref name="opacity"/> with the rounding of <see cref="Compositor.DrawOver"/>.</summary>
    public static void Opacity(Span<Rgba32> pixels, float opacity)
    {
        Span<byte> alphaMap = stackalloc byte[256];
        Compositor.FillAlphaMap(alphaMap, opacity);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i].A = alphaMap[pixels[i].A];
        }
    }

    /// <summary>Applies the classic sepia matrix in fixed point (thousandths), rounding half up and clamping.</summary>
    public static void Sepia(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            int r = ((393 * p.R) + (769 * p.G) + (189 * p.B) + 500) / 1000;
            int g = ((349 * p.R) + (686 * p.G) + (168 * p.B) + 500) / 1000;
            int b = ((272 * p.R) + (534 * p.G) + (131 * p.B) + 500) / 1000;
            pixels[i] = new Rgba32((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255), p.A);
        }
    }
}
