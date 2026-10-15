using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static PictureSharp.Formats.WebP.WebPConstants;

namespace PictureSharp.Formats.WebP.Lossless;

/// <summary>Decoder for the VP8L (WebP lossless) bitstream.</summary>
/// <remarks>
/// Unlike libwebp, which processes rows incrementally through a small cache, this port decodes the
/// whole entropy-coded image first and then applies the inverse transforms to the full buffer.
/// The arithmetic is identical, so the output is bit-exact.
/// </remarks>
// port: src/dec/vp8l_dec.c
internal sealed class Vp8LDecoder
{
    private const int CodeLengthLiterals = 16;
    private const int CodeLengthRepeatCode = 16;
    private const int NumCodeLengthCodes = 19;
    private const int CodeToPlaneCodes = 120;
    private const int BitsSpecialMarker = 0x100;   // something large enough (and a bit-mask)
    private const int PackedNonLiteralCode = 0;    // must be < NumLiteralCodes

    // Huffman tree indices.
    private const int Green = 0;
    private const int Red = 1;
    private const int Blue = 2;
    private const int Alpha = 3;
    private const int Dist = 4;

    private static ReadOnlySpan<byte> CodeLengthExtraBits => [2, 3, 7];
    private static ReadOnlySpan<byte> CodeLengthRepeatOffsets => [3, 3, 11];
    private static ReadOnlySpan<ushort> AlphabetSize =>
        [NumLiteralCodes + NumLengthCodes, NumLiteralCodes, NumLiteralCodes, NumLiteralCodes, NumDistanceCodes];
    private static ReadOnlySpan<byte> LiteralMap => [0, 1, 1, 1, 0];
    private static ReadOnlySpan<byte> CodeLengthCodeOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private static ReadOnlySpan<byte> CodeToPlane =>
    [
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a,
        0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
        0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b,
        0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
        0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c,
        0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b,
        0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
        0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b,
        0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
        0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f,
        0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    ];

    private readonly Vp8LBitReader _br;
    private readonly Vp8LTransform[] _transforms = new Vp8LTransform[NumTransforms];
    private int _nextTransform;
    private uint _transformsSeen;

    private Vp8LDecoder(byte[] data, int offset, int length)
    {
        _br = new Vp8LBitReader(data, offset, length);
    }

    /// <summary>Header information of a VP8L bitstream.</summary>
    public readonly record struct Info(int Width, int Height, bool HasAlpha);

    // port: VP8LCheckSignature
    public static bool CheckSignature(ReadOnlySpan<byte> data) =>
        data.Length >= Vp8LFrameHeaderSize && data[0] == Vp8LMagicByte && (data[4] >> 5) == 0;

    // port: VP8LGetInfo
    public static bool TryGetInfo(ReadOnlySpan<byte> data, out Info info)
    {
        info = default;
        if (!CheckSignature(data))
        {
            return false;
        }

        var br = new Vp8LBitReader(data[..Vp8LFrameHeaderSize].ToArray(), 0, Vp8LFrameHeaderSize);
        if (!ReadImageInfo(br, out int w, out int h, out bool a))
        {
            return false;
        }

        info = new Info(w, h, a);
        return true;
    }

    /// <summary>
    /// Decodes a complete VP8L bitstream (with its 5-byte header) to ARGB pixels in a buffer rented from
    /// <see cref="ArrayPool{T}.Shared"/>: the first width × height entries are the image; return it after use.
    /// </summary>
    public static uint[] Decode(byte[] data, int offset, int length, out int width, out int height)
    {
        var dec = new Vp8LDecoder(data, offset, length);
        if (!ReadImageInfo(dec._br, out width, out height, out _))
        {
            throw Error("Invalid VP8L header.");
        }

        return dec.DecodeImageStream(width, height, isLevel0: true);
    }

    /// <summary>
    /// Decodes a headerless VP8L image stream of known size, as used by the ALPH chunk.
    /// Returns ARGB pixels in a rented buffer like <see cref="Decode"/>; the alpha values are in the green channel.
    /// </summary>
    public static uint[] DecodeHeaderless(byte[] data, int offset, int length, int width, int height)
    {
        var dec = new Vp8LDecoder(data, offset, length);
        return dec.DecodeImageStream(width, height, isLevel0: true);
    }

