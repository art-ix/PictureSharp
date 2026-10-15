namespace PictureSharp;

/// <summary>Helpers for <see cref="PixelFormat"/>.</summary>
public static class PixelFormatExtensions
{
    extension(PixelFormat format)
    {
        /// <summary>Number of bytes occupied by one pixel in the given format.</summary>
        public int BytesPerPixel() => format switch
        {
            PixelFormat.Rgb24 or PixelFormat.Bgr24 => 3,
            PixelFormat.Gray8 => 1,
            PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Argb32
                or PixelFormat.Rgba32Premultiplied or PixelFormat.Bgra32Premultiplied => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
        };

        /// <summary>Whether the format carries an alpha channel.</summary>
        public bool HasAlpha() => format is not (PixelFormat.Rgb24 or PixelFormat.Bgr24 or PixelFormat.Gray8);
    }
}
