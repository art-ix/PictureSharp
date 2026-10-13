using System.Runtime.CompilerServices;

namespace WebpSharp.Compression;

/// <summary>DEFLATE (RFC 1951) / zlib (RFC 1950) compressor with zlib-like compression levels 0–9.</summary>
/// <remarks>
/// LZ77 uses hash chains with lazy matching (parameters follow zlib's configuration table). Each
/// block is emitted as stored, fixed-Huffman or dynamic-Huffman, whichever is smallest. Dynamic
/// code lengths are limited with the Moffat–Katajainen algorithm plus Kraft-sum adjustment.
/// </remarks>
internal sealed class Deflater
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

    private readonly byte[] _input;
    private readonly int _level;
    private readonly BitWriter _out = new();

    // LZ77 state
    private readonly int[] _head = new int[HashSize];
    private readonly int[] _prev = new int[WindowSize];
    private int _inserted;  // positions < _inserted are in the hash chains

    // Current block: symbols are (length << 16 | distance) for matches or a literal byte with length 0.
    private readonly int[] _symLength = new int[BlockSymbols];
    private readonly int[] _symValue = new int[BlockSymbols];
    private int _symCount;
    private int _blockStart;

    private Deflater(byte[] input, int level)
    {
        _input = input;
        _level = level;
        _head.AsSpan().Fill(-1);
    }

    /// <summary>Compresses <paramref name="data"/> into a zlib stream.</summary>
    public static byte[] CompressZlib(ReadOnlySpan<byte> data, int level)
    {
        level = Math.Clamp(level, 0, 9);
        byte[] input = data.ToArray();
        var deflater = new Deflater(input, level);

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
        return deflater._out.ToArray();
    }

    /// <summary>Compresses <paramref name="data"/> into a raw deflate stream.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, int level)
    {
        var deflater = new Deflater(data.ToArray(), Math.Clamp(level, 0, 9));
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

        (int good, int lazy, int nice, int chain) = Config[_level];
        int pos = 0;
        (int len, int dist) cur = FindMatch(pos, MinMatch - 1, good, nice, chain);
        while (pos < n)
        {
            if (cur.len >= MinMatch)
            {
                if (cur.len < lazy && pos + 1 < n)
                {
                    (int len, int dist) next = FindMatch(pos + 1, cur.len, good, nice, chain);
                    if (next.len > cur.len)
                    {
                        EmitLiteral(_input[pos], pos + 1);
                        pos++;
                        cur = next;
                        continue;
                    }
                }

                EmitMatch(cur.len, cur.dist, pos + cur.len);
                pos += cur.len;
                InsertUpTo(Math.Min(pos, n - MinMatch + 1));
                cur = pos < n ? FindMatch(pos, MinMatch - 1, good, nice, chain) : default;
            }
            else
            {
                EmitLiteral(_input[pos], pos + 1);
                pos++;
                cur = pos < n ? FindMatch(pos, MinMatch - 1, good, nice, chain) : default;
            }
        }

        FlushBlock(n, final: true);
        _out.Flush();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Hash(int pos) =>
        (int)(((uint)(_input[pos] | (_input[pos + 1] << 8) | (_input[pos + 2] << 16)) * 2654435761u) >> (32 - HashBits));

    // Inserts all positions in [_inserted, end) into the hash chains.
    private void InsertUpTo(int end)
    {
        for (int p = _inserted; p < end; p++)
        {
            int h = Hash(p);
            _prev[p & WindowMask] = _head[h];
            _head[h] = p;
        }

        if (end > _inserted)
        {
            _inserted = end;
        }
    }

    // Finds the longest match at 'pos' longer than 'prevLength' (inserting 'pos' into the chains).
    private (int Length, int Distance) FindMatch(int pos, int prevLength, int good, int nice, int chain)
    {
        byte[] data = _input;
        int n = data.Length;
        if (pos + MinMatch > n)
        {
            return default;
        }

        InsertUpTo(pos);
        int h = Hash(pos);
        int candidate = _head[h];
        _prev[pos & WindowMask] = candidate;
        _head[h] = pos;
        _inserted = pos + 1;

        int maxLen = Math.Min(MaxMatch, n - pos);
        int bestLen = prevLength;
        if (bestLen >= maxLen)
        {
            return default;
        }

        int bestDist = 0;
        if (prevLength >= good)
        {
            chain >>= 2;
        }

        while (candidate >= 0 && pos - candidate <= MaxDistance && chain-- > 0)
        {
            if (data[candidate + bestLen] == data[pos + bestLen] && data[candidate] == data[pos])
            {
                int len = 0;
                while (len < maxLen && data[candidate + len] == data[pos + len])
                {
                    len++;
                }

                if (len > bestLen)
                {
                    bestLen = len;
                    bestDist = pos - candidate;
                    if (len >= nice || len >= maxLen)
                    {
                        break;
                    }
                }
            }

            int next = _prev[candidate & WindowMask];
            if (next >= candidate)
            {
                break;
            }

            candidate = next;
        }

        // Short matches far away cost more than literals (zlib's TOO_FAR heuristic).
        if (bestLen == MinMatch && bestDist > 4096)
        {
            return default;
        }

        return bestDist == 0 ? default : (bestLen, bestDist);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DistanceToCode(int distance)
    {
        int code = 0;
        while (code < 29 && DistBase[code + 1] <= distance)
        {
            code++;
        }

        return code;
    }

    private void FlushBlock(int end, bool final)
    {
        int count = _symCount;
        Span<int> litFreq = stackalloc int[NumLitLen];
        Span<int> distFreq = stackalloc int[NumDist];
        litFreq.Clear();
        distFreq.Clear();
        int[] distCodes = new int[count];
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
        BitWriter w = _out;
        for (int i = 0; i < count; i++)
        {
            int len = _symLength[i];
            if (len == 0)
            {
                int lit = _symValue[i];
                w.WriteBits(litCodes[lit], litLengths[lit]);
            }
            else
            {
                int lc = LengthToCode[len];
                w.WriteBits(litCodes[257 + lc], litLengths[257 + lc]);
                w.WriteBits((uint)(len - LengthBase[lc]), LengthExtra[lc]);
                int dc = distCodes[i];
                w.WriteBits(distCodesTable[dc], distLengths[dc]);
                w.WriteBits((uint)(_symValue[i] - DistBase[dc]), DistExtra[dc]);
            }
        }

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

    /// <summary>LSB-first bit writer into a growable buffer.</summary>
    private sealed class BitWriter
    {
        private byte[] _buffer = new byte[1 << 16];
        private int _length;
        private ulong _bits;
        private int _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBits(uint value, int count)
        {
            _bits |= (ulong)value << _count;
            _count += count;
            if (_count >= 32)
            {
                Ensure(4);
                uint v = (uint)_bits;
                _buffer[_length] = (byte)v;
                _buffer[_length + 1] = (byte)(v >> 8);
                _buffer[_length + 2] = (byte)(v >> 16);
                _buffer[_length + 3] = (byte)(v >> 24);
                _length += 4;
                _bits >>= 32;
                _count -= 32;
            }
        }

        public void AlignToByte()
        {
            while (_count > 0)
            {
                Ensure(1);
                _buffer[_length++] = (byte)_bits;
                _bits >>= 8;
                _count = Math.Max(0, _count - 8);
            }

            _bits = 0;
        }

        public void WriteByte(byte value)
        {
            AlignToByte();
            Ensure(1);
            _buffer[_length++] = value;
        }

        public void WriteBytes(ReadOnlySpan<byte> data)
        {
            AlignToByte();
            Ensure(data.Length);
            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }

        public void Flush() => AlignToByte();

        public byte[] ToArray()
        {
            AlignToByte();
            return _buffer.AsSpan(0, _length).ToArray();
        }

        private void Ensure(int extra)
        {
            if (_length + extra > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + extra));
            }
        }
    }
}

