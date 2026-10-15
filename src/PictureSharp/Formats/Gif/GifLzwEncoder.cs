namespace PictureSharp.Formats.Gif;

/// <summary>Variable-length-code LZW encoder producing GIF image data in 255-byte sub-blocks.</summary>
internal static class GifLzwEncoder
{
    private const int HashSize = 8192;   // power of two, at least twice the number of codes

    /// <summary>Writes the minimum code size byte, the data sub-blocks and the block terminator.</summary>
    public static void Encode(ReadOnlySpan<byte> indices, int minCodeSize, Stream output)
    {
        var writer = new SubBlockWriter(output);
        output.WriteByte((byte)minCodeSize);

        int clear = 1 << minCodeSize;
        int endOfInformation = clear + 1;
        Span<int> keys = stackalloc int[HashSize];      // (prefix << 8 | symbol) + 1, 0 = empty
        Span<short> codes = stackalloc short[HashSize];
        keys.Clear();

        int codeSize = minCodeSize + 1;
        int nextCode = endOfInformation + 1;
        writer.WriteCode(clear, codeSize);
        if (indices.IsEmpty)
        {
            writer.WriteCode(endOfInformation, codeSize);
            writer.Finish();
            return;
        }

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int symbol = indices[i];
            int key = ((prefix << 8) | symbol) + 1;
            int slot = Hash(key);
            while (keys[slot] != 0 && keys[slot] != key)
            {
                slot = (slot + 1) & (HashSize - 1);
            }

            if (keys[slot] == key)
            {
                prefix = codes[slot];
                continue;
            }

            writer.WriteCode(prefix, codeSize);
            if (nextCode < GifConstants.MaxCodes)
            {
                keys[slot] = key;
                codes[slot] = (short)nextCode++;

                // The decoder defines each code one step later, so widen once it can receive code 2^codeSize.
                if (nextCode > (1 << codeSize) && codeSize < GifConstants.MaxCodeBits)
                {
                    codeSize++;
                }
            }
            else
            {
                // Table full: start over rather than keep encoding with a frozen table.
                writer.WriteCode(clear, codeSize);
                keys.Clear();
                nextCode = endOfInformation + 1;
                codeSize = minCodeSize + 1;
            }

            prefix = symbol;
        }

        writer.WriteCode(prefix, codeSize);
        writer.WriteCode(endOfInformation, codeSize);
        writer.Finish();
    }

    private static int Hash(int key) => (int)(((uint)key * 2654435761u) >> 19);

    /// <summary>Packs codes LSB-first and emits them as sub-blocks of at most 255 bytes.</summary>
    private ref struct SubBlockWriter(Stream output)
    {
        private readonly Stream _output = output;
        private readonly byte[] _block = new byte[256];
        private int _length;
        private uint _bits;
        private int _bitCount;

        public void WriteCode(int code, int size)
        {
            _bits |= (uint)code << _bitCount;
            _bitCount += size;
            while (_bitCount >= 8)
            {
                Put((byte)_bits);
                _bits >>= 8;
                _bitCount -= 8;
            }
        }

        public void Finish()
        {
            if (_bitCount > 0)
            {
                Put((byte)_bits);
                _bits = 0;
                _bitCount = 0;
            }

            Flush();
            _output.WriteByte(0);
        }

        private void Put(byte value)
        {
            _block[1 + _length++] = value;
            if (_length == 255)
            {
                Flush();
            }
        }

        private void Flush()
        {
            if (_length > 0)
            {
                _block[0] = (byte)_length;
                _output.Write(_block, 0, _length + 1);
                _length = 0;
            }
        }
    }
}
