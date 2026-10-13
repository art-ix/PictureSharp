using System.Numerics;

namespace WebpSharp.Formats.Jpeg;

internal sealed class JpegHuffmanEncoder
{
    public byte[] Counts { get; }
    public byte[] Values { get; }
    public ushort[] Codes { get; } = new ushort[256];
    public byte[] Sizes { get; } = new byte[256];

    // port: libjpeg-turbo jchuff.c jpeg_make_c_derived_tbl
    public JpegHuffmanEncoder(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
    {
        Counts = counts.ToArray();
        Values = values.ToArray();
        int code = 0, index = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int j = 0; j < counts[length - 1]; j++)
            {
                int symbol = values[index++];
                Codes[symbol] = (ushort)code++;
                Sizes[symbol] = (byte)length;
            }

            code <<= 1;
        }
    }

    // port: libjpeg-turbo jchuff.c jpeg_gen_optimal_table
    public static JpegHuffmanEncoder Optimize(ReadOnlySpan<long> frequencies)
    {
        Span<long> freq = stackalloc long[257];
        frequencies.CopyTo(freq);
        freq[256] = 1; // Reserve the all-ones code for padding.
        Span<int> sizes = stackalloc int[257];
        Span<int> links = stackalloc int[257];
        Span<int> bits = stackalloc int[257];
        sizes.Clear();
        bits.Clear();
        links.Fill(-1);
        while (true)
        {
            int first = -1, second = -1;
            long minimum = long.MaxValue;
            for (int i = 0; i <= 256; i++)
                if (freq[i] != 0 && freq[i] <= minimum) { minimum = freq[i]; first = i; }
            minimum = long.MaxValue;
            for (int i = 0; i <= 256; i++)
                if (i != first && freq[i] != 0 && freq[i] <= minimum) { minimum = freq[i]; second = i; }
            if (second < 0) break;
            freq[first] += freq[second];
            freq[second] = 0;
            sizes[first]++;
            while (links[first] >= 0) { first = links[first]; sizes[first]++; }
            links[first] = second;
            sizes[second]++;
            while (links[second] >= 0) { second = links[second]; sizes[second]++; }
        }

        for (int i = 0; i <= 256; i++) if (sizes[i] != 0) bits[sizes[i]]++;
        for (int i = 256; i > 16; i--)
        {
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0) j--;
                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }

        int largest = 16;
        while (bits[largest] == 0) largest--;
        bits[largest]--;
        Span<byte> counts = stackalloc byte[16];
        Span<byte> values = stackalloc byte[256];
        int count = 0;
        for (int i = 0; i < 16; i++) counts[i] = (byte)bits[i + 1];
        for (int length = 1; length <= 256; length++)
            for (int symbol = 0; symbol < 256; symbol++)
                if (sizes[symbol] == length) values[count++] = (byte)symbol;
        return new JpegHuffmanEncoder(counts, values[..count]);
    }

    internal static int MagnitudeBits(int value) => 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(value));
}

internal sealed class JpegBitWriter(Stream stream)
{
    private uint _buffer;
    private int _bits;

    // port: libjpeg-turbo jcphuff.c emit_bits, flush_bits
    public void Write(int value, int count)
    {
        if (count == 0) return;
        _buffer = (_buffer << count) | ((uint)value & ((1u << count) - 1));
        _bits += count;
        while (_bits >= 8)
        {
            _bits -= 8;
            byte b = (byte)(_buffer >> _bits);
            stream.WriteByte(b);
            if (b == 255) stream.WriteByte(0);
        }
    }

    public void Flush()
    {
        if (_bits > 0) Write(127, 8 - _bits);
        _bits = 0;
        _buffer = 0;
    }

    public void Restart(int index)
    {
        Flush();
        stream.WriteByte(255);
        stream.WriteByte((byte)(0xD0 + (index & 7)));
    }
}
