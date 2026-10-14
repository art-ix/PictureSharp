namespace RasterSharp.Formats.Pcx;

/// <summary>PCX run-length coding of one uncompressed scanline.</summary>
internal static class PcxRle
{
    // ZSoft PCX: a byte below 0xC0 is a literal. Otherwise the low 6 bits are a repeat count (0–63)
    // of the next byte. The uncompressed scanline is Planes * BytesPerLine bytes, so a run may cross
    // color planes; it must not extend past that scanline.
    public static void Decode(ReadOnlySpan<byte> source, ref int offset, Span<byte> row, bool compressed)
    {
        if (!compressed)
        {
            if ((uint)offset > (uint)source.Length || row.Length > source.Length - offset)
            {
                throw Truncated();
            }

            source.Slice(offset, row.Length).CopyTo(row);
            offset += row.Length;
            return;
        }

        int filled = 0;
        while (filled < row.Length)
        {
            if ((uint)offset >= (uint)source.Length)
            {
                throw Truncated();
            }

            byte unit = source[offset++];
            if (unit < 0xC0)
            {
                row[filled++] = unit;
                continue;
            }

            if ((uint)offset >= (uint)source.Length)
            {
                throw Truncated();
            }

            int count = unit & 0x3F;
            byte value = source[offset++];
            if (count > row.Length - filled)
            {
                throw new ImageFormatException(ImageFormat.Pcx, "A PCX run extends past the scanline.");
            }

            row.Slice(filled, count).Fill(value);
            filled += count;
        }
    }

    public static int Encode(ReadOnlySpan<byte> row, Span<byte> destination)
    {
        int written = 0;
        for (int i = 0; i < row.Length;)
        {
            byte value = row[i];
            int count = 1;
            int max = Math.Min(PcxConstants.MaxRun, row.Length - i);
            while (count < max && row[i + count] == value)
            {
                count++;
            }

            if (count > 1 || value >= 0xC0)
            {
                destination[written++] = (byte)(0xC0 | count);
                destination[written++] = value;
            }
            else
            {
                destination[written++] = value;
            }

            i += count;
        }

        return written;
    }

    private static ImageFormatException Truncated() => new(ImageFormat.Pcx, "The PCX image is truncated.");
}
