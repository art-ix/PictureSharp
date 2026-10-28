using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PictureSharp.Domain.Pixels;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>Lossless geometric transforms of frame buffers, generic over the storage pixel type.</summary>
internal static partial class Geometry
{
    public static Rgba32[] Crop(ImageFrame frame, Rectangle rect) => Crop(frame.Pixels, frame.Width, rect);

    public static TPixel[] Crop<TPixel>(TPixel[] src, int width, Rectangle rect) where TPixel : unmanaged
    {
        var dst = GC.AllocateUninitializedArray<TPixel>(rect.Width * rect.Height);
        for (int y = 0; y < rect.Height; y++)
        {
            src.AsSpan(((rect.Y + y) * width) + rect.X, rect.Width).CopyTo(dst.AsSpan(y * rect.Width, rect.Width));
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
        var operation = new RotateFlipInPlace(frame, rotation, flipX);
        PixelDispatch.Run<RotateFlipInPlace, bool>(frame.PixelType, ref operation);
    }

    /// <summary>Rotates <paramref name="src"/> in place by an even number of quarter turns, then mirrors it when <paramref name="flipX"/> is set.</summary>
    public static void RotateFlip<TPixel>(TPixel[] src, int w, int h, int rotation, bool flipX) where TPixel : unmanaged
    {
        if (rotation == 0 && !flipX)
        {
            return;
        }

        if (rotation == 2 && flipX)
        {
            // Rotate180 + FlipX == FlipY: swap rows.
            for (int y = 0; y < h / 2; y++)
            {
                Span<TPixel> top = src.AsSpan(y * w, w);
                Span<TPixel> bottom = src.AsSpan((h - 1 - y) * w, w);
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
    /// <remarks>
    /// Output pixel (dx, dy) comes from source column x = dy (90°) or w - 1 - dy (270°) and row y = dx, or h - 1 - dx when
    /// exactly one of "90°" and <paramref name="flipX"/> holds. The output is filled in 32x32 tiles so that the column
    /// reads of the source stay within a few kilobytes instead of striding through the whole image.
    /// </remarks>
    public static TPixel[] RotateQuarterPixels<TPixel>(TPixel[] src, int w, int h, int rotation, bool flipX, CancellationToken cancellationToken = default)
        where TPixel : unmanaged
    {
        const int Tile = 32;
        // 90° or 270°: dimensions swap.
        int nw = h;
        int nh = w;
        if (src.Length < (long)w * h)
        {
            throw new ArgumentException("The buffer is smaller than the image.", nameof(src));
        }

        var dst = GC.AllocateUninitializedArray<TPixel>(src.Length);
        bool reverseX = rotation != 1, reverseY = (rotation == 1) != flipX;
        ref TPixel source = ref MemoryMarshal.GetArrayDataReference(src);
        for (int dy0 = 0; dy0 < nh; dy0 += Tile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int dy1 = Math.Min(dy0 + Tile, nh);
            for (int dx0 = 0; dx0 < nw; dx0 += Tile)
            {
                int dx1 = Math.Min(dx0 + Tile, nw);
                for (int dy = dy0; dy < dy1; dy++)
                {
                    int x = reverseX ? w - 1 - dy : dy;
                    Span<TPixel> row = dst.AsSpan((dy * nw) + dx0, dx1 - dx0);
                    for (int i = 0; i < row.Length; i++)
                    {
                        int dx = dx0 + i, y = reverseY ? h - 1 - dx : dx;
                        // 0 <= x < w and 0 <= y < h, so the index is inside the checked length.
                        row[i] = Unsafe.Add(ref source, (y * w) + x);
                    }
                }
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
    public static void Place<TPixel>(ReadOnlySpan<TPixel> src, int srcWidth, int srcHeight, Span<TPixel> dst, int dstWidth, int dstHeight, int x, int y)
        where TPixel : unmanaged
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
