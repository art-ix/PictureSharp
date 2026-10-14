using System.Buffers.Binary;
using System.Drawing;

namespace RasterSharp.Formats.Gif;

/// <summary>Writes GIF blocks from prepared palette, frame and metadata values.</summary>
internal readonly struct GifBlockWriter(Stream stream)
{
    public void WriteHeader(int width, int height, Rgba32[] colors, int backgroundIndex, bool extended)
    {
        int bits = TableBits(colors.Length);
        Span<byte> header = stackalloc byte[13];
        (extended ? "GIF89a"u8 : "GIF87a"u8).CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)height);
        header[10] = (byte)(0x80 | ((bits - 1) << 4) | (bits - 1));
        header[11] = (byte)backgroundIndex;
        header[12] = 0;
        stream.Write(header);
        WriteColorTable(colors, bits);
    }

    public void WriteLoopCount(int repetitions)
    {
        stream.Write([GifConstants.ExtensionIntroducer, GifConstants.ApplicationLabel, 11]);
        stream.Write(GifConstants.Netscape);
        stream.Write([3, 1, (byte)repetitions, (byte)(repetitions >> 8), 0]);
    }

    public void WriteMetadata(byte[]? icc, byte[]? xmp)
    {
        if (icc is not null)
        {
            stream.Write([GifConstants.ExtensionIntroducer, GifConstants.ApplicationLabel, 11]);
            stream.Write(GifConstants.IccIdentifier);
            for (int pos = 0; pos < icc.Length; pos += 255)
            {
                int size = Math.Min(255, icc.Length - pos);
                stream.WriteByte((byte)size);
                stream.Write(icc, pos, size);
            }

            stream.WriteByte(0);
        }

        if (xmp is not null)
        {
            // The packet is stored raw; the "magic trailer" (0x01, 0xFF, 0xFE, …, 0x00) lets sub-block readers skip it.
            stream.Write([GifConstants.ExtensionIntroducer, GifConstants.ApplicationLabel, 11]);
            stream.Write(GifConstants.XmpIdentifier);
            stream.Write(xmp);
            stream.WriteByte(1);
            for (int value = 255; value >= 0; value--)
            {
                stream.WriteByte((byte)value);
            }

            stream.WriteByte(0);
        }
    }

    public void WriteGraphicControl(int delay, int transparentIndex, GifDisposal disposal)
    {
        int packed = ((int)disposal << 2) | (transparentIndex >= 0 ? 1 : 0);
        stream.Write([GifConstants.ExtensionIntroducer, GifConstants.GraphicControlLabel, 4, (byte)packed,
            (byte)delay, (byte)(delay >> 8), (byte)Math.Max(transparentIndex, 0), 0]);
    }

    public void WriteImageDescriptor(Rectangle rectangle, Rgba32[] colors, bool local, bool interlace)
    {
        int bits = TableBits(colors.Length);
        Span<byte> descriptor = stackalloc byte[10];
        descriptor[0] = GifConstants.ImageSeparator;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[1..], (ushort)rectangle.X);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[3..], (ushort)rectangle.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[5..], (ushort)rectangle.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[7..], (ushort)rectangle.Height);
        descriptor[9] = (byte)((local ? 0x80 | (bits - 1) : 0) | (interlace ? 0x40 : 0));
        stream.Write(descriptor);
        if (local)
        {
            WriteColorTable(colors, bits);
        }
    }

    /// <summary>Writes LZW-compressed indices into a palette of <paramref name="colorCount"/> entries.</summary>
    public void WriteImageData(ReadOnlySpan<byte> indices, int colorCount) =>
        GifLzwEncoder.Encode(indices, Math.Max(2, TableBits(colorCount)), stream);

    public void WriteTrailer() => stream.WriteByte(GifConstants.Trailer);

    /// <summary>Number of bits for a color table holding <paramref name="count"/> entries (at least 2 entries).</summary>
    private static int TableBits(int count)
    {
        int bits = 1;
        while ((1 << bits) < count)
        {
            bits++;
        }

        return bits;
    }

    private void WriteColorTable(Rgba32[] colors, int bits)
    {
        var table = new byte[3 << bits];
        for (int i = 0; i < colors.Length; i++)
        {
            table[3 * i] = colors[i].R;
            table[(3 * i) + 1] = colors[i].G;
            table[(3 * i) + 2] = colors[i].B;
        }

        stream.Write(table);
    }
}
