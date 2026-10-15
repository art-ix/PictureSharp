namespace PictureSharp;

/// <summary>Options for the BMP encoder.</summary>
public sealed record BmpEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Bmp;

    /// <summary>
    /// Bits per pixel: 1, 4 or 8 (palette), 24 (BGR) or 32 (BGRA with alpha), or 0 to choose automatically:
    /// a palette when the image is opaque and has at most 256 colors, 24 bits when it is opaque, otherwise 32 bits.
    /// Palette and 24-bit output drop the alpha channel. Default: 0.
    /// </summary>
    public int BitsPerPixel
    {
        get;
        init => field = value is 0 or 1 or 4 or 8 or 24 or 32 ? value : throw new ArgumentOutOfRangeException(nameof(BitsPerPixel), value, "Expected 0, 1, 4, 8, 24 or 32.");
    }
}
