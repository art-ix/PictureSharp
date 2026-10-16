using System.Buffers;
using System.Runtime.InteropServices;

namespace PictureSharp.Drawing;

/// <summary>
/// Row copies between PictureSharp pixels and a locked native bitmap buffer. Independent of GDI+, so the stride
/// handling (padding and negative, bottom-up strides) is testable on every platform.
/// </summary>
internal static class BitmapPixels
{
    /// <summary>Writes <paramref name="frame"/> converted to <paramref name="format"/> into rows starting at <paramref name="scan0"/>.</summary>
    public static void Write(ImageFrame frame, IntPtr scan0, int stride, PixelFormat format)
    {
        int rowBytes = RowBytes(frame.Width, format, stride);
        int length = checked(rowBytes * frame.Height);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            frame.CopyPixelDataTo(buffer.AsSpan(0, length), format);
            for (int y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(buffer, y * rowBytes, scan0 + ((nint)y * stride), rowBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads rows of <paramref name="format"/> starting at <paramref name="scan0"/> into a new image; a format without
    /// alpha leaves <see cref="Image.HasAlpha"/> false.
    /// </summary>
    public static Image Read(IntPtr scan0, int stride, int width, int height, PixelFormat format)
    {
        int rowBytes = RowBytes(width, format, stride);
        int length = checked(rowBytes * height);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(scan0 + ((nint)y * stride), buffer, y * rowBytes, rowBytes);
            }

            return Image.LoadPixelData(buffer.AsSpan(0, length), width, height, format);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int RowBytes(int width, PixelFormat format, int stride)
    {
        int rowBytes = checked(width * format.BytesPerPixel());
        if (stride == int.MinValue || Math.Abs(stride) < rowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "The stride is smaller than one row of pixels.");
        }

        return rowBytes;
    }
}
