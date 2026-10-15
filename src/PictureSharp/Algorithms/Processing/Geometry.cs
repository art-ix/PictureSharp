using System.Drawing;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>Lossless geometric transforms of frame buffers.</summary>
internal static class Geometry
{
    public static Rgba32[] Crop(ImageFrame frame, Rectangle rect)
    {
        var dst = GC.AllocateUninitializedArray<Rgba32>(rect.Width * rect.Height);
        for (int y = 0; y < rect.Height; y++)
        {
            frame.GetRowSpan(rect.Y + y).Slice(rect.X, rect.Width).CopyTo(dst.AsSpan(y * rect.Width, rect.Width));
        }

        return dst;
    }

    /// <summary>The internal form of <paramref name="rotate"/> followed by <paramref name="flip"/>.</summary>
    public static (int QuarterTurns, bool FlipX) ToQuarterTurns(RotateMode rotate, FlipMode flip)
    {
        int turns = rotate switch
        {
            RotateMode.None => 0,
            RotateMode.Rotate90 => 1,
            RotateMode.Rotate180 => 2,
            RotateMode.Rotate270 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(rotate), rotate, "Unknown rotate mode."),
        };

        // A vertical flip equals a half turn followed by a horizontal flip.
        return flip switch
        {
            FlipMode.None => (turns, false),
            FlipMode.Horizontal => (turns, true),
            FlipMode.Vertical => ((turns + 2) & 3, true),
            _ => throw new ArgumentOutOfRangeException(nameof(flip), flip, "Unknown flip mode."),
        };
    }

    /// <summary>
    /// Rotates the frame by <paramref name="rotation"/> quarter turns clockwise, then mirrors it horizontally when
    /// <paramref name="flipX"/> is set. Even turns work in place; odd turns use RotateQuarterPixels instead.
    /// </summary>
    public static void RotateFlip(ImageFrame frame, int rotation, bool flipX)
    {
        int w = frame.Width;
        int h = frame.Height;
        Rgba32[] src = frame.Pixels;

        if (rotation == 0 && !flipX)
        {
            return;
        }

        if (rotation == 2 && flipX)
        {
            // Rotate180 + FlipX == FlipY: swap rows.
            for (int y = 0; y < h / 2; y++)
            {
                Span<Rgba32> top = src.AsSpan(y * w, w);
                Span<Rgba32> bottom = src.AsSpan((h - 1 - y) * w, w);
                for (int x = 0; x < w; x++)
                {
                    (top[x], bottom[x]) = (bottom[x], top[x]);
                }
            }

            return;
        }

        if (rotation == 0 || rotation == 2)
        {
            if (rotation == 2)
            {
                src.AsSpan().Reverse();
            }

            if (flipX)
            {
                for (int y = 0; y < h; y++)
                {
                    src.AsSpan(y * w, w).Reverse();
                }
            }

            return;
        }

        throw new ArgumentException("Odd rotations must produce buffers for aggregate publication.", nameof(rotation));
    }

    /// <summary>Produces a new buffer for an odd number of quarter turns.</summary>
    public static Rgba32[] RotateQuarterPixels(Rgba32[] src, int w, int h, int rotation, bool flipX)
    {
        // 90° or 270°: dimensions swap.
        int nw = h;
        int nh = w;
        var dst = GC.AllocateUninitializedArray<Rgba32>(src.Length);
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<Rgba32> row = src.AsSpan(y * w, w);
            for (int x = 0; x < w; x++)
            {
                int dx, dy;
                if (rotation == 1)
                {
                    dx = h - 1 - y;
                    dy = x;
                }
                else
                {
                    dx = y;
                    dy = w - 1 - x;
                }

                if (flipX)
                {
                    dx = nw - 1 - dx;
                }

                dst[(dy * nw) + dx] = row[x];
            }
        }

        return dst;
    }

    /// <summary>The transform (quarter turns, then horizontal flip) that turns an image stored with EXIF <paramref name="orientation"/> upright.</summary>
    public static (int QuarterTurns, bool FlipX) FromExifOrientation(ExifOrientation orientation) => orientation switch
    {
        ExifOrientation.TopRight => (0, true),
        ExifOrientation.BottomRight => (2, false),
        ExifOrientation.BottomLeft => (2, true),
        ExifOrientation.LeftTop => (1, true),
        ExifOrientation.RightTop => (1, false),
        ExifOrientation.RightBottom => (3, true),
        ExifOrientation.LeftBottom => (3, false),
        _ => (0, false),
    };

    /// <summary>
    /// Copies a <paramref name="srcWidth"/>-wide image onto <paramref name="dst"/> with its top-left corner at
    /// (<paramref name="x"/>, <paramref name="y"/>), clipped to the destination (no blending).
    /// </summary>
    public static void Place(ReadOnlySpan<Rgba32> src, int srcWidth, int srcHeight, Span<Rgba32> dst, int dstWidth, int dstHeight, int x, int y)
    {
        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = (int)Math.Min(dstWidth, (long)x + srcWidth);
        int y1 = (int)Math.Min(dstHeight, (long)y + srcHeight);
        for (int row = y0; row < y1; row++)
        {
            src.Slice(((row - y) * srcWidth) + (x0 - x), x1 - x0).CopyTo(dst.Slice((row * dstWidth) + x0, x1 - x0));
        }
    }
}
