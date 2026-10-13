using System.Buffers;

namespace RasterSharp.Formats.WebP.Lossless;

/// <summary>Least-significant-bit-first writer, including unaligned stream concatenation.</summary>
internal sealed class Vp8LBitWriter : IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(256);
    public long BitCount { get; private set; }
    public int ByteCount => checked((int)((BitCount + 7) >> 3));

    // port: src/utils/bit_writer_utils.c VP8LPutBits
    public void Put(uint value, int bits)
    {
        if (bits == 0) return;
        int needed = checked((int)((BitCount + bits + 7) >> 3) + 4);
        if (needed > _buffer.Length)
        {
            byte[] next = ArrayPool<byte>.Shared.Rent(Math.Max(needed, (int)Math.Min(Array.MaxLength, 2L * _buffer.Length)));
            _buffer.AsSpan(0, ByteCount).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }
        for (int remaining = bits; remaining > 0;)
        {
            int index = (int)(BitCount >> 3), offset = (int)(BitCount & 7);
            if (offset == 0) _buffer[index] = 0;
            int count = Math.Min(8 - offset, remaining);
            _buffer[index] |= (byte)((value & ((1u << count) - 1)) << offset);
            BitCount += count;
            remaining -= count;
            value >>= count;
        }
    }

    public void Put(int value, int bits) => Put((uint)value, bits);

    public void Append(Vp8LBitWriter other)
    {
        for (int i = 0; i < other.ByteCount; i++) Put(other._buffer[i], (int)Math.Min(8, other.BitCount - (long)i * 8));
    }

    public byte[] ToArray() => _buffer.AsSpan(0, ByteCount).ToArray();
    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}