    private static ImageFormatException Error(string message) => new(ImageFormat.WebP, message);

    // port: ReadImageInfo
    private static bool ReadImageInfo(Vp8LBitReader br, out int width, out int height, out bool hasAlpha)
    {
        width = height = 0;
        hasAlpha = false;
        if (br.ReadBits(8) != Vp8LMagicByte)
        {
            return false;
        }

        width = (int)br.ReadBits(Vp8LImageSizeBits) + 1;
        height = (int)br.ReadBits(Vp8LImageSizeBits) + 1;
        hasAlpha = br.ReadBits(1) != 0;
        if (br.ReadBits(Vp8LVersionBits) != 0)
        {
            return false;
        }

        return !br.Eos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SubSampleSize(int size, int samplingBits) => (size + (1 << samplingBits) - 1) >> samplingBits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetCopyDistance(int distanceSymbol, Vp8LBitReader br)
    {
        if (distanceSymbol < 4)
        {
            return distanceSymbol + 1;
        }

        int extraBits = (distanceSymbol - 2) >> 1;
        int offset = (2 + (distanceSymbol & 1)) << extraBits;
        return offset + (int)br.ReadBits(extraBits) + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PlaneCodeToDistance(int xsize, int planeCode)
    {
        if (planeCode > CodeToPlaneCodes)
        {
            return planeCode - CodeToPlaneCodes;
        }

        int distCode = CodeToPlane[planeCode - 1];
        int yoffset = distCode >> 4;
        int xoffset = 8 - (distCode & 0xf);
        int dist = (yoffset * xsize) + xoffset;
        return dist >= 1 ? dist : 1;  // dist < 1 can happen if xsize is very small
    }

    // port: ReadSymbol. FillBitWindow must be called at least every second call.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadSymbol(HuffmanCode[] table, Vp8LBitReader br)
    {
        uint val = br.PrefetchBits();
        int index = (int)(val & HuffmanTableBuilder.TableMask);
        ref HuffmanCode code = ref table[index];
        int nbits = code.Bits - HuffmanTableBuilder.TableBits;
        if (nbits > 0)
        {
            br.BitPos += HuffmanTableBuilder.TableBits;
            val >>= HuffmanTableBuilder.TableBits;
            index += code.Value + (int)(val & ((1u << nbits) - 1));
            code = ref table[index];
        }

        br.BitPos += code.Bits;
        return code.Value;
    }

    // port: ReadPackedSymbols
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadPackedSymbols(HTreeGroup group, Vp8LBitReader br, out uint dst)
    {
        uint val = br.PrefetchBits() & (HTreeGroup.PackedTableSize - 1);
        HuffmanCode32 code = group.PackedTable![val];
        if (code.Bits < BitsSpecialMarker)
        {
            br.BitPos += code.Bits;
            dst = code.Value;
            return PackedNonLiteralCode;
        }

        br.BitPos += code.Bits - BitsSpecialMarker;
        dst = 0;
        return (int)code.Value;
    }

    // port: BuildPackedTable
    private static void BuildPackedTable(HTreeGroup group)
    {
        group.PackedTable = new HuffmanCode32[HTreeGroup.PackedTableSize];
        for (uint code = 0; code < HTreeGroup.PackedTableSize; ++code)
        {
            uint bits = code;
            ref HuffmanCode32 huff = ref group.PackedTable[bits];
            HuffmanCode hcode = group.HTrees[Green][bits];
            if (hcode.Value >= NumLiteralCodes)
            {
                huff.Bits = hcode.Bits + BitsSpecialMarker;
                huff.Value = hcode.Value;
            }
            else
            {
                huff.Bits = 0;
                huff.Value = 0;
                bits >>= AccumulateHCode(hcode, 8, ref huff);
                bits >>= AccumulateHCode(group.HTrees[Red][bits], 16, ref huff);
                bits >>= AccumulateHCode(group.HTrees[Blue][bits], 0, ref huff);
                AccumulateHCode(group.HTrees[Alpha][bits], 24, ref huff);
            }
        }

        static int AccumulateHCode(HuffmanCode hcode, int shift, ref HuffmanCode32 huff)
        {
            huff.Bits += hcode.Bits;
            huff.Value |= (uint)hcode.Value << shift;
            return hcode.Bits;
        }
    }

    // port: ReadHuffmanCodeLengths
    private void ReadHuffmanCodeLengths(ReadOnlySpan<int> codeLengthCodeLengths, int numSymbols, Span<int> codeLengths)
    {
        HuffmanCode[] table = HuffmanTableBuilder.Build(HuffmanTableBuilder.LengthsTableBits, codeLengthCodeLengths)
            ?? throw Error("Invalid code length code.");

        int maxSymbol;
        int prevCodeLen = DefaultCodeLength;
        if (_br.ReadBits(1) != 0)
        {
            int lengthNbits = 2 + (2 * (int)_br.ReadBits(3));
            maxSymbol = 2 + (int)_br.ReadBits(lengthNbits);
            if (maxSymbol > numSymbols)
            {
                throw Error("Invalid Huffman code length count.");
            }
        }
        else
        {
            maxSymbol = numSymbols;
        }

        int symbol = 0;
        while (symbol < numSymbols)
        {
            if (maxSymbol-- == 0)
            {
                break;
            }

            _br.FillBitWindow();
            HuffmanCode p = table[_br.PrefetchBits() & HuffmanTableBuilder.LengthsTableMask];
            _br.BitPos += p.Bits;
            int codeLen = p.Value;
            if (codeLen < CodeLengthLiterals)
            {
                codeLengths[symbol++] = codeLen;
                if (codeLen != 0)
                {
                    prevCodeLen = codeLen;
                }
            }
            else
            {
                bool usePrev = codeLen == CodeLengthRepeatCode;
                int slot = codeLen - CodeLengthLiterals;
                int extraBits = CodeLengthExtraBits[slot];
                int repeatOffset = CodeLengthRepeatOffsets[slot];
                int repeat = (int)_br.ReadBits(extraBits) + repeatOffset;
                if (symbol + repeat > numSymbols)
                {
                    throw Error("Invalid Huffman code length repetition.");
                }

                int length = usePrev ? prevCodeLen : 0;
                while (repeat-- > 0)
                {
                    codeLengths[symbol++] = length;
                }
            }
        }
    }

    // port: ReadHuffmanCode. Returns the table (empty when store is false).
    // 'codeLengths' must hold at least 256 entries: like libwebp, simple codes may name symbols
    // beyond the alphabet; those are ignored when the table is built.
    private HuffmanCode[] ReadHuffmanCode(int alphabetSize, Span<int> codeLengths, bool store)
    {
        codeLengths[..alphabetSize].Clear();

        bool simpleCode = _br.ReadBits(1) != 0;
        if (simpleCode)
        {
            // Read symbols, codes & code lengths directly.
            int numSymbols = (int)_br.ReadBits(1) + 1;
            int firstSymbolLenCode = (int)_br.ReadBits(1);
            // The first code is either 1 bit or 8 bit code.
            int symbol = (int)_br.ReadBits(firstSymbolLenCode == 0 ? 1 : 8);
            codeLengths[symbol] = 1;
            // The second code (if present), is always 8 bits long.
            if (numSymbols == 2)
            {
                symbol = (int)_br.ReadBits(8);
                codeLengths[symbol] = 1;
            }
        }
        else
        {
            // Decode Huffman-coded code lengths.
            Span<int> codeLengthCodeLengths = stackalloc int[NumCodeLengthCodes];
            codeLengthCodeLengths.Clear();
            int numCodes = (int)_br.ReadBits(4) + 4;
            for (int i = 0; i < numCodes; ++i)
            {
                codeLengthCodeLengths[CodeLengthCodeOrder[i]] = (int)_br.ReadBits(3);
            }

            ReadHuffmanCodeLengths(codeLengthCodeLengths, alphabetSize, codeLengths);
        }

        if (_br.Eos)
        {
            throw Error("Truncated Huffman code.");
        }

        return HuffmanTableBuilder.Build(HuffmanTableBuilder.TableBits, codeLengths[..alphabetSize], store)
            ?? throw Error("Invalid Huffman code.");
    }

    // port: ReadHuffmanCodes
    private Metadata ReadHuffmanCodes(int xsize, int ysize, int colorCacheBits, bool allowRecursion)
    {
        var hdr = new Metadata();
        int numHtreeGroups = 1;
        int numHtreeGroupsMax = 1;
        int[]? mapping = null;

        if (allowRecursion && _br.ReadBits(1) != 0)
        {
            // Use meta Huffman codes.
            int huffmanPrecision = (int)_br.ReadBits(3) + 2;
            int huffmanXSize = SubSampleSize(xsize, huffmanPrecision);
            int huffmanYSize = SubSampleSize(ysize, huffmanPrecision);
            int huffmanPixs = huffmanXSize * huffmanYSize;
            uint[] huffmanImage = DecodeImageStream(huffmanXSize, huffmanYSize, isLevel0: false);
            hdr.HuffmanSubsampleBits = huffmanPrecision;
            var image = new int[huffmanPixs];
            for (int i = 0; i < huffmanPixs; ++i)
            {
                // The Huffman data is stored in red and green bytes.
                int group = (int)((huffmanImage[i] >> 8) & 0xffff);
                image[i] = group;
                if (group >= numHtreeGroupsMax)
                {
                    numHtreeGroupsMax = group + 1;
                }
            }

            // If the number of groups seems too big, remap the used indices to [0, numHtreeGroups)
            // to prevent big allocations for a bad bitstream.
            if (numHtreeGroupsMax > 1000 || numHtreeGroupsMax > xsize * ysize)
            {
                mapping = new int[numHtreeGroupsMax];
                mapping.AsSpan().Fill(-1);
                numHtreeGroups = 0;
                for (int i = 0; i < huffmanPixs; ++i)
                {
                    ref int mappedGroup = ref mapping[image[i]];
                    if (mappedGroup == -1)
                    {
                        mappedGroup = numHtreeGroups++;
                    }

                    image[i] = mappedGroup;
                }
            }
            else
            {
                numHtreeGroups = numHtreeGroupsMax;
            }

            hdr.HuffmanImage = image;
        }

        if (_br.Eos)
        {
            throw Error("Truncated data.");
        }

        int maxAlphabetSize = 0;
        for (int j = 0; j < HuffmanCodesPerMetaCode; ++j)
        {
            int alphabetSize = AlphabetSize[j];
            if (j == 0 && colorCacheBits > 0)
            {
                alphabetSize += 1 << colorCacheBits;
            }

            maxAlphabetSize = Math.Max(maxAlphabetSize, alphabetSize);
        }

        var codeLengths = new int[Math.Max(maxAlphabetSize, NumLiteralCodes)];
        var groups = new HTreeGroup[numHtreeGroups];

        for (int i = 0; i < numHtreeGroupsMax; ++i)
        {
            // If the index "i" is unused in the Huffman image, just make sure the
            // coefficients are valid but do not store them.
            if (mapping is not null && mapping[i] == -1)
            {
                for (int j = 0; j < HuffmanCodesPerMetaCode; ++j)
                {
                    int alphabetSize = AlphabetSize[j];
                    if (j == 0 && colorCacheBits > 0)
                    {
                        alphabetSize += 1 << colorCacheBits;
                    }

                    ReadHuffmanCode(alphabetSize, codeLengths, store: false);
                }

                continue;
            }

            var group = new HTreeGroup();
            groups[mapping is null ? i : mapping[i]] = group;
            HuffmanCode[][] htrees = group.HTrees;
            int totalSize = 0;
            bool isTrivialLiteral = true;
            int maxBits = 0;
            for (int j = 0; j < HuffmanCodesPerMetaCode; ++j)
            {
                int alphabetSize = AlphabetSize[j];
                if (j == 0 && colorCacheBits > 0)
                {
                    alphabetSize += 1 << colorCacheBits;
                }

                htrees[j] = ReadHuffmanCode(alphabetSize, codeLengths, store: true);
                if (isTrivialLiteral && LiteralMap[j] == 1)
                {
                    isTrivialLiteral = htrees[j][0].Bits == 0;
                }

                totalSize += htrees[j][0].Bits;
                if (j <= Alpha)
                {
                    int localMaxBits = codeLengths[0];
                    for (int k = 1; k < alphabetSize; ++k)
                    {
                        if (codeLengths[k] > localMaxBits)
                        {
                            localMaxBits = codeLengths[k];
                        }
                    }

                    maxBits += localMaxBits;
                }
            }

            group.IsTrivialLiteral = isTrivialLiteral;
            group.IsTrivialCode = false;
            if (isTrivialLiteral)
            {
                int red = htrees[Red][0].Value;
                int blue = htrees[Blue][0].Value;
                int alpha = htrees[Alpha][0].Value;
                group.LiteralArb = ((uint)alpha << 24) | (uint)(red << 16) | (uint)blue;
                if (totalSize == 0 && htrees[Green][0].Value < NumLiteralCodes)
                {
                    group.IsTrivialCode = true;
                    group.LiteralArb |= (uint)htrees[Green][0].Value << 8;
                }
            }

            group.UsePackedTable = !group.IsTrivialCode && maxBits < HTreeGroup.PackedBits;
            if (group.UsePackedTable)
            {
                BuildPackedTable(group);
            }
        }

        hdr.HtreeGroups = groups;
        return hdr;
    }

    // port: ExpandColorMap. Remaps the color map to span all possible bundled values.
    private static uint[] ExpandColorMap(int numColors, int bits, uint[] data)
    {
        int finalNumColors = 1 << (8 >> bits);
        var newColorMap = new uint[finalNumColors];
        newColorMap[0] = data[0];
        for (int i = 1; i < numColors; ++i)
        {
            // Equivalent to AddPixels(), on a byte-basis.
            newColorMap[i] = LosslessTransforms.AddPixels(data[i], newColorMap[i - 1]);
        }

        // The tail (numColors..finalNumColors) stays black (zero).
        return newColorMap;
    }

    // port: ReadTransform
    private void ReadTransform(ref int xsize, int ysize)
    {
        var type = (Vp8LTransformType)_br.ReadBits(2);

        // Each transform type can only be present once in the stream.
        if ((_transformsSeen & (1u << (int)type)) != 0)
        {
            throw Error("Duplicate transform.");
        }

        _transformsSeen |= 1u << (int)type;
        var transform = new Vp8LTransform { Type = type, XSize = xsize, YSize = ysize };
        _transforms[_nextTransform++] = transform;

        switch (type)
        {
            case Vp8LTransformType.Predictor:
            case Vp8LTransformType.CrossColor:
                transform.Bits = (int)_br.ReadBits(3) + 2;
                transform.Data = DecodeImageStream(
                    SubSampleSize(transform.XSize, transform.Bits),
                    SubSampleSize(transform.YSize, transform.Bits),
                    isLevel0: false);
                break;
            case Vp8LTransformType.ColorIndexing:
            {
                int numColors = (int)_br.ReadBits(8) + 1;
                int bits = numColors > 16 ? 0 : numColors > 4 ? 1 : numColors > 2 ? 2 : 3;
                xsize = SubSampleSize(transform.XSize, bits);
                transform.Bits = bits;
                uint[] palette = DecodeImageStream(numColors, 1, isLevel0: false);
                transform.Data = ExpandColorMap(numColors, bits, palette);
                break;
            }

            case Vp8LTransformType.SubtractGreen:
                break;
        }
    }

    // port: DecodeImageStream. For level 0, also decodes the pixels and applies the inverse transforms.
    private uint[] DecodeImageStream(int xsize, int ysize, bool isLevel0)
    {
        int transformXSize = xsize;
        int transformYSize = ysize;
        int colorCacheBits = 0;

        // Read the transforms (may recurse).
        if (isLevel0)
        {
            while (_br.ReadBits(1) != 0)
            {
                if (_nextTransform == NumTransforms)
                {
                    throw Error("Too many transforms.");
                }

                ReadTransform(ref transformXSize, transformYSize);
            }
        }

        // Color cache
        if (_br.ReadBits(1) != 0)
        {
            colorCacheBits = (int)_br.ReadBits(4);
            if (colorCacheBits is < 1 or > MaxCacheBits)
            {
                throw Error("Invalid color cache size.");
            }
        }

        // Read the Huffman codes (may recurse).
        Metadata hdr = ReadHuffmanCodes(transformXSize, transformYSize, colorCacheBits, isLevel0);
        if (colorCacheBits > 0)
        {
            hdr.ColorCacheSize = 1 << colorCacheBits;
            hdr.ColorCache = new ColorCache(colorCacheBits);
        }

        hdr.HuffmanXSize = SubSampleSize(transformXSize, hdr.HuffmanSubsampleBits);
        hdr.HuffmanMask = hdr.HuffmanSubsampleBits == 0 ? ~0 : (1 << hdr.HuffmanSubsampleBits) - 1;

        // The level-0 image is the largest buffer; it is rented and handed to the caller.
        int size = checked(transformXSize * transformYSize);
        uint[] data = isLevel0 ? ArrayPool<uint>.Shared.Rent(size) : new uint[size];
        try
        {
            DecodeImageData(hdr, data, transformXSize, transformYSize);
            if (_br.Eos)
            {
                throw Error("Truncated data.");
            }

            return isLevel0 ? ApplyInverseTransforms(data, xsize, ysize) : data;
        }
        catch
        {
            if (isLevel0)
            {
                ArrayPool<uint>.Shared.Return(data);
            }

            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HTreeGroup GetHtreeGroupForPos(Metadata hdr, int x, int y)
    {
        int bits = hdr.HuffmanSubsampleBits;
        int metaIndex = bits == 0 ? 0 : hdr.HuffmanImage![(hdr.HuffmanXSize * (y >> bits)) + (x >> bits)];
        return hdr.HtreeGroups[metaIndex];
    }

    // port: DecodeImageData (non-incremental, no row processing)
    private void DecodeImageData(Metadata hdr, uint[] data, int width, int height)
    {
        Vp8LBitReader br = _br;
        int row = 0;
        int col = 0;
        int src = 0;
        int lastCached = 0;
        int srcEnd = width * height;
        const int lenCodeLimit = NumLiteralCodes + NumLengthCodes;
        int colorCacheLimit = lenCodeLimit + hdr.ColorCacheSize;
        ColorCache? colorCache = hdr.ColorCacheSize > 0 ? hdr.ColorCache : null;
        int mask = hdr.HuffmanMask;
        HTreeGroup htreeGroup = GetHtreeGroupForPos(hdr, col, row);

        while (src < srcEnd)
        {
            int code;
            // Only update when changing tile.
            if ((col & mask) == 0)
            {
                htreeGroup = GetHtreeGroupForPos(hdr, col, row);
            }

            if (htreeGroup.IsTrivialCode)
            {
                data[src] = htreeGroup.LiteralArb;
                goto AdvanceByOne;
            }

            br.FillBitWindow();
            if (htreeGroup.UsePackedTable)
            {
                code = ReadPackedSymbols(htreeGroup, br, out uint packed);
                if (br.IsEndOfStream)
                {
                    break;
                }

                if (code == PackedNonLiteralCode)
                {
                    data[src] = packed;
                    goto AdvanceByOne;
                }
            }
            else
            {
                code = ReadSymbol(htreeGroup.HTrees[Green], br);
            }

            if (br.IsEndOfStream)
            {
                break;
            }

            if (code < NumLiteralCodes)
            {
                // Literal
                if (htreeGroup.IsTrivialLiteral)
                {
                    data[src] = htreeGroup.LiteralArb | (uint)(code << 8);
                }
                else
                {
                    int red = ReadSymbol(htreeGroup.HTrees[Red], br);
                    br.FillBitWindow();
                    int blue = ReadSymbol(htreeGroup.HTrees[Blue], br);
                    int alpha = ReadSymbol(htreeGroup.HTrees[Alpha], br);
                    if (br.IsEndOfStream)
                    {
                        break;
                    }

                    data[src] = ((uint)alpha << 24) | (uint)(red << 16) | (uint)(code << 8) | (uint)blue;
                }

                goto AdvanceByOne;
            }

            if (code < lenCodeLimit)
            {
                // Backward reference
                int lengthSym = code - NumLiteralCodes;
                int length = GetCopyDistance(lengthSym, br);
                int distSymbol = ReadSymbol(htreeGroup.HTrees[Dist], br);
                br.FillBitWindow();
                int distCode = GetCopyDistance(distSymbol, br);
                int dist = PlaneCodeToDistance(width, distCode);

                if (br.IsEndOfStream)
                {
                    break;
                }

                if (src < dist || srcEnd - src < length)
                {
                    throw Error("Invalid backward reference.");
                }

                CopyBlock32b(data, src, dist, length);
                src += length;
                col += length;
                while (col >= width)
                {
                    col -= width;
                    ++row;
                }

                if ((col & mask) != 0)
                {
                    htreeGroup = GetHtreeGroupForPos(hdr, col, row);
                }

                if (colorCache is not null)
                {
                    while (lastCached < src)
                    {
                        colorCache.Insert(data[lastCached++]);
                    }
                }

                continue;
            }

            if (code < colorCacheLimit)
            {
                // Color cache
                int key = code - lenCodeLimit;
                while (lastCached < src)
                {
                    colorCache!.Insert(data[lastCached++]);
                }

                data[src] = colorCache!.Lookup(key);
                goto AdvanceByOne;
            }

            throw Error("Invalid symbol.");

        AdvanceByOne:
            ++src;
            ++col;
            if (col >= width)
            {
                col = 0;
                ++row;
                if (colorCache is not null)
                {
                    while (lastCached < src)
                    {
                        colorCache.Insert(data[lastCached++]);
                    }
                }
            }
        }

        br.Eos = br.IsEndOfStream;
        if (br.Eos)
        {
            throw Error("Truncated data.");
        }
    }

    // port: src/dec/vp8l_dec.c CopyBlock32b, CopySmallPattern32b
    internal static void CopyBlock32b(uint[] data, int dst, int dist, int length)
    {
        int src = dst - dist;
        if (dist == 1)
        {
            data.AsSpan(dst, length).Fill(data[src]);
        }
        else if (dist == 2 && length >= 4)
        {
            Span<uint> output = data.AsSpan(dst, length);
            ulong pattern = MemoryMarshal.Read<ulong>(MemoryMarshal.AsBytes(data.AsSpan(src, 2)));
            MemoryMarshal.Cast<uint, ulong>(output).Fill(pattern);
            if ((length & 1) != 0)
            {
                output[^1] = data[src];
            }
        }
        else if (dist >= length)
        {
            data.AsSpan(src, length).CopyTo(data.AsSpan(dst, length));
        }
        else
        {
            // Overlapping copy: the pattern repeats.
            for (int i = 0; i < length; ++i)
            {
                data[dst + i] = data[src + i];
            }
        }
    }

    // port: ApplyInverseTransforms / VP8LInverseTransform, applied to the whole image.
    // Takes ownership of the rented pixels and returns the rented result (the color index transform replaces them).
    private uint[] ApplyInverseTransforms(uint[] pixels, int width, int height)
    {
        for (int n = _nextTransform - 1; n >= 0; n--)
        {
            Vp8LTransform transform = _transforms[n];
            switch (transform.Type)
            {
                case Vp8LTransformType.SubtractGreen:
                    LosslessTransforms.AddGreenToBlueAndRed(pixels.AsSpan(0, transform.XSize * transform.YSize));
                    break;
                case Vp8LTransformType.Predictor:
                    LosslessTransforms.PredictorInverseTransform(transform, pixels);
                    break;
                case Vp8LTransformType.CrossColor:
                    LosslessTransforms.ColorSpaceInverseTransform(transform, pixels);
                    break;
                case Vp8LTransformType.ColorIndexing:
                    uint[] expanded = ArrayPool<uint>.Shared.Rent(transform.XSize * transform.YSize);
                    LosslessTransforms.ColorIndexInverseTransform(transform, pixels, expanded);
                    ArrayPool<uint>.Shared.Return(pixels);
                    pixels = expanded;
                    break;
            }
        }

        return pixels;
    }

    private sealed class Metadata
    {
        public int ColorCacheSize;
        public ColorCache? ColorCache;
        public int HuffmanMask;
        public int HuffmanSubsampleBits;
        public int HuffmanXSize;
        public int[]? HuffmanImage;
        public HTreeGroup[] HtreeGroups = [];
    }
}

internal sealed class Vp8LTransform
{
    public Vp8LTransformType Type;
    public int Bits;
    public int XSize;
    public int YSize;
    public uint[] Data = [];
}
