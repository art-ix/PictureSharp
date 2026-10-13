using System.Buffers;
using System.Runtime.InteropServices;

namespace RasterSharp.Formats.WebP.Lossless;

/// <summary>Huffman lookup table entry.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct HuffmanCode
{
    public byte Bits;     // number of bits used for this symbol
    public ushort Value;  // symbol value or table offset

    public HuffmanCode(int bits, int value)
    {
        Bits = (byte)bits;
        Value = (ushort)value;
    }
}

/// <summary>Long version of <see cref="HuffmanCode"/> holding packed 32-bit ARGB values.</summary>
internal struct HuffmanCode32
{
    public int Bits;      // number of bits used, or an impossible value if not a literal code
    public uint Value;    // packed ARGB value if literal, or non-literal symbol otherwise
}

/// <summary>The five Huffman tables used at one meta code, plus fast-path data.</summary>
// port: src/utils/huffman_utils.h (HTreeGroup)
internal sealed class HTreeGroup
{
    public const int PackedBits = 6;
    public const int PackedTableSize = 1 << PackedBits;

    public readonly HuffmanCode[][] HTrees = new HuffmanCode[WebPConstants.HuffmanCodesPerMetaCode][];
    public bool IsTrivialLiteral;   // red, blue and alpha trees have a single code
    public uint LiteralArb;         // ARGB value (green = 0) when IsTrivialLiteral
    public bool IsTrivialCode;      // IsTrivialLiteral with only one green code
    public bool UsePackedTable;
    public HuffmanCode32[]? PackedTable;
}

// port: src/utils/huffman_utils.c
internal static class HuffmanTableBuilder
{
    public const int TableBits = 8;
    public const int TableMask = (1 << TableBits) - 1;
    public const int LengthsTableBits = 7;
    public const int LengthsTableMask = (1 << LengthsTableBits) - 1;

    private const int MaxLength = WebPConstants.MaxAllowedCodeLength;

    /// <summary>
    /// Builds the lookup table for <paramref name="codeLengths"/>. Returns null for an invalid
    /// (incomplete or over-subscribed) code. When <paramref name="store"/> is false only validates.
    /// </summary>
    // port: VP8LBuildHuffmanTable
    public static HuffmanCode[]? Build(int rootBits, ReadOnlySpan<int> codeLengths, bool store = true)
    {
        int totalSize = BuildTable(null, rootBits, codeLengths, default);
        if (totalSize == 0)
        {
            return null;
        }

        if (!store)
        {
            return [];
        }

        var table = new HuffmanCode[totalSize];
        ushort[] sorted = ArrayPool<ushort>.Shared.Rent(codeLengths.Length);
        try
        {
            BuildTable(table, rootBits, codeLengths, sorted);
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(sorted);
        }

        return table;
    }

    // Returns reverse(reverse(key, len) + 1, len), where reverse(key, len) is the
    // bit-wise reversal of the len least significant bits of key.
    private static uint GetNextKey(uint key, int len)
    {
        uint step = 1u << (len - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return step != 0 ? (key & (step - 1)) + step : key;
    }

    // Stores code in table[0], table[step], table[2*step], ..., table[end - step].
    private static void ReplicateValue(Span<HuffmanCode> table, int step, int end, HuffmanCode code)
    {
        do
        {
            end -= step;
            table[end] = code;
        }
        while (end > 0);
    }

    // Returns the table width of the next 2nd level table.
    private static int NextTableBitSize(ReadOnlySpan<int> count, int len, int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < MaxLength)
        {
            left -= count[len];
            if (left <= 0)
            {
                break;
            }

            ++len;
            left <<= 1;
        }

        return len - rootBits;
    }

    // port: BuildHuffmanTable. With rootTable == null, only computes the size.
    private static int BuildTable(HuffmanCode[]? rootTable, int rootBits, ReadOnlySpan<int> codeLengths, Span<ushort> sorted)
    {
        int tableOffset = 0;          // next available space in table
        int totalSize = 1 << rootBits;
        Span<int> count = stackalloc int[MaxLength + 1];
        Span<int> offset = stackalloc int[MaxLength + 1];
        count.Clear();

        for (int symbol = 0; symbol < codeLengths.Length; ++symbol)
        {
            int len = codeLengths[symbol];
            if ((uint)len > MaxLength)
            {
                return 0;
            }

            ++count[len];
        }

        if (count[0] == codeLengths.Length)
        {
            return 0;
        }

        offset[1] = 0;
        for (int len = 1; len < MaxLength; ++len)
        {
            if (count[len] > (1 << len))
            {
                return 0;
            }

            offset[len + 1] = offset[len] + count[len];
        }

        for (int symbol = 0; symbol < codeLengths.Length; ++symbol)
        {
            int symbolCodeLength = codeLengths[symbol];
            if (symbolCodeLength > 0)
            {
                if (rootTable is not null)
                {
                    sorted[offset[symbolCodeLength]++] = (ushort)symbol;
                }
                else
                {
                    offset[symbolCodeLength]++;
                }
            }
        }

        // Special case code with only one value.
        if (offset[MaxLength] == 1)
        {
            if (rootTable is not null)
            {
                ReplicateValue(rootTable, 1, totalSize, new HuffmanCode(0, sorted[0]));
            }

            return totalSize;
        }

        {
            int sym = 0;
            uint low = 0xffffffffu;           // low bits for current root entry
            uint mask = (uint)totalSize - 1;  // mask for low bits
            uint key = 0;                     // reversed prefix code
            int numNodes = 1;                 // number of Huffman tree nodes
            int numOpen = 1;                  // number of open branches in current tree level
            int tableBits = rootBits;
            int tableSize = 1 << tableBits;
            int len;
            int step;

            // Fill in root table.
            for (len = 1, step = 2; len <= rootBits; ++len, step <<= 1)
            {
                numOpen <<= 1;
                numNodes += numOpen;
                numOpen -= count[len];
                if (numOpen < 0)
                {
                    return 0;
                }

                if (rootTable is null)
                {
                    continue;
                }

                for (; count[len] > 0; --count[len])
                {
                    ReplicateValue(rootTable.AsSpan((int)key), step, tableSize, new HuffmanCode(len, sorted[sym++]));
                    key = GetNextKey(key, len);
                }
            }

            // Fill in 2nd level tables and add pointers to root table.
            for (len = rootBits + 1, step = 2; len <= MaxLength; ++len, step <<= 1)
            {
                numOpen <<= 1;
                numNodes += numOpen;
                numOpen -= count[len];
                if (numOpen < 0)
                {
                    return 0;
                }

                for (; count[len] > 0; --count[len])
                {
                    if ((key & mask) != low)
                    {
                        if (rootTable is not null)
                        {
                            tableOffset += tableSize;
                        }

                        tableBits = NextTableBitSize(count, len, rootBits);
                        tableSize = 1 << tableBits;
                        totalSize += tableSize;
                        low = key & mask;
                        if (rootTable is not null)
                        {
                            rootTable[low] = new HuffmanCode(tableBits + rootBits, tableOffset - (int)low);
                        }
                    }

                    if (rootTable is not null)
                    {
                        ReplicateValue(rootTable.AsSpan(tableOffset + (int)(key >> rootBits)), step, tableSize,
                            new HuffmanCode(len - rootBits, sorted[sym++]));
                    }

                    key = GetNextKey(key, len);
                }
            }

            // Check if tree is full.
            if (numNodes != (2 * offset[MaxLength]) - 1)
            {
                return 0;
            }
        }

        return totalSize;
    }
}
