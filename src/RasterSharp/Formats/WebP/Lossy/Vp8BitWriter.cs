using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RasterSharp.Formats.WebP.Lossy;

internal sealed class Vp8BitWriter : IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(1024);
    private int _range = 254, _value, _run, _bits = -8, _size;
    private bool _finished;
    public long Position => 8L * (_size + _run) + 8 + _bits;

    // port: src/utils/bit_writer_utils.c VP8PutBit
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Put(bool bit, int probability)
    {
        int split = (_range * probability) >> 8;
        if (bit) { _value += split + 1; _range -= split + 1; }
        else _range = split;
        if (_range < 127)
        {
            int shift = 7 - BitOperations.Log2((uint)(_range + 1));
            _range = ((_range + 1) << shift) - 1;
            _value <<= shift; _bits += shift;
            if (_bits > 0) Flush();
        }
        return bit;
    }

    // port: src/utils/bit_writer_utils.c VP8PutBitUniform, VP8PutBits, VP8PutSignedBits
    public bool Put(bool bit) => Put(bit, 128);
    public void PutBits(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--) Put(((value >> i) & 1) != 0);
    }
    public void PutSigned(int value, int bits)
    {
        if (Put(value != 0)) PutBits((uint)((Math.Abs(value) << 1) | (value < 0 ? 1 : 0)), bits + 1);
    }

    // port: src/utils/bit_writer_utils.c Flush
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Flush()
    {
        int shift = 8 + _bits, bits = _value >> shift;
        _value -= bits << shift; _bits -= 8;
        if ((bits & 255) == 255) { _run++; return; }
        int needed = checked(_size + _run + 1);
        if (needed > _buffer.Length)
        {
            byte[] next = ArrayPool<byte>.Shared.Rent(Math.Max(needed, (int)Math.Min(Array.MaxLength, 2L * _buffer.Length)));
            _buffer.AsSpan(0, _size).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer); _buffer = next;
        }
        if ((bits & 256) != 0 && _size > 0) _buffer[_size - 1]++;
        _buffer.AsSpan(_size, _run).Fill((byte)((bits & 256) != 0 ? 0 : 255));
        _size += _run; _run = 0;
        _buffer[_size++] = (byte)bits;
    }

    // port: src/utils/bit_writer_utils.c VP8BitWriterFinish
    public byte[] Finish()
    {
        if (!_finished)
        {
            PutBits(0, 9 - _bits); _bits = 0; Flush(); _finished = true;
        }
        return _buffer.AsSpan(0, _size).ToArray();
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}
