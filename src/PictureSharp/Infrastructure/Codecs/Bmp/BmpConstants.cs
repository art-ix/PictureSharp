namespace PictureSharp.Infrastructure.Codecs.Bmp;

internal static class BmpConstants
{
    public const int FileHeaderSize = 14;
    public const int CoreHeaderSize = 12;       // BITMAPCOREHEADER (OS/2 1.x)
    public const int InfoHeaderSize = 40;       // BITMAPINFOHEADER
    public const int V3HeaderSize = 56;         // BITMAPV3INFOHEADER (with alpha mask)
    public const int V5HeaderSize = 124;        // BITMAPV5HEADER

    // biCompression values
    public const uint Rgb = 0;
    public const uint Rle8 = 1;
    public const uint Rle4 = 2;
    public const uint BitFields = 3;
    public const uint Jpeg = 4;
    public const uint Png = 5;
    public const uint AlphaBitFields = 6;
    public const uint Cmyk = 11;
    public const uint CmykRle4 = 13;

    // bV5CSType values (multi-character constants, stored little-endian)
    public const uint ColorSpaceSrgb = 0x73524742;       // 'sRGB'
    public const uint ProfileEmbedded = 0x4D424544;      // 'MBED'
    public const uint IntentImages = 4;                  // LCS_GM_IMAGES
}

internal enum BmpCompression
{
    None,
    Rle8,
    Rle4,
    Rle24,
    Embedded,
}
