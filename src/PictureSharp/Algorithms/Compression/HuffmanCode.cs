using System.Runtime.InteropServices;

namespace PictureSharp.Algorithms.Compression;

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
