using System.Drawing;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>Multiplies the alpha of a frame by a mask; color channels are never changed.</summary>
internal static class AlphaMask
{
    /// <summary>
    /// A' = round(A · v / 255), where v is the mask value at the pixel (tests/assets/graphics_reference.py apply_mask).
    /// The mask's top-left corner is at <paramref name="at"/>; pixels outside it keep their alpha or become transparent.
    /// </summary>
    public static void Apply(ImageFrame frame, ReadOnlySpan<Rgba32> mask, int maskWidth, int maskHeight, Point at, MaskOptions options)
    {
        int x0 = Math.Clamp(at.X, 0, frame.Width);
        int y0 = Math.Clamp(at.Y, 0, frame.Height);
        int x1 = (int)Math.Clamp((long)at.X + maskWidth, 0, frame.Width);
        int y1 = (int)Math.Clamp((long)at.Y + maskHeight, 0, frame.Height);
        bool clearOutside = options.Outside == MaskOutside.Transparent;
        bool luminance = options.Channel == MaskChannel.Luminance;
        int flip = options.Invert ? 255 : 0;
        for (int y = 0; y < frame.Height; y++)
        {
            Span<Rgba32> row = frame.GetRowSpan(y);
            if (y < y0 || y >= y1 || x0 >= x1)
            {
                if (clearOutside)
                {
                    ClearAlpha(row);
                }

                continue;
            }

            if (clearOutside)
            {
                ClearAlpha(row[..x0]);
                ClearAlpha(row[x1..]);
            }

            ReadOnlySpan<Rgba32> maskRow = mask.Slice(((y - at.Y) * maskWidth) + (x0 - at.X), x1 - x0);
            Span<Rgba32> target = row[x0..x1];
            for (int x = 0; x < target.Length; x++)
            {
                Rgba32 m = maskRow[x];
                int v = (luminance ? PixelConverter.Luma(m.R, m.G, m.B) : m.A) ^ flip;
                target[x].A = Multiply(target[x].A, v);
            }
        }
    }

    // round(a · v / 255): an exact half never occurs because 255 is odd.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Multiply(byte a, int v) => (byte)(((a * v * 2) + 255) / 510);

    private static void ClearAlpha(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i].A = 0;
        }
    }
}
