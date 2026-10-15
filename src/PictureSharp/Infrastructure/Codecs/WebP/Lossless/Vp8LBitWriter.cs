using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

/// <summary>Least-significant-bit-first writer, including unaligned stream concatenation.</summary>
// The bytes after the current one are kept zero, so a write of up to 32 bits ORs one unaligned 64-bit word.
internal sealed class Vp8LBitWriter : IDisposable
{
    private const int Slack = 8;
    private byte[] _buffer = RentCleared(256);
    public long BitCount { get; private set; }
    public int ByteCount => checked((int)((BitCount + 7) >> 3));

    // port: src/utils/bit_writer_utils.c VP8LPutBits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(uint value, int bits)
    {
        Debug.Assert(bits is >= 0 and <= 32);
        if (bits == 0) return;
        int index = (int)(BitCount >> 3);
        if (index + Slack > _buffer.Length) Grow(index + Slack);
        Span<byte> at = _buffer.AsSpan(index, Slack);
        ulong word = BinaryPrimitives.ReadUInt64LittleEndian(at);
        word |= (ulong)(value & (uint)((1UL << bits) - 1)) << (int)(BitCount & 7);
        BinaryPrimitives.WriteUInt64LittleEndian(at, word);
        BitCount += bits;
    }

    public void Put(int value, int bits) => Put((uint)value, bits);

    public void Append(Vp8LBitWriter other)
    {
        int count = other.ByteCount;
        if ((BitCount & 7) == 0)
        {
            // Byte-aligned: copy whole bytes; the last partial byte keeps its zero padding.
            int start = ByteCount;
            if (start + count + Slack > _buffer.Length) Grow(start + count + Slack);
            other._buffer.AsSpan(0, count).CopyTo(_buffer.AsSpan(start));
            BitCount += other.BitCount;
            return;
        }
        int i = 0;
        for (; (i + 4) * 8L <= other.BitCount; i += 4) Put(BinaryPrimitives.ReadUInt32LittleEndian(other._buffer.AsSpan(i)), 32);
        for (; i < count; i++) Put(other._buffer[i], (int)Math.Min(8, other.BitCount - (long)i * 8));
    }

    private void Grow(int needed)
    {
        byte[] next = RentCleared(Math.Max(needed, (int)Math.Min(Array.MaxLength, 2L * _buffer.Length)));
        _buffer.AsSpan(0, ByteCount).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    private static byte[] RentCleared(int size)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        Array.Clear(buffer);
        return buffer;
    }

    public byte[] ToArray() => _buffer.AsSpan(0, ByteCount).ToArray();
    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}
