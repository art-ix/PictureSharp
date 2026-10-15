namespace PictureSharp;

/// <summary>Options for the PCX encoder. PCX has no ICC, EXIF or XMP; resolution is taken from <see cref="Image.Metadata"/>.</summary>
public sealed record PcxEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Pcx;

    /// <summary>
    /// Bits per pixel: 1 (two colors), 4 (up to 16 colors, four 1-bit planes), 8 (up to 256 colors and a VGA palette),
    /// 24 (RGB, three 8-bit planes) or 32 (RGBA, four 8-bit planes), or 0 to choose automatically.
    /// Automatic writes 32 bits when any pixel is transparent, otherwise the smallest palette that holds the colors
    /// (one 1-bit plane for two colors, two for four, four for sixteen, then 8 bits) and 24 bits above 256 colors.
    /// Forced 1, 4 and 8 keep RGB colors exactly and reject images that do not fit. All depths except 32 drop alpha.
    /// Three to four colors in automatic mode are stored as two 1-bit planes. Default: 0.
    /// </summary>
    public int BitsPerPixel
    {
        get;
        init => field = value is 0 or 1 or 4 or 8 or 24 or 32
            ? value
            : throw new ArgumentOutOfRangeException(nameof(BitsPerPixel), value, "Expected 0, 1, 4, 8, 24 or 32.");
    }
}
