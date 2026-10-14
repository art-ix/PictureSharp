using System.Buffers.Binary;

namespace RasterSharp.Formats.Pcx;

/// <summary>The 128-byte ZSoft PCX header.</summary>
internal readonly record struct PcxHeader(
    int Version,
    int Encoding,
    int BitsPerPixel,
    int Planes,
    int BytesPerLine,
    int Width,
    int Height,
    int HorizontalDpi,
    int VerticalDpi)
{
    public bool HasAlphaPlane => BitsPerPixel == 8 && Planes == 4;

    public int Mode => (Planes << 8) | BitsPerPixel;

    public bool HasHeaderPalette => Version is 2 or 4 or 5;

    public bool IsSupported =>
        (Mode is 0x0101 or 0x0201 or 0x0301 or 0x0401 or 0x0102 or 0x0104 or 0x0108 or 0x0308 or 0x0408)
        && (BitsPerPixel == 8 || Mode == 0x0101 || HasHeaderPalette);

    public (float X, float Y) Resolution =>
        HorizontalDpi > 0 && VerticalDpi > 0 ? (HorizontalDpi, VerticalDpi) : (96f, 96f);

    public static PcxHeader Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < PcxConstants.HeaderSize)
        {
            throw Error("The PCX header is truncated.");
        }

        if (file[0] != PcxConstants.Manufacturer || file[1] is not (0 or 2 or 3 or 4 or 5))
        {
            throw Error("The data is not a PCX image.");
        }

        int encoding = file[2];
        int bits = file[3];
        if (encoding > PcxConstants.EncodingRle || bits is not (1 or 2 or 4 or 8))
        {
            throw Error("The PCX header is invalid.");
        }

        int xMin = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        int yMin = BinaryPrimitives.ReadUInt16LittleEndian(file[6..]);
        int xMax = BinaryPrimitives.ReadUInt16LittleEndian(file[8..]);
        int yMax = BinaryPrimitives.ReadUInt16LittleEndian(file[10..]);
        if (xMax < xMin || yMax < yMin)
        {
            throw Error("Invalid PCX image dimensions.");
        }

        int width = xMax - xMin + 1;
        int height = yMax - yMin + 1;
        int planes = file[65];
        int bytesPerLine = BinaryPrimitives.ReadUInt16LittleEndian(file[66..]);
        if (planes == 0)
        {
            throw Error("The PCX header is invalid.");
        }

        int minimum = (int)(((long)width * bits + 7) / 8);
        // BytesPerLine describes stored padding, which need not be the minimum aligned stride.
        if (bytesPerLine < minimum)
        {
            throw Error("The PCX scanline stride is invalid.");
        }

        return new PcxHeader(
            file[1],
            encoding,
            bits,
            planes,
            bytesPerLine,
            width,
            height,
            BinaryPrimitives.ReadUInt16LittleEndian(file[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(file[14..]));
    }

    private static ImageFormatException Error(string message) => new(ImageFormat.Pcx, message);
}
