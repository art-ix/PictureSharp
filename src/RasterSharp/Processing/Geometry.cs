using System.Drawing;

namespace RasterSharp.Processing;

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

    /// <summary>Applies <paramref name="type"/> to the frame in place.</summary>
    public static void RotateFlip(ImageFrame frame, RotateFlipType type)
    {
        int w = frame.Width;
        int h = frame.Height;
        Rgba32[] src = frame.Pixels;
        int rotation = (int)type & 3;   // quarter turns clockwise
        bool flipX = (int)type >= 4;    // horizontal flip applied after rotation

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

        frame.Replace(nw, nh, dst);
    }

    /// <summary>The transform that turns an image stored with EXIF <paramref name="orientation"/> upright.</summary>
    public static RotateFlipType FromExifOrientation(ExifOrientation orientation) => orientation switch
    {
        ExifOrientation.TopRight => RotateFlipType.RotateNoneFlipX,
        ExifOrientation.BottomRight => RotateFlipType.Rotate180FlipNone,
        ExifOrientation.BottomLeft => RotateFlipType.RotateNoneFlipY,
        ExifOrientation.LeftTop => RotateFlipType.Rotate90FlipX,
        ExifOrientation.RightTop => RotateFlipType.Rotate90FlipNone,
        ExifOrientation.RightBottom => RotateFlipType.Rotate270FlipX,
        ExifOrientation.LeftBottom => RotateFlipType.Rotate270FlipNone,
        _ => RotateFlipType.RotateNoneFlipNone,
    };
}
