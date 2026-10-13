using RasterSharp.Internal;

namespace RasterSharp;

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

/// <summary>PNG scanline filter selection.</summary>
public enum PngFilterStrategy
{
    /// <summary>Choose per row with the minimum-sum-of-absolute-differences heuristic.</summary>
    Adaptive = 0,
    /// <summary>Filter type 0.</summary>
    None,
    /// <summary>Filter type 1.</summary>
    Sub,
    /// <summary>Filter type 2.</summary>
    Up,
    /// <summary>Filter type 3.</summary>
    Average,
    /// <summary>Filter type 4.</summary>
    Paeth,
}

/// <summary>Options for the PNG encoder.</summary>
public sealed record PngEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Png;

    /// <summary>Deflate compression level 0 (store) – 9 (best). Default: 6.</summary>
    public int CompressionLevel { get; init => field = Check.Range(value, 0, 9); } = 6;

    /// <summary>Output color type. Default: automatic.</summary>
    public PngColorType ColorType { get; init => field = Check.Defined(value); } = PngColorType.Auto;

    /// <summary>Scanline filter strategy. Default: adaptive.</summary>
    public PngFilterStrategy FilterStrategy { get; init => field = Check.Defined(value); } = PngFilterStrategy.Adaptive;

    /// <summary>Write an Adam7-interlaced image.</summary>
    public bool Interlace { get; init; }

    /// <summary>Write all frames of animated images as APNG (otherwise only the first frame). Default: true.</summary>
    public bool SaveAnimation { get; init; } = true;
}
