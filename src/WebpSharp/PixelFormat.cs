namespace WebpSharp;

/// <summary>Memory layouts used when importing or exporting raw pixel data.</summary>
public enum PixelFormat
{
    /// <summary>4 bytes per pixel: R, G, B, A (straight alpha).</summary>
    Rgba32,
    /// <summary>4 bytes per pixel: B, G, R, A (straight alpha). Matches System.Drawing Format32bppArgb and WPF Bgra32.</summary>
    Bgra32,
    /// <summary>4 bytes per pixel: A, R, G, B (straight alpha).</summary>
    Argb32,
    /// <summary>3 bytes per pixel: R, G, B.</summary>
    Rgb24,
    /// <summary>3 bytes per pixel: B, G, R. Matches System.Drawing Format24bppRgb.</summary>
    Bgr24,
    /// <summary>1 byte per pixel: luminance (BT.601).</summary>
    Gray8,
    /// <summary>4 bytes per pixel: R, G, B, A with color channels premultiplied by alpha.</summary>
    Rgba32Premultiplied,
    /// <summary>4 bytes per pixel: B, G, R, A with color channels premultiplied by alpha. Matches Format32bppPArgb and WPF Pbgra32.</summary>
    Bgra32Premultiplied,
}

/// <summary>Helpers for <see cref="PixelFormat"/>.</summary>
public static class PixelFormatExtensions
{
    /// <summary>Number of bytes occupied by one pixel in the given format.</summary>
    public static int BytesPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.Rgb24 or PixelFormat.Bgr24 => 3,
        PixelFormat.Gray8 => 1,
        PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Argb32
            or PixelFormat.Rgba32Premultiplied or PixelFormat.Bgra32Premultiplied => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
    };

    /// <summary>Whether the format carries an alpha channel.</summary>
    public static bool HasAlpha(this PixelFormat format) =>
        format is not (PixelFormat.Rgb24 or PixelFormat.Bgr24 or PixelFormat.Gray8);
}