/// <summary>Length-limited canonical Huffman code construction.</summary>
internal static class HuffmanBuilder
{
    /// <summary>
    /// Computes code lengths (at most <paramref name="maxLength"/>) for the given frequencies.
    /// Symbols with zero frequency get length 0. A single used symbol gets length 1.
    /// </summary>
    public static void BuildLengths(ReadOnlySpan<int> freqs, int maxLength, Span<byte> lengths)
    {
        lengths.Clear();
        int n = 0;
        Span<long> keys = freqs.Length <= 512 ? stackalloc long[freqs.Length] : new long[freqs.Length];
        Span<int> symbols = freqs.Length <= 512 ? stackalloc int[freqs.Length] : new int[freqs.Length];
        for (int i = 0; i < freqs.Length; i++)
        {
            if (freqs[i] > 0)
            {
                keys[n] = freqs[i];
                symbols[n] = i;
                n++;
            }
        }

        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            lengths[symbols[0]] = 1;
            return;
        }

        // Sort ascending by frequency (stable by symbol).
        Span<long> sortKeys = keys[..n];
        Span<int> sortSyms = symbols[..n];
        for (int i = 0; i < n; i++)
        {
            sortKeys[i] = (sortKeys[i] << 16) | (uint)sortSyms[i];
        }

