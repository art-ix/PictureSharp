using System.Buffers;
using System.Runtime.InteropServices;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Infrastructure.Codecs.WebP;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

/// <summary>Long version of <see cref="HuffmanCode"/> holding packed 32-bit ARGB values.</summary>
internal struct HuffmanCode32
{
    public int Bits;      // number of bits used, or an impossible value if not a literal code
    public uint Value;    // packed ARGB value if literal, or non-literal symbol otherwise
}
