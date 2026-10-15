namespace PictureSharp.Formats.Png;

/// <summary>PNG color types (IHDR).</summary>
internal enum PngColor : byte
{
    Gray = 0,
    Rgb = 2,
    Palette = 3,
    GrayAlpha = 4,
    Rgba = 6,
}

/// <summary>Shared PNG constants and helpers.</summary>
internal static class PngConstants
{
    public static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    // Adam7 pass geometry: x start, y start, x step, y step.
    public static ReadOnlySpan<byte> Adam7XStart => [0, 4, 0, 2, 0, 1, 0];
    public static ReadOnlySpan<byte> Adam7YStart => [0, 0, 4, 0, 2, 0, 1];
    public static ReadOnlySpan<byte> Adam7XStep => [8, 8, 4, 4, 2, 2, 1];
    public static ReadOnlySpan<byte> Adam7YStep => [8, 8, 8, 4, 4, 2, 2];

    public const string XmpKeyword = "XML:com.adobe.xmp";

    public static int Channels(PngColor color) => color switch
    {
        PngColor.Gray => 1,
        PngColor.Rgb => 3,
        PngColor.Palette => 1,
        PngColor.GrayAlpha => 2,
        PngColor.Rgba => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(color)),
    };

    public static int PassWidth(int width, int pass) =>
        (width - Adam7XStart[pass] + Adam7XStep[pass] - 1) / Adam7XStep[pass];

    public static int PassHeight(int height, int pass) =>
        (height - Adam7YStart[pass] + Adam7YStep[pass] - 1) / Adam7YStep[pass];

    public static long RowBytes(int width, int bitsPerPixel) => (((long)width * bitsPerPixel) + 7) / 8;

    public static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }
}