        sortKeys.Sort();
        for (int i = 0; i < n; i++)
        {
            sortSyms[i] = (int)(sortKeys[i] & 0xffff);
            sortKeys[i] >>= 16;
        }

        CalculateMinimumRedundancy(sortKeys);

        Span<int> numCodes = stackalloc int[64];
        numCodes.Clear();
        for (int i = 0; i < n; i++)
        {
            numCodes[(int)Math.Min(sortKeys[i], 63)]++;
        }

        EnforceMaxCodeSize(numCodes, n, maxLength);

        // Longest codes go to the least frequent symbols.
        int j = n;
        for (int len = 1; len <= maxLength; len++)
        {
            for (int l = numCodes[len]; l > 0; l--)
            {
                lengths[sortSyms[--j]] = (byte)len;
            }
        }
    }

    // In-place Moffat–Katajainen: on input A holds ascending frequencies, on output the code lengths.
    private static void CalculateMinimumRedundancy(Span<long> a)
    {
        int n = a.Length;
        a[0] += a[1];
        int root = 0;
        int leaf = 2;
        for (int next = 1; next < n - 1; next++)
        {
            if (leaf >= n || a[root] < a[leaf])
            {
                a[next] = a[root];
                a[root++] = next;
            }
            else
            {
                a[next] = a[leaf++];
            }

            if (leaf >= n || (root < next && a[root] < a[leaf]))
            {
                a[next] += a[root];
                a[root++] = next;
            }
            else
            {
                a[next] += a[leaf++];
            }
        }

        a[n - 2] = 0;
        for (int next = n - 3; next >= 0; next--)
        {
            a[next] = a[(int)a[next]] + 1;
        }

        int avbl = 1;
        int used = 0;
        int dpth = 0;
        int r = n - 2;
        int nx = n - 1;
        while (avbl > 0)
        {
            while (r >= 0 && a[r] == dpth)
            {
                used++;
                r--;
            }

            while (avbl > used)
            {
                a[nx--] = dpth;
                avbl--;
            }

            avbl = 2 * used;
            dpth++;
            used = 0;
        }
    }

    private static void EnforceMaxCodeSize(Span<int> numCodes, int codeListLength, int maxCodeSize)
    {
        if (codeListLength <= 1)
        {
            return;
        }

        for (int i = maxCodeSize + 1; i < numCodes.Length; i++)
        {
            numCodes[maxCodeSize] += numCodes[i];
            numCodes[i] = 0;
        }

        uint total = 0;
        for (int i = maxCodeSize; i > 0; i--)
        {
            total += (uint)numCodes[i] << (maxCodeSize - i);
        }

        while (total != (1u << maxCodeSize))
        {
            numCodes[maxCodeSize]--;
            for (int i = maxCodeSize - 1; i > 0; i--)
            {
                if (numCodes[i] != 0)
                {
                    numCodes[i]--;
                    numCodes[i + 1] += 2;
                    break;
                }
            }

            total--;
        }
    }

    /// <summary>Assigns canonical codes (bit-reversed for LSB-first output) to the given lengths.</summary>
    public static void BuildCodes(ReadOnlySpan<byte> lengths, Span<ushort> codes)
    {
        Span<int> blCount = stackalloc int[16];
        Span<int> nextCode = stackalloc int[16];
        blCount.Clear();
        foreach (byte len in lengths)
        {
            blCount[len]++;
        }

        blCount[0] = 0;
        int code = 0;
        for (int bits = 1; bits < 16; bits++)
        {
            code = (code + blCount[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        for (int i = 0; i < lengths.Length; i++)
        {
            int len = lengths[i];
            if (len != 0)
            {
                codes[i] = (ushort)Reverse(nextCode[len]++, len);
            }
        }
    }

    private static int Reverse(int code, int length)
    {
        int result = 0;
        for (int i = 0; i < length; i++)
        {
            result = (result << 1) | (code & 1);
            code >>= 1;
        }

        return result;
    }
}
