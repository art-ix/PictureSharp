using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace RasterSharp.Formats.Jpeg;

/// <summary>MSB-first entropy-coded segment writer with 0xFF byte stuffing, buffered in front of the stream.</summary>
internal sealed class JpegBitWriter(Stream stream)
{
    private readonly byte[] _output = new byte[1 << 16];
    private int _length;
    private ulong _buffer;
    private int _bits;  // < 32 between calls

    // port: libjpeg-turbo jcphuff.c emit_bits, flush_bits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int value, int count)
    {
        if (count == 0) return;
        _buffer = (_buffer << count) | ((uint)value & ((1u << count) - 1));
        _bits += count;
        if (_bits >= 32) Emit(32);
    }

    public void Flush()
    {
        if ((_bits & 7) != 0) Write(127, 8 - (_bits & 7));
        if (_bits > 0) Emit(_bits);
        _buffer = 0;
        stream.Write(_output, 0, _length);
        _length = 0;
    }

    public void Restart(int index)
    {
        Flush();
        stream.WriteByte(255);
        stream.WriteByte((byte)(0xD0 + (index & 7)));
    }

    // Moves the oldest 'count' buffered bits (a multiple of 8, at most 32) to the output.
    private void Emit(int count)
    {
        if (_length > _output.Length - 8)
        {
            stream.Write(_output, 0, _length);
            _length = 0;
        }

        _bits -= count;
        uint word = (uint)(_buffer >> _bits) << (32 - count);
        _buffer &= (1UL << _bits) - 1;

        // A 0xFF byte is followed by a stuffed zero; otherwise all bytes go out at once.
        if (count == 32 && !HasFFByte(word))
        {
            BinaryPrimitives.WriteUInt32BigEndian(_output.AsSpan(_length), word);
            _length += 4;
            return;
        }

        for (int i = 0; i < count; i += 8)
        {
            byte b = (byte)(word >> (24 - i));
            _output[_length++] = b;
            if (b == 255) _output[_length++] = 0;
        }
    }

    // A 0xFF byte is a zero byte of the complement.
    private static bool HasFFByte(uint word)
    {
        uint inverse = ~word;
        return ((inverse - 0x01010101u) & ~inverse & 0x80808080u) != 0;
    }
}
