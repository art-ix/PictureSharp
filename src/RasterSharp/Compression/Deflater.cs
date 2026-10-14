using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RasterSharp.Internal;

namespace RasterSharp.Compression;

/// <summary>DEFLATE (RFC 1951) / zlib (RFC 1950) compressor with zlib-like compression levels 0–9.</summary>
/// <remarks>
/// LZ77 uses hash chains with lazy matching (parameters follow zlib's configuration table). Each
/// block is emitted as stored, fixed-Huffman or dynamic-Huffman, whichever is smallest. Dynamic
/// code lengths are limited with the Moffat–Katajainen algorithm plus Kraft-sum adjustment.
/// </remarks>
internal sealed partial class Deflater : IDisposable
{
    private const int MinMatch = 3;
    private const int MaxMatch = 258;
    private const int WindowSize = 1 << 15;
    private const int WindowMask = WindowSize - 1;
    private const int MaxDistance = WindowSize - 1;
    private const int HashBits = 15;
    private const int HashSize = 1 << HashBits;
    private const int BlockSymbols = 1 << 15;
    private const int NumLitLen = 286;
    private const int NumDist = 30;
    private const int EndOfBlock = 256;

    // zlib's configuration table: good_length, max_lazy, nice_length, max_chain
    private static readonly (int Good, int Lazy, int Nice, int Chain)[] Config =
    [
        (0, 0, 0, 0),
        (4, 4, 8, 4),
        (4, 5, 16, 8),
        (4, 6, 32, 32),
        (4, 4, 16, 16),
        (8, 16, 32, 32),
        (8, 16, 128, 128),
        (8, 32, 128, 256),
        (32, 128, 258, 1024),
        (32, 258, 258, 4096),
    ];

