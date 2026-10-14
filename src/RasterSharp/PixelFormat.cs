namespace RasterSharp;

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
