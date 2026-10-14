using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RasterSharp.Formats.WebP.Lossless;

namespace RasterSharp.Compression;

/// <summary>Thrown for malformed deflate / zlib streams.</summary>
internal sealed class InvalidDataStreamException(string message) : Exception(message);

/// <summary>DEFLATE (RFC 1951) and zlib (RFC 1950) decompressor.</summary>
/// <remarks>
/// Huffman decoding uses the same two-level lookup tables as the VP8L decoder (both formats use
/// canonical codes packed LSB-first).
/// </remarks>
internal sealed class Inflater
{
    private const int LitLenRootBits = 10;
    private const int DistRootBits = 8;
    private const int CodeLengthRootBits = 7;
    private const int EndOfBlock = 256;

    private static ReadOnlySpan<ushort> LengthBase =>
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static ReadOnlySpan<byte> LengthExtra =>
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static ReadOnlySpan<ushort> DistBase =>
        [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    private static ReadOnlySpan<byte> DistExtra =>
        [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    private static ReadOnlySpan<byte> CodeLengthOrder => [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly Lazy<(HuffmanCode[] LitLen, HuffmanCode[] Dist)> FixedTables = new(BuildFixedTables);

    private readonly byte[] _input;
    private readonly int _inputEnd;
    private int _inputPos;
    private ulong _bitBuf;
    private int _bitCount;

    private byte[] _output;
    // The logical size of _output; a rented buffer may be longer, which must not change any decision.
    private int _capacity;
    private readonly byte[]? _rented;
    private int _outputPos;
    private readonly long _maxOutput;
    private Action<byte[], int>? _progress;

    private Inflater(byte[] input, int offset, int length, int expectedSize, long maxOutput, bool pooled = false)
    {
        _input = input;
        _inputPos = offset;
        _inputEnd = offset + length;
        _capacity = Math.Max(expectedSize, 64);
        _output = pooled ? ArrayPool<byte>.Shared.Rent(_capacity) : GC.AllocateUninitializedArray<byte>(_capacity);
        _rented = pooled ? _output : null;
        _maxOutput = maxOutput;
    }

    /// <summary>Decompresses a zlib stream (2-byte header, deflate data, Adler-32 trailer).</summary>
    /// <param name="input">Buffer holding the stream.</param>
    /// <param name="offset">Start of the stream.</param>
    /// <param name="length">Length of the stream.</param>
    /// <param name="expectedSize">Expected output size (a capacity hint).</param>
    /// <param name="maxOutput">Maximum allowed output size; protects against decompression bombs.</param>
    /// <param name="verifyChecksum">Whether to verify the Adler-32 trailer.</param>
    /// <param name="stopAtExpectedSize">Stop once <paramref name="expectedSize"/> bytes are produced (tolerates trailing junk, like libpng).</param>
    /// <param name="progress">
    /// Called after each block with the output buffer and the number of bytes produced. Bytes already produced never
    /// change; a later call may pass a larger copy of the buffer.
    /// </param>
    public static byte[] InflateZlib(byte[] input, int offset, int length, int expectedSize, long maxOutput, bool verifyChecksum = true,
        bool stopAtExpectedSize = false, Action<byte[], int>? progress = null)
    {
        Inflater inflater = RunZlib(input, offset, length, expectedSize, maxOutput, verifyChecksum, stopAtExpectedSize, progress, pooled: false);
        return inflater.TakeOutput();
    }

    /// <summary>
    /// Like <see cref="InflateZlib"/>, but the output starts in a buffer rented from the shared pool: read
    /// <see cref="PooledOutput.Data"/>[..<see cref="PooledOutput.Length"/>], then call <see cref="PooledOutput.Return"/>
    /// once nothing reads it any more (including <paramref name="progress"/> consumers).
    /// </summary>
    public static PooledOutput InflateZlibPooled(byte[] input, int offset, int length, int expectedSize, long maxOutput,
        bool verifyChecksum = true, bool stopAtExpectedSize = false, Action<byte[], int>? progress = null)
    {
        Inflater inflater = RunZlib(input, offset, length, expectedSize, maxOutput, verifyChecksum, stopAtExpectedSize, progress, pooled: true);
        return new PooledOutput(inflater._output, inflater._outputPos, inflater._rented);
    }

    /// <summary>Inflated bytes in <see cref="Data"/>[..<see cref="Length"/>]; <see cref="Return"/> gives a rented buffer back.</summary>
    public readonly record struct PooledOutput(byte[] Data, int Length, byte[]? Rented)
    {
        public void Return()
        {
            if (Rented is not null)
            {
                ArrayPool<byte>.Shared.Return(Rented);
            }
        }
    }

    private static Inflater RunZlib(byte[] input, int offset, int length, int expectedSize, long maxOutput, bool verifyChecksum,
        bool stopAtExpectedSize, Action<byte[], int>? progress, bool pooled)
    {
        if (length < 2)
        {
            throw new InvalidDataStreamException("zlib stream is too short.");
        }

        int cmf = input[offset];
        int flg = input[offset + 1];
        if ((cmf & 0x0f) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0)
        {
            throw new InvalidDataStreamException("Invalid zlib header.");
        }

        if ((flg & 0x20) != 0)
        {
            throw new InvalidDataStreamException("zlib preset dictionaries are not supported.");
        }

        var inflater = new Inflater(input, offset + 2, length - 2, expectedSize, maxOutput, pooled) { _progress = progress };
        bool complete;
        try
        {
            complete = inflater.Run(stopAtExpectedSize ? expectedSize : -1);
        }
        catch
        {
            inflater.ReturnRented();
            throw;
        }

        ReadOnlySpan<byte> result = inflater._output.AsSpan(0, inflater._outputPos);
        if (complete && verifyChecksum)
        {
            inflater.AlignToByte();
            int trailer = inflater.ReadAlignedBytes(4);
            if (trailer >= 0)
            {
                uint expected = (uint)((inflater._input[trailer] << 24) | (inflater._input[trailer + 1] << 16) |
                                       (inflater._input[trailer + 2] << 8) | inflater._input[trailer + 3]);
                if (Adler32.Compute(result) != expected)
                {
                    inflater.ReturnRented();
                    throw new InvalidDataStreamException("zlib checksum mismatch.");
                }
            }
        }

        return inflater;
    }

    private void ReturnRented()
    {
        if (_rented is not null)
        {
            ArrayPool<byte>.Shared.Return(_rented);
        }
    }

    /// <summary>Decompresses raw deflate data.</summary>
    public static byte[] Inflate(byte[] input, int offset, int length, int expectedSize, long maxOutput)
    {
        var inflater = new Inflater(input, offset, length, expectedSize, maxOutput);
        inflater.Run(-1);
        return inflater.TakeOutput();
    }

    // The output buffer itself when it is exactly full (the usual case with an exact size hint).
    private byte[] TakeOutput() => _outputPos == _output.Length ? _output : _output.AsSpan(0, _outputPos).ToArray();

    // Returns true when the final block was fully decoded, false when stopped at 'stopAt' bytes.
    private bool Run(int stopAt)
    {
        bool final;
        do
        {
            final = ReadBits(1) != 0;
            int type = (int)ReadBits(2);
            switch (type)
            {
                case 0:
                    StoredBlock();
                    break;
                case 1:
                {
                    (HuffmanCode[] litLen, HuffmanCode[] dist) = FixedTables.Value;
                    HuffmanBlock(litLen, dist);
                    break;
                }

                case 2:
                {
                    (HuffmanCode[] litLen, HuffmanCode[]? dist) = ReadDynamicTables();
                    HuffmanBlock(litLen, dist);
                    break;
                }

                default:
                    throw new InvalidDataStreamException("Invalid deflate block type.");
            }

            _progress?.Invoke(_output, _outputPos);
            if (stopAt >= 0 && _outputPos >= stopAt)
            {
                return final;
            }
        }
        while (!final);

        return true;
    }

    // ------------------------------------------------------------------ bit input

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Refill()
    {
        while (_bitCount <= 56)
        {
            if (_inputPos < _inputEnd)
            {
                _bitBuf |= (ulong)_input[_inputPos++] << _bitCount;
            }
            else if (_bitCount > 0)
            {
                // Pad with zeros past the end; reading them is detected in Consume.
                return;
            }
            else
            {
                return;
            }

            _bitCount += 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadBits(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        if (_bitCount < n)
        {
            Refill();
            if (_bitCount < n)
            {
                throw new InvalidDataStreamException("Unexpected end of deflate stream.");
            }
        }

        uint value = (uint)(_bitBuf & ((1UL << n) - 1));
        _bitBuf >>= n;
        _bitCount -= n;
        return value;
    }

    private void AlignToByte()
    {
        int drop = _bitCount & 7;
        _bitBuf >>= drop;
        _bitCount -= drop;
    }

    // Returns the input offset of 'count' aligned bytes (taking buffered bits into account), or -1.
    private int ReadAlignedBytes(int count)
    {
        // Give back whole buffered bytes to the input.
        int buffered = _bitCount >> 3;
        _inputPos -= buffered;
        _bitBuf = 0;
        _bitCount = 0;
        if (_inputEnd - _inputPos < count)
        {
            return -1;
        }

        int start = _inputPos;
        _inputPos += count;
        return start;
    }

    // Decodes one symbol with a table built by HuffmanTableBuilder.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeSymbol(HuffmanCode[] table, int rootBits)
    {
        if (_bitCount < 15)
        {
            Refill();
        }

        int index = (int)(_bitBuf & ((1UL << rootBits) - 1));
        HuffmanCode code = table[index];
        int nbits = code.Bits - rootBits;
        if (nbits > 0)
        {
            index += code.Value + (int)((_bitBuf >> rootBits) & ((1UL << nbits) - 1));
            code = table[index];
            if (code.Bits + rootBits > _bitCount || code.Bits == 0)
            {
                throw new InvalidDataStreamException("Unexpected end of deflate stream.");
            }

            _bitBuf >>= rootBits + code.Bits;
            _bitCount -= rootBits + code.Bits;
            return code.Value;
        }

        if (code.Bits > _bitCount || code.Bits == 0)
        {
            throw new InvalidDataStreamException(code.Bits == 0 ? "Invalid Huffman code in deflate stream." : "Unexpected end of deflate stream.");
        }

        _bitBuf >>= code.Bits;
        _bitCount -= code.Bits;
        return code.Value;
    }

    // ------------------------------------------------------------------ output

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int extra)
    {
        long needed = (long)_outputPos + extra;
        if (needed <= _capacity)
        {
            return;
        }

        if (needed > _maxOutput)
        {
            throw new InvalidDataStreamException("Decompressed data exceeds the allowed size.");
        }

        long newSize = Math.Min(Math.Max(needed, (long)_capacity * 2), Math.Min(_maxOutput, Array.MaxLength));
        // A rented buffer is never resized in place: a progress consumer may still read it, so it is returned last.
        byte[] next = GC.AllocateUninitializedArray<byte>((int)newSize);
        _output.AsSpan(0, _outputPos).CopyTo(next);
        _output = next;
        _capacity = (int)newSize;
    }

    // ------------------------------------------------------------------ blocks

    private void StoredBlock()
    {
        AlignToByte();
        int start = ReadAlignedBytes(4);
        if (start < 0)
        {
            throw new InvalidDataStreamException("Unexpected end of deflate stream.");
        }

        int len = _input[start] | (_input[start + 1] << 8);
        int nlen = _input[start + 2] | (_input[start + 3] << 8);
        if ((len ^ 0xffff) != nlen)
        {
            throw new InvalidDataStreamException("Invalid stored block length.");
        }

        if (_inputEnd - _inputPos < len)
        {
            throw new InvalidDataStreamException("Unexpected end of deflate stream.");
        }

        EnsureCapacity(len);
        Array.Copy(_input, _inputPos, _output, _outputPos, len);
        _inputPos += len;
        _outputPos += len;
    }

    private void HuffmanBlock(HuffmanCode[] litLen, HuffmanCode[]? dist)
    {
        while (true)
        {
            if (FastHuffmanBlock(litLen, dist))
            {
                return;
            }

            int sym = DecodeSymbol(litLen, LitLenRootBits);
            if (sym < 256)
            {
                EnsureCapacity(1);
                _output[_outputPos++] = (byte)sym;
                continue;
            }

            if (sym == EndOfBlock)
            {
                return;
            }

            sym -= 257;
            if (sym >= 29)
            {
                throw new InvalidDataStreamException("Invalid deflate length code.");
            }

            int length = LengthBase[sym] + (int)ReadBits(LengthExtra[sym]);
            if (dist is null)
            {
                throw new InvalidDataStreamException("Deflate block has no distance codes.");
            }

            int dsym = DecodeSymbol(dist, DistRootBits);
            if (dsym >= 30)
            {
                throw new InvalidDataStreamException("Invalid deflate distance code.");
            }

            int distance = DistBase[dsym] + (int)ReadBits(DistExtra[dsym]);
            if (distance > _outputPos)
            {
                throw new InvalidDataStreamException("Deflate distance too far back.");
            }

            EnsureCapacity(length);
            byte[] output = _output;
            int src = _outputPos - distance;
            if (distance >= length)
            {
                Array.Copy(output, src, output, _outputPos, length);
            }
            else
            {
                for (int i = 0; i < length; i++)
                {
                    output[_outputPos + i] = output[src + i];
                }
            }

            _outputPos += length;
        }
    }

    /// <summary>
    /// Decodes symbols while at least 8 input bytes and room for a whole match (plus 8 bytes of copy overrun)
    /// remain; returns true at the end of the block. The checks match <see cref="HuffmanBlock"/>, which takes over
    /// near the end of the input or output.
    /// </summary>
    // One refill leaves at least 56 bits: enough for a length code with extra bits (15 + 5) and a distance
    // code with extra bits (15 + 13). Bits above the count may hold copies of the next input bytes; they are
    // cleared before returning, because Refill expects zeros there.
    private bool FastHuffmanBlock(HuffmanCode[] litLen, HuffmanCode[]? dist)
    {
        byte[] input = _input;
        byte[] output = _output;
        int ip = _inputPos;
        int op = _outputPos;
        int inputLimit = _inputEnd - sizeof(ulong);
        int outputLimit = _capacity - (258 + sizeof(ulong));
        if (ip > inputLimit || op > outputLimit)
        {
            return false;
        }

        ulong bits = _bitBuf;
        int count = _bitCount;
        ref byte inputRef = ref MemoryMarshal.GetArrayDataReference(input);
        ref byte outputRef = ref MemoryMarshal.GetArrayDataReference(output);
        ref HuffmanCode litLenRef = ref MemoryMarshal.GetArrayDataReference(litLen);
        // Exceptions leave the state as it is: the inflater is not used after a failure. (A try/finally here would
        // keep the bit buffer in memory instead of registers.)
        bool endOfBlock = false;
        while (ip <= inputLimit && op <= outputLimit)
        {
            if (count < 56)
            {
                bits |= BinaryPrimitives.ReadUInt64LittleEndian(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref inputRef, ip), sizeof(ulong))) << count;
                ip += (63 - count) >> 3;
                count |= 56;
            }

            int sym = Decode(ref litLenRef, LitLenRootBits, ref bits, ref count);
            if (sym < 256)
            {
                Unsafe.Add(ref outputRef, op++) = (byte)sym;
                continue;
            }

            if (sym == EndOfBlock)
            {
                endOfBlock = true;
                break;
            }

            sym -= 257;
            if (sym >= 29)
            {
                throw new InvalidDataStreamException("Invalid deflate length code.");
            }

            int extra = LengthExtra[sym];
            int length = LengthBase[sym] + (int)(bits & ((1UL << extra) - 1));
            bits >>= extra;
            count -= extra;
            if (dist is null)
            {
                throw new InvalidDataStreamException("Deflate block has no distance codes.");
            }

            int dsym = Decode(ref MemoryMarshal.GetArrayDataReference(dist), DistRootBits, ref bits, ref count);
            if (dsym >= 30)
            {
                throw new InvalidDataStreamException("Invalid deflate distance code.");
            }

            extra = DistExtra[dsym];
            int distance = DistBase[dsym] + (int)(bits & ((1UL << extra) - 1));
            bits >>= extra;
            count -= extra;
            if (distance > op)
            {
                throw new InvalidDataStreamException("Deflate distance too far back.");
            }

            CopyMatch(ref outputRef, op, distance, length);
            op += length;
        }

        _bitBuf = bits & ((1UL << count) - 1);
        _bitCount = count;
        _inputPos = ip;
        _outputPos = op;
        return endOfBlock;
    }

    // Decodes one symbol from a full bit buffer (at least 30 bits); see DecodeSymbol.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Decode(ref HuffmanCode table, int rootBits, ref ulong bits, ref int count)
    {
        int index = (int)(bits & ((1UL << rootBits) - 1));
        HuffmanCode code = Unsafe.Add(ref table, index);
        if (code.Bits > rootBits)
        {
            index += code.Value + (int)((bits >> rootBits) & ((1UL << (code.Bits - rootBits)) - 1));
            code = Unsafe.Add(ref table, index);
            bits >>= rootBits;
            count -= rootBits;
        }

        if (code.Bits == 0)
        {
            throw new InvalidDataStreamException("Invalid Huffman code in deflate stream.");
        }

        bits >>= code.Bits;
        count -= code.Bits;
        return code.Value;
    }

    // Copies an LZ77 match; may write up to 8 bytes past the match (the caller leaves room).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyMatch(ref byte output, int op, int distance, int length)
    {
        ref byte dst = ref Unsafe.Add(ref output, op);
        ref byte src = ref Unsafe.Add(ref output, op - distance);
        if (distance >= sizeof(ulong))
        {
            for (int i = 0; i < length; i += sizeof(ulong))
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, i)));
            }
        }
        else if (distance == 1)
        {
            MemoryMarshal.CreateSpan(ref dst, length).Fill(src);
        }
        else
        {
            for (int i = 0; i < length; i++)
            {
                Unsafe.Add(ref dst, i) = Unsafe.Add(ref src, i);
            }
        }
    }

    private (HuffmanCode[] LitLen, HuffmanCode[]? Dist) ReadDynamicTables()
    {
        int hlit = (int)ReadBits(5) + 257;
        int hdist = (int)ReadBits(5) + 1;
        int hclen = (int)ReadBits(4) + 4;
        if (hlit > 286)
        {
            throw new InvalidDataStreamException("Too many deflate length codes.");
        }

        Span<int> clLengths = stackalloc int[19];
        clLengths.Clear();
        for (int i = 0; i < hclen; i++)
        {
            clLengths[CodeLengthOrder[i]] = (int)ReadBits(3);
        }

        HuffmanCode[] clTable = BuildTable(clLengths, CodeLengthRootBits)
            ?? throw new InvalidDataStreamException("Invalid deflate code length code.");

        Span<int> lengths = stackalloc int[286 + 32];
        int n = 0;
        int total = hlit + hdist;
        while (n < total)
        {
            int sym = DecodeSymbol(clTable, CodeLengthRootBits);
            if (sym < 16)
            {
                lengths[n++] = sym;
                continue;
            }

            int repeat;
            int value = 0;
            if (sym == 16)
            {
                if (n == 0)
                {
                    throw new InvalidDataStreamException("Deflate repeat code without a previous length.");
                }

                value = lengths[n - 1];
                repeat = 3 + (int)ReadBits(2);
            }
            else if (sym == 17)
            {
                repeat = 3 + (int)ReadBits(3);
            }
            else
            {
                repeat = 11 + (int)ReadBits(7);
            }

            if (n + repeat > total)
            {
                throw new InvalidDataStreamException("Deflate code lengths overflow.");
            }

            lengths.Slice(n, repeat).Fill(value);
            n += repeat;
        }

        if (lengths[EndOfBlock] == 0)
        {
            throw new InvalidDataStreamException("Deflate block has no end-of-block code.");
        }

        HuffmanCode[] litLen = BuildTable(lengths[..hlit], LitLenRootBits)
            ?? throw new InvalidDataStreamException("Invalid deflate literal/length code.");

        ReadOnlySpan<int> distLengths = lengths.Slice(hlit, hdist);
        HuffmanCode[]? dist = null;
        if (distLengths.ContainsAnyExcept(0))
        {
            dist = BuildTable(distLengths, DistRootBits)
                ?? throw new InvalidDataStreamException("Invalid deflate distance code.");
        }

        return (litLen, dist);
    }

    /// <summary>
    /// Builds a lookup table; besides complete codes, accepts the single-code case (one symbol of
    /// length 1) like zlib. Returns null for invalid codes.
    /// </summary>
    private static HuffmanCode[]? BuildTable(ReadOnlySpan<int> lengths, int rootBits)
    {
        int used = 0;
        int single = -1;
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] != 0)
            {
                used++;
                single = i;
            }
        }

        if (used == 1)
        {
            if (lengths[single] != 1)
            {
                return null;
            }

            // Code "0" decodes the symbol; code "1" is invalid (bits = 0 marks it).
            var table = new HuffmanCode[1 << rootBits];
            for (int i = 0; i < table.Length; i += 2)
            {
                table[i] = new HuffmanCode(1, single);
            }

            return table;
        }

        return HuffmanTableBuilder.Build(rootBits, lengths);
    }

    private static (HuffmanCode[] LitLen, HuffmanCode[] Dist) BuildFixedTables()
    {
        Span<int> lengths = stackalloc int[288];
        lengths[..144].Fill(8);
        lengths[144..256].Fill(9);
        lengths[256..280].Fill(7);
        lengths[280..288].Fill(8);
        HuffmanCode[] litLen = HuffmanTableBuilder.Build(LitLenRootBits, lengths)!;
        Span<int> dist = stackalloc int[30];
        dist.Fill(5);
        // 30 codes of length 5 form an incomplete code; pad to 32 so the table builder accepts it.
        Span<int> distPadded = stackalloc int[32];
        distPadded.Fill(5);
        HuffmanCode[] distTable = HuffmanTableBuilder.Build(DistRootBits, distPadded)!;
        return (litLen, distTable);
    }
}
