namespace PictureSharp;

/// <summary>PNG color type selection.</summary>
public enum PngColorType
{
    /// <summary>Choose the smallest lossless representation (palette, grayscale, RGB or RGBA).</summary>
    Auto = 0,
    /// <summary>8-bit grayscale (color is converted to luminance).</summary>
    Grayscale,
    /// <summary>8-bit grayscale with alpha.</summary>
    GrayscaleAlpha,
    /// <summary>24-bit RGB (alpha is dropped).</summary>
    Rgb,
    /// <summary>32-bit RGBA.</summary>
    Rgba,
    /// <summary>Indexed color; fails if the image has more than 256 distinct colors.</summary>
    Palette,
}
