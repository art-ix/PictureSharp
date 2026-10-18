using System.Buffers;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

/// <summary>A Huffman decoding table derived from a DHT segment.</summary>
internal sealed class JpegHuffmanTable
{
    /// <summary>Codes up to this length are resolved by a single table lookup.</summary>
    public const int LookupBits = 11;

    /// <summary>(length &lt;&lt; 8) | symbol for every <see cref="LookupBits"/>-bit prefix of a short code, 0 otherwise.</summary>
    public ushort[] Lookup { get; private set; } = Rent<ushort>();

    /// <summary>Largest code of each length, -1 when there is none.</summary>
    public readonly int[] MaxCode = new int[17];

    /// <summary>Index of the first symbol of each length in <see cref="Values"/>, minus the first code of that length.</summary>
    public readonly int[] ValOffset = new int[17];

    public readonly byte[] Values = new byte[256];

    /// <summary>
    /// For AC tables: (value &lt;&lt; 16) | (run &lt;&lt; 8) | bits for every <see cref="LookupBits"/>-bit prefix that
    /// holds a whole code with a non-zero size and its extra bits; 0 otherwise.
    /// </summary>
    public int[] FastAc { get; private set; } = Rent<int>();

    /// <summary>Largest symbol value; DC tables may not exceed 15 (and 16-bit values cannot be read).</summary>
    public int MaxSymbol { get; private set; }

    /// <summary>Gives the pooled lookup tables back once the file is decoded; the table must not be used afterwards.</summary>
    public void Return()
    {
        ArrayPool<ushort>.Shared.Return(Lookup);
        ArrayPool<int>.Shared.Return(FastAc);
        Lookup = [];
        FastAc = [];
    }

    private static T[] Rent<T>()
    {
        T[] array = ArrayPool<T>.Shared.Rent(1 << LookupBits);
        array.AsSpan(0, 1 << LookupBits).Clear();
        return array;
    }

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

                // A short code followed by its extra bits resolves the coefficient value in the same lookup.
                int run = values[p] >> 4, size = values[p] & 15;
                if (size != 0 && l + size <= LookupBits)
                {
                    for (int v = 0; v < 1 << size; v++)
                    {
                        // port: libjpeg-turbo jdhuff.h HUFF_EXTEND
                        int value = v < 1 << (size - 1) ? v + (-1 << size) + 1 : v;
                        int length = l + size;
                        table.FastAc.AsSpan(start | (v << (LookupBits - length)), 1 << (LookupBits - length))
                            .Fill((value << 16) | (run << 8) | length);
                    }
                }
            }
        }

        return table;
    }
}
