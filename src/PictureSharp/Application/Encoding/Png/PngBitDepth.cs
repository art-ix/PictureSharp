namespace PictureSharp.Formats.Png;

/// <summary>PNG sample depth selection.</summary>
public enum PngBitDepth
{
    /// <summary>
    /// 8-bit images keep the automatic layout (1, 2, 4 or 8 bits). 16-bit images are written with 16 bits per
    /// sample, unless every sample of every frame is an exact 8-bit value (a multiple of 257, e.g. a widened 8-bit
    /// image), which is written with 8 bits without loss; a palette is chosen only in that case.
    /// </summary>
    Auto = 0,
    /// <summary>At most 8 bits per sample: 16-bit images are rounded to 8 bits, then the layout is chosen as for 8-bit images.</summary>
    UpTo8,
    /// <summary>16 bits per sample (grey, grey with alpha, RGB or RGBA); 8-bit images are widened exactly.</summary>
    Sixteen,
}
