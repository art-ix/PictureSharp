namespace RasterSharp.Formats.Jpeg;

/// <summary>A Huffman decoding table derived from a DHT segment.</summary>
internal sealed class JpegHuffmanTable
{
    /// <summary>Codes up to this length are resolved by a single table lookup.</summary>
    public const int LookupBits = 9;

    /// <summary>(length &lt;&lt; 8) | symbol for every <see cref="LookupBits"/>-bit prefix of a short code, 0 otherwise.</summary>
    public readonly ushort[] Lookup = new ushort[1 << LookupBits];

    /// <summary>Largest code of each length, -1 when there is none.</summary>
    public readonly int[] MaxCode = new int[17];

    /// <summary>Index of the first symbol of each length in <see cref="Values"/>, minus the first code of that length.</summary>
    public readonly int[] ValOffset = new int[17];

    public readonly byte[] Values = new byte[256];

    /// <summary>Largest symbol value; DC tables may not exceed 15 (and 16-bit values cannot be read).</summary>
    public int MaxSymbol { get; private set; }

    // port: libjpeg-turbo jdhuff.c jpeg_make_d_derived_tbl
    public static JpegHuffmanTable Build(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
    {
        var table = new JpegHuffmanTable();
        values.CopyTo(table.Values);
        foreach (byte v in values)
        {
            table.MaxSymbol = Math.Max(table.MaxSymbol, v);
        }

        Span<int> sizes = stackalloc int[257];
        Span<int> codes = stackalloc int[257];
        int p = 0;
        for (int l = 1; l <= 16; l++)
        {
            int n = counts[l - 1];
            if (p + n > 256)
            {
                throw new ImageFormatException(ImageFormat.Jpeg, "Bogus Huffman table definition.");
            }

            for (int i = 0; i < n; i++)
            {
                sizes[p++] = l;
            }
        }

        sizes[p] = 0;
        int code = 0;
        int si = sizes[0];
        p = 0;
        while (sizes[p] != 0)
        {
            while (sizes[p] == si)
            {
                codes[p++] = code++;
            }

            // No code may consist of all ones.
            if (code >= 1 << si)
            {
                throw new ImageFormatException(ImageFormat.Jpeg, "Bogus Huffman table definition.");
            }

            code <<= 1;
            si++;
        }

        p = 0;
        for (int l = 1; l <= 16; l++)
        {
            int n = counts[l - 1];
            if (n != 0)
            {
                table.ValOffset[l] = p - codes[p];
                p += n;
                table.MaxCode[l] = codes[p - 1];
            }
            else
            {
                table.MaxCode[l] = -1;
            }
        }

        p = 0;
        for (int l = 1; l <= LookupBits; l++)
        {
            for (int i = 0; i < counts[l - 1]; i++, p++)
            {
                int start = codes[p] << (LookupBits - l);
                table.Lookup.AsSpan(start, 1 << (LookupBits - l)).Fill((ushort)((l << 8) | values[p]));
            }
        }

        return table;
    }
}
