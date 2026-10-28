namespace PictureSharp.Formats.Png;

/// <summary>PNG color type selection.</summary>
public enum PngColorType
{
    /// <summary>Choose the smallest lossless representation (palette, grayscale, RGB or RGBA).</summary>
    Auto = 0,
    /// <summary>Grayscale (color is converted to luminance), 8 or 16 bits per sample.</summary>
    Grayscale,
    /// <summary>Grayscale with alpha, 8 or 16 bits per sample.</summary>
    GrayscaleAlpha,
    /// <summary>RGB, 8 or 16 bits per sample (alpha is dropped).</summary>
    Rgb,
    /// <summary>RGBA, 8 or 16 bits per sample.</summary>
    Rgba,
    /// <summary>Indexed color; fails if the image has more than 256 distinct colors.</summary>
    Palette,
}
