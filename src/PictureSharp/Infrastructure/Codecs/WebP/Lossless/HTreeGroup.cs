using System.Buffers;
using System.Runtime.InteropServices;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Infrastructure.Codecs.WebP;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

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
