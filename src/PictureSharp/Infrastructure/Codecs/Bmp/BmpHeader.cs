using System.Buffers.Binary;

namespace PictureSharp.Infrastructure.Codecs.Bmp;

/// <summary>The parsed file header and DIB header of a BMP file.</summary>
internal sealed class BmpHeader
{
    public int HeaderSize;
    public int Width;
    public int Height;
    public bool TopDown;
    public int BitCount;
    public uint RawCompression;
    public BmpCompression Compression;
    public uint ImageSize;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ColorsUsed;
    public bool HasMasks;
    public BmpChannel Red;
    public BmpChannel Green;
    public BmpChannel Blue;
    public BmpChannel Alpha;
    public int PaletteOffset;
    public int PaletteEntrySize;
    public int PaletteCount;
    public long DataOffset;
    public int IccOffset = -1;
    public int IccLength;
    public bool IsSrgb;

    public bool HasAlpha => Alpha.Mask != 0;

    public bool IsPaletted => BitCount <= 8 && Compression != BmpCompression.Embedded;

    /// <summary>The stored sample depth: the index depth for palettes, else the widest mask (8 without masks).</summary>
    public int BitsPerSample =>
        IsPaletted ? BitCount
        : (Red.Mask | Green.Mask | Blue.Mask | Alpha.Mask) == 0 ? 8
        : Math.Max(Math.Max(Red.Bits, Green.Bits), Math.Max(Blue.Bits, Alpha.Bits));

    private static ImageFormatException Error(string message) => new(ImageFormat.Bmp, message);