    private static ReadOnlySpan<ushort> LengthBase =>
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static ReadOnlySpan<byte> LengthExtra =>
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static ReadOnlySpan<ushort> DistBase =>
        [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    private static ReadOnlySpan<byte> DistExtra =>
        [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    private static ReadOnlySpan<byte> CodeLengthOrder => [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly byte[] LengthToCode = BuildLengthToCode();
    private static readonly byte[] DistanceCodes = BuildDistanceCodes();

    private readonly byte[] _input;
    private readonly int _level;
    private readonly int _maxDegreeOfParallelism;
    private readonly BitWriter _out = new();

    // Current block: symbols are (length << 16 | distance) for matches or a literal byte with length 0.
    private readonly int[] _symLength = new int[BlockSymbols];
    private readonly int[] _symValue = new int[BlockSymbols];
    private readonly int[] _distCodes = new int[BlockSymbols];
    private int _symCount;
    private int _blockStart;

    private Deflater(byte[] input, int level, int maxDegreeOfParallelism)
    {
        _input = input;
        _level = level;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <summary>Compresses <paramref name="data"/> into a zlib stream.</summary>
    public static byte[] CompressZlib(ReadOnlySpan<byte> data, int level) => CompressZlib(data.ToArray(), level, 1);

    /// <summary>Compresses <paramref name="input"/> (not modified) into a zlib stream.</summary>
    /// <remarks>The output does not depend on <paramref name="maxDegreeOfParallelism"/>.</remarks>
    public static byte[] CompressZlib(byte[] input, int level, int maxDegreeOfParallelism)
    {
        using Deflater deflater = CompressZlibPooled(input, level, maxDegreeOfParallelism);
        return deflater.Output.ToArray();
    }

    /// <summary>
    /// Compresses <paramref name="input"/> into a zlib stream kept in a pooled buffer: read <see cref="Output"/>,
    /// then dispose the returned instance.
    /// </summary>
    public static Deflater CompressZlibPooled(byte[] input, int level, int maxDegreeOfParallelism)
    {
        level = Math.Clamp(level, 0, 9);
        var deflater = new Deflater(input, level, maxDegreeOfParallelism);

        // zlib header: CM=8, CINFO=7 (32K window), FLEVEL from the level, FCHECK so header % 31 == 0.
        int flevel = level < 2 ? 0 : level < 6 ? 1 : level == 6 ? 2 : 3;
        int cmf = 0x78;
        int flg = flevel << 6;
        flg |= 31 - (((cmf << 8) | flg) % 31);
        deflater._out.WriteByte((byte)cmf);
        deflater._out.WriteByte((byte)flg);

        deflater.Compress();

        uint adler = Adler32.Compute(input);
        deflater._out.WriteByte((byte)(adler >> 24));
        deflater._out.WriteByte((byte)(adler >> 16));
        deflater._out.WriteByte((byte)(adler >> 8));
        deflater._out.WriteByte((byte)adler);
        return deflater;
    }

    /// <summary>The compressed bytes of <see cref="CompressZlibPooled"/>.</summary>
    public ReadOnlySpan<byte> Output => _out.Written;

    public void Dispose() => _out.Dispose();

    /// <summary>Compresses <paramref name="data"/> into a raw deflate stream.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, int level, int maxDegreeOfParallelism = 1)
    {
        using var deflater = new Deflater(data.ToArray(), Math.Clamp(level, 0, 9), maxDegreeOfParallelism);
        deflater.Compress();
        return deflater._out.ToArray();
    }

    private void Compress()
    {
        int n = _input.Length;
        if (_level == 0 || n == 0)
        {
            WriteStoredBlocks(0, n, final: true);
            _out.Flush();
            return;
        }

        if (Parallelism.IsWorthwhile(_maxDegreeOfParallelism, n, ParallelMinimumInput))
        {
            CompressSegmented();
        }
        else
        {
            var finder = new MatchFinder(_input, _level);
            var sink = new BlockSink(this);
            finder.Parse(0, n, ref sink);
            finder.Release();
        }

        FlushBlock(n, final: true);
        _out.Flush();
    }

    private void EmitLiteral(byte value, int end)
    {
        _symLength[_symCount] = 0;
        _symValue[_symCount] = value;
        if (++_symCount == BlockSymbols)
        {
            FlushBlock(end, final: false);
        }
    }

    private void EmitMatch(int length, int distance, int end)
    {
        _symLength[_symCount] = length;
        _symValue[_symCount] = distance;
        if (++_symCount == BlockSymbols)
        {
            FlushBlock(end, final: false);
        }
    }

    /// <summary>Feeds parsed symbols straight into the current block.</summary>
    private readonly struct BlockSink(Deflater deflater) : ISymbolSink
    {
        public void Fresh(int pos)
        {
        }

        public void Literal(int pos, byte value) => deflater.EmitLiteral(value, pos + 1);

        public void Match(int pos, int length, int distance) => deflater.EmitMatch(length, distance, pos + length);
    }

    // ------------------------------------------------------------------ block output

    private static byte[] BuildLengthToCode()
    {
        var table = new byte[MaxMatch + 1];
        for (int code = 0; code < 29; code++)
        {
            int start = LengthBase[code];
            int end = code == 28 ? 258 : LengthBase[code] + (1 << LengthExtra[code]) - 1;
            for (int len = start; len <= end && len <= MaxMatch; len++)
            {
                table[len] = (byte)code;
            }
        }

        table[258] = 28;
        return table;
    }

    // zlib's _dist_code layout: distances up to 256 directly, larger ones in steps of 128.
    private static byte[] BuildDistanceCodes()
    {
        var table = new byte[512];
        for (int code = 0; code < NumDist; code++)
        {
            int start = DistBase[code];
            int end = start + (1 << DistExtra[code]) - 1;
            for (int distance = start; distance <= end; distance++)
            {
                int index = distance <= 256 ? distance - 1 : 256 + ((distance - 1) >> 7);
                table[index] = (byte)code;
            }
        }

        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DistanceToCode(int distance) =>
        DistanceCodes[distance <= 256 ? distance - 1 : 256 + ((distance - 1) >> 7)];

    private void FlushBlock(int end, bool final)
    {
        int count = _symCount;
        Span<int> litFreq = stackalloc int[NumLitLen];
        Span<int> distFreq = stackalloc int[NumDist];
        litFreq.Clear();
        distFreq.Clear();
        int[] distCodes = _distCodes;
        for (int i = 0; i < count; i++)
        {
            int len = _symLength[i];
            if (len == 0)
            {
                litFreq[_symValue[i]]++;
            }
            else
            {
                litFreq[257 + LengthToCode[len]]++;
                int dc = DistanceToCode(_symValue[i]);
                distCodes[i] = dc;
                distFreq[dc]++;
            }
        }

        litFreq[EndOfBlock]++;

        // Dynamic codes.
        Span<byte> litLengths = stackalloc byte[NumLitLen];
        Span<byte> distLengths = stackalloc byte[NumDist];
        HuffmanBuilder.BuildLengths(litFreq, 15, litLengths);
        HuffmanBuilder.BuildLengths(distFreq, 15, distLengths);
        // A distance code needs at least one entry (RFC 1951: one distance code of zero bits is allowed,
        // but some decoders reject a fully empty set); give symbol 0 a length if nothing is used.
        if (!distLengths.ContainsAnyExcept((byte)0))
        {
            distLengths[0] = 1;
        }

        int hlit = NumLitLen;
        while (hlit > 257 && litLengths[hlit - 1] == 0)
        {
            hlit--;
        }

        int hdist = NumDist;
        while (hdist > 1 && distLengths[hdist - 1] == 0)
        {
            hdist--;
        }

        // Run-length encode the code lengths.
        Span<byte> allLengths = stackalloc byte[NumLitLen + NumDist];
        litLengths[..hlit].CopyTo(allLengths);
        distLengths[..hdist].CopyTo(allLengths[hlit..]);
        allLengths = allLengths[..(hlit + hdist)];
        var rle = new List<(int Symbol, int Extra)>(allLengths.Length);
        RunLengthEncode(allLengths, rle);
        Span<int> clFreq = stackalloc int[19];
        clFreq.Clear();
        foreach ((int sym, _) in rle)
        {
            clFreq[sym]++;
        }

        Span<byte> clLengths = stackalloc byte[19];
        HuffmanBuilder.BuildLengths(clFreq, 7, clLengths);
        int hclen = 19;
        while (hclen > 4 && clLengths[CodeLengthOrder[hclen - 1]] == 0)
        {
            hclen--;
        }

        // Sizes in bits.
        long dataBitsDyn = 0;
        long dataBitsFixed = 0;
        for (int s = 0; s < NumLitLen; s++)
        {
            if (litFreq[s] == 0)
            {
                continue;
            }

            int extra = s > 256 ? LengthExtra[s - 257] : 0;
            dataBitsDyn += (long)litFreq[s] * (litLengths[s] + extra);
            dataBitsFixed += (long)litFreq[s] * (FixedLitLength(s) + extra);
        }

        for (int d = 0; d < NumDist; d++)
        {
            dataBitsDyn += (long)distFreq[d] * (distLengths[d] + DistExtra[d]);
            dataBitsFixed += (long)distFreq[d] * (5 + DistExtra[d]);
        }

        long headerBits = 5 + 5 + 4 + (3L * hclen);
        foreach ((int sym, _) in rle)
        {
            headerBits += clLengths[sym] + (sym == 16 ? 2 : sym == 17 ? 3 : sym == 18 ? 7 : 0);
        }

        long dynBits = 3 + headerBits + dataBitsDyn;
        long fixedBits = 3 + dataBitsFixed;
        int rawLength = end - _blockStart;
        long storedBits = (((rawLength + 65534) / 65535) * 5 * 8L) + (rawLength * 8L) + 7;

        if (storedBits <= Math.Min(dynBits, fixedBits))
        {
            WriteStoredBlocks(_blockStart, rawLength, final);
        }
        else if (fixedBits <= dynBits)
        {
            _out.WriteBits(final ? 1u : 0u, 1);
            _out.WriteBits(1, 2);
            WriteSymbols(count, distCodes, FixedLitLengths, FixedDistLengths);
        }
        else
        {
            _out.WriteBits(final ? 1u : 0u, 1);
            _out.WriteBits(2, 2);
            _out.WriteBits((uint)(hlit - 257), 5);
            _out.WriteBits((uint)(hdist - 1), 5);
            _out.WriteBits((uint)(hclen - 4), 4);
            for (int i = 0; i < hclen; i++)
            {
                _out.WriteBits(clLengths[CodeLengthOrder[i]], 3);
            }

            Span<ushort> clCodes = stackalloc ushort[19];
            HuffmanBuilder.BuildCodes(clLengths, clCodes);
            foreach ((int sym, int extra) in rle)
            {
                _out.WriteBits(clCodes[sym], clLengths[sym]);
                if (sym == 16)
                {
                    _out.WriteBits((uint)extra, 2);
                }
                else if (sym == 17)
                {
                    _out.WriteBits((uint)extra, 3);
                }
                else if (sym == 18)
                {
                    _out.WriteBits((uint)extra, 7);
                }
            }

            WriteSymbols(count, distCodes, litLengths, distLengths);
        }

        _symCount = 0;
        _blockStart = end;
    }

    private static int FixedLitLength(int s) => s < 144 ? 8 : s < 256 ? 9 : s < 280 ? 7 : 8;

    private static ReadOnlySpan<byte> FixedLitLengths => FixedLitLengthTable;

    private static ReadOnlySpan<byte> FixedDistLengths =>
        [5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5];

    // All 288 fixed codes must take part in the canonical code assignment (RFC 1951 3.2.6).
    private static readonly byte[] FixedLitLengthTable = Enumerable.Range(0, 288).Select(s => (byte)FixedLitLength(s)).ToArray();

    private void WriteSymbols(int count, int[] distCodes, ReadOnlySpan<byte> litLengths, ReadOnlySpan<byte> distLengths)
    {
        Span<ushort> litCodes = stackalloc ushort[litLengths.Length];
        Span<ushort> distCodesTable = stackalloc ushort[NumDist];
        HuffmanBuilder.BuildCodes(litLengths, litCodes);
        HuffmanBuilder.BuildCodes(distLengths, distCodesTable);

        // A symbol takes at most 15 + 5 + 15 + 13 bits.
        BitWriter w = _out;
        w.Reserve((count * 6) + 8);
        Span<byte> buffer = w.Buffer;
        int length = w.Length;
        ulong bits = w.Bits;
        int bitCount = w.BitCount;
        ReadOnlySpan<int> symLength = _symLength.AsSpan(0, count);
        ReadOnlySpan<int> symValue = _symValue.AsSpan(0, count);
        for (int i = 0; i < symLength.Length; i++)
        {
            int len = symLength[i];
            if (len == 0)
            {
                int lit = symValue[i];
                bits |= (ulong)litCodes[lit] << bitCount;
                bitCount += litLengths[lit];
            }
            else
            {
                int lc = LengthToCode[len];
                int codeLength = litLengths[257 + lc];
                bits |= ((ulong)litCodes[257 + lc] | ((ulong)(len - LengthBase[lc]) << codeLength)) << bitCount;
                bitCount += codeLength + LengthExtra[lc];
                BitWriter.Spill(buffer, ref length, ref bits, ref bitCount);
                int dc = distCodes[i];
                codeLength = distLengths[dc];
                bits |= ((ulong)distCodesTable[dc] | ((ulong)(symValue[i] - DistBase[dc]) << codeLength)) << bitCount;
                bitCount += codeLength + DistExtra[dc];
            }

            BitWriter.Spill(buffer, ref length, ref bits, ref bitCount);
        }

        w.Length = length;
        w.Bits = bits;
        w.BitCount = bitCount;
        w.WriteBits(litCodes[EndOfBlock], litLengths[EndOfBlock]);
    }

    private void WriteStoredBlocks(int start, int length, bool final)
    {
        do
        {
            int chunk = Math.Min(length, 65535);
            length -= chunk;
            bool last = final && length == 0;
            _out.WriteBits(last ? 1u : 0u, 1);
            _out.WriteBits(0, 2);
            _out.AlignToByte();
            _out.WriteByte((byte)chunk);
            _out.WriteByte((byte)(chunk >> 8));
            _out.WriteByte((byte)~chunk);
            _out.WriteByte((byte)(~chunk >> 8));
            _out.WriteBytes(_input.AsSpan(start, chunk));
            start += chunk;
        }
        while (length > 0);
    }

    // Encodes code lengths with the 16 (repeat previous), 17 and 18 (repeat zero) codes.
    private static void RunLengthEncode(ReadOnlySpan<byte> lengths, List<(int Symbol, int Extra)> output)
    {
        int i = 0;
        while (i < lengths.Length)
        {
            int value = lengths[i];
            int run = 1;
            while (i + run < lengths.Length && lengths[i + run] == value)
            {
                run++;
            }

            i += run;
            if (value == 0)
            {
                while (run >= 11)
                {
                    int r = Math.Min(run, 138);
                    output.Add((18, r - 11));
                    run -= r;
                }

                if (run >= 3)
                {
                    output.Add((17, run - 3));
                    run = 0;
                }
            }
            else
            {
                output.Add((value, 0));
                run--;
                while (run >= 3)
                {
                    int r = Math.Min(run, 6);
                    output.Add((16, r - 3));
                    run -= r;
                }
            }

            while (run-- > 0)
            {
                output.Add((value, 0));
            }
        }
    }

    /// <summary>LSB-first bit writer into a growable pooled buffer.</summary>
    private sealed class BitWriter : IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(1 << 16);

        public int Length;
        public ulong Bits;
        public int BitCount;  // < 32 between calls

        public Span<byte> Buffer => _buffer;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBits(uint value, int count)
        {
            Bits |= (ulong)value << BitCount;
            BitCount += count;
            if (BitCount >= 32)
            {
                Reserve(4);
                Spill(_buffer, ref Length, ref Bits, ref BitCount);
            }
        }

        /// <summary>Stores 32 bits once at least that many are pending; the caller has reserved the space.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Spill(Span<byte> buffer, ref int length, ref ulong bits, ref int bitCount)
        {
            if (bitCount >= 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer[length..], (uint)bits);
                length += 4;
                bits >>= 32;
                bitCount -= 32;
            }
        }

        public void AlignToByte()
        {
            while (BitCount > 0)
            {
                Reserve(1);
                _buffer[Length++] = (byte)Bits;
                Bits >>= 8;
                BitCount = Math.Max(0, BitCount - 8);
            }

            Bits = 0;
        }

        public void WriteByte(byte value)
        {
            AlignToByte();
            Reserve(1);
            _buffer[Length++] = value;
        }

        public void WriteBytes(ReadOnlySpan<byte> data)
        {
            AlignToByte();
            Reserve(data.Length);
            data.CopyTo(_buffer.AsSpan(Length));
            Length += data.Length;
        }

        public void Flush() => AlignToByte();

        public byte[] ToArray() => Written.ToArray();

        public ReadOnlySpan<byte> Written
        {
            get
            {
                AlignToByte();
                return _buffer.AsSpan(0, Length);
            }
        }

        /// <summary>Ensures room for <paramref name="extra"/> more bytes.</summary>
        public void Reserve(int extra)
        {
            if (Length + extra > _buffer.Length)
            {
                byte[] next = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, Length + extra));
                _buffer.AsSpan(0, Length).CopyTo(next);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = next;
            }
        }

        public void Dispose()
        {
            if (_buffer.Length == 0) return;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }
}