    public static BmpHeader Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < BmpConstants.FileHeaderSize + 4 || !file.StartsWith("BM"u8))
        {
            throw Error("Not a BMP file.");
        }

        uint offBits = BinaryPrimitives.ReadUInt32LittleEndian(file[10..]);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(file[14..]);
        bool core = headerSize == BmpConstants.CoreHeaderSize;
        bool windows = headerSize is 40 or 52 or 56 or 108 or 124;
        bool os2v2 = !windows && headerSize is >= 16 and <= 64;
        if (!core && !windows && !os2v2)
        {
            throw Error($"Unsupported BMP header size {headerSize}.");
        }

        if (BmpConstants.FileHeaderSize + headerSize > file.Length)
        {
            throw Error("The BMP header is truncated.");
        }

        var h = new BmpHeader { HeaderSize = (int)headerSize };
        int planes;
        if (core)
        {
            ReadOnlySpan<byte> b = file.Slice(BmpConstants.FileHeaderSize, BmpConstants.CoreHeaderSize);
            h.Width = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
            h.Height = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]);
            planes = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
            h.BitCount = BinaryPrimitives.ReadUInt16LittleEndian(b[10..]);
            h.RawCompression = BmpConstants.Rgb;
            h.PaletteEntrySize = 3;
        }
        else
        {
            // OS/2 2.x headers may be truncated anywhere after 16 bytes; missing fields read as zero.
            Span<byte> b = stackalloc byte[BmpConstants.V5HeaderSize];
            b.Clear();
            file.Slice(BmpConstants.FileHeaderSize, (int)headerSize).CopyTo(b);
            int width = BinaryPrimitives.ReadInt32LittleEndian(b[4..]);
            int height = BinaryPrimitives.ReadInt32LittleEndian(b[8..]);
            planes = BinaryPrimitives.ReadUInt16LittleEndian(b[12..]);
            h.BitCount = BinaryPrimitives.ReadUInt16LittleEndian(b[14..]);
            h.RawCompression = BinaryPrimitives.ReadUInt32LittleEndian(b[16..]);
            h.ImageSize = BinaryPrimitives.ReadUInt32LittleEndian(b[20..]);
            h.XPelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(b[24..]);
            h.YPelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(b[28..]);
            h.ColorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(b[32..]);
            if (height == int.MinValue)
            {
                throw Error("Invalid BMP height.");
            }

            h.Width = width;
            h.TopDown = height < 0;
            h.Height = Math.Abs(height);
            h.PaletteEntrySize = 4;

            if (h.RawCompression is BmpConstants.BitFields or BmpConstants.AlphaBitFields && !os2v2)
            {
                h.HasMasks = true;
                int maskStart = BmpConstants.FileHeaderSize + (int)headerSize;
                if (headerSize == BmpConstants.InfoHeaderSize)
                {
                    // BITMAPINFOHEADER: the masks follow the header.
                    int count = h.RawCompression == BmpConstants.AlphaBitFields ? 4 : 3;
                    if (maskStart + (4 * count) > file.Length)
                    {
                        throw Error("The BMP color masks are truncated.");
                    }

                    file.Slice(maskStart, 4 * count).CopyTo(b[40..]);
                }

                bool alphaMask = headerSize >= BmpConstants.V3HeaderSize || h.RawCompression == BmpConstants.AlphaBitFields;
                h.Red = new BmpChannel(BinaryPrimitives.ReadUInt32LittleEndian(b[40..]));
                h.Green = new BmpChannel(BinaryPrimitives.ReadUInt32LittleEndian(b[44..]));
                h.Blue = new BmpChannel(BinaryPrimitives.ReadUInt32LittleEndian(b[48..]));
                h.Alpha = new BmpChannel(alphaMask ? BinaryPrimitives.ReadUInt32LittleEndian(b[52..]) : 0);
            }

            // bV4CSType: LCS_WINDOWS_COLOR_SPACE means "system default" and is not reported as sRGB.
            h.IsSrgb = headerSize >= BmpConstants.V4HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(b[56..]) == BmpConstants.ColorSpaceSrgb;
            if (headerSize >= BmpConstants.V5HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(b[56..]) == BmpConstants.ProfileEmbedded)
            {
                long iccOffset = BmpConstants.FileHeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(b[112..]);
                uint iccLength = BinaryPrimitives.ReadUInt32LittleEndian(b[116..]);
                if (iccLength > 0 && iccOffset + iccLength <= file.Length)
                {
                    h.IccOffset = (int)iccOffset;
                    h.IccLength = (int)iccLength;
                }
            }
        }

        if (planes != 1)
        {
            throw Error($"Invalid BMP plane count {planes}.");
        }

        if (h.Width <= 0 || h.Height <= 0)
        {
            throw Error($"Invalid BMP dimensions {h.Width}x{h.Height}.");
        }

        h.Compression = ResolveCompression(h, os2v2 || headerSize == BmpConstants.InfoHeaderSize);
        h.PaletteOffset = BmpConstants.FileHeaderSize + (int)headerSize + (h.HasMasks && headerSize == BmpConstants.InfoHeaderSize
            ? (h.RawCompression == BmpConstants.AlphaBitFields ? 16 : 12)
            : 0);

        if (h.IsPaletted)
        {
            if (h.ColorsUsed > 65536)
            {
                throw Error($"Invalid BMP palette size {h.ColorsUsed}.");
            }

            int declared = h.ColorsUsed == 0 ? 1 << h.BitCount : (int)h.ColorsUsed;
            int count = Math.Min(declared, 1 << h.BitCount);
            if (offBits >= h.PaletteOffset + h.PaletteEntrySize)
            {
                // The palette cannot extend into the pixels (OS/2 1.x has no color count at all).
                count = Math.Min(count, (int)((offBits - h.PaletteOffset) / h.PaletteEntrySize));
            }

            h.PaletteCount = count;
            if (h.PaletteOffset + ((long)count * h.PaletteEntrySize) > file.Length)
            {
                throw Error("The BMP palette is truncated.");
            }

            // Same recovery as Pillow: an offset of 0, or one pointing at the palette, means "right after the palette".
            if (offBits == 0)
            {
                offBits = (uint)(h.PaletteOffset + (declared * h.PaletteEntrySize));
            }
            else if (offBits == BmpConstants.FileHeaderSize + headerSize)
            {
                offBits += (uint)(declared * h.PaletteEntrySize);
            }
        }
        else if (offBits == 0)
        {
            offBits = (uint)h.PaletteOffset;
        }

        h.DataOffset = offBits;
        if (h.DataOffset > file.Length)
        {
            throw Error("The BMP pixel data offset is beyond the end of the file.");
        }

        return h;
    }

    private static BmpCompression ResolveCompression(BmpHeader h, bool maybeOs2)
    {
        int bits = h.BitCount;
        switch (h.RawCompression)
        {
            case BmpConstants.Rgb:
                if (bits is 1 or 2 or 4 or 8 or 16 or 24 or 32)
                {
                    if (bits is 16 or 32)
                    {
                        h.Red = new BmpChannel(bits == 16 ? 0x7C00u : 0xFF0000u);
                        h.Green = new BmpChannel(bits == 16 ? 0x3E0u : 0xFF00u);
                        h.Blue = new BmpChannel(bits == 16 ? 0x1Fu : 0xFFu);
                    }

                    return BmpCompression.None;
                }

                break;
            case BmpConstants.Rle8:
                if (bits == 8)
                {
                    return BmpCompression.Rle8;
                }

                break;
            case BmpConstants.Rle4:
                if (bits == 4)
                {
                    return BmpCompression.Rle4;
                }

                break;
            case BmpConstants.BitFields when maybeOs2 && bits == 1:
                throw new NotSupportedException("OS/2 Huffman 1D compressed bitmaps are not supported.");
            case BmpConstants.Jpeg when maybeOs2 && bits == 24:
                return BmpCompression.Rle24;  // OS/2 2.x reuses value 4 for RLE24
            case BmpConstants.BitFields or BmpConstants.AlphaBitFields:
                if (bits is 16 or 24 or 32)
                {
                    if (h.Red.Mask == 0 && h.Green.Mask == 0 && h.Blue.Mask == 0 && h.Alpha.Mask == 0)
                    {
                        // Empty masks: fall back to the default layout (BGRA for 32 bits, as Pillow does).
                        h.Red = new BmpChannel(bits == 16 ? 0x7C00u : 0xFF0000u);
                        h.Green = new BmpChannel(bits == 16 ? 0x3E0u : 0xFF00u);
                        h.Blue = new BmpChannel(bits == 16 ? 0x1Fu : 0xFFu);
                        h.Alpha = new BmpChannel(bits == 32 ? 0xFF000000u : 0);
                    }

                    ValidateMask(h.Red, bits);
                    ValidateMask(h.Green, bits);
                    ValidateMask(h.Blue, bits);
                    ValidateMask(h.Alpha, bits);
                    return BmpCompression.None;
                }

                break;
            case BmpConstants.Jpeg or BmpConstants.Png:
                return BmpCompression.Embedded;
            case >= BmpConstants.Cmyk and <= BmpConstants.CmykRle4:
                throw new NotSupportedException("CMYK bitmaps are not supported.");
            default:
                throw Error($"Unknown BMP compression {h.RawCompression}.");
        }

        if (bits == 64)
        {
            throw new NotSupportedException("64-bit bitmaps are not supported.");
        }

        throw Error($"Invalid BMP bit count {bits} for compression {h.RawCompression}.");
    }

    private static void ValidateMask(BmpChannel channel, int bits)
    {
        if (!channel.IsContiguous || (bits < 32 && channel.Mask >> bits != 0))
        {
            throw Error($"Invalid BMP color mask 0x{channel.Mask:X8}.");
        }
    }
}
