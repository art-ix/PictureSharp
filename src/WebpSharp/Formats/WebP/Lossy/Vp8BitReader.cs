using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace WebpSharp.Formats.WebP.Lossy;

/// <summary>Boolean (arithmetic) decoder of the VP8 bitstream.</summary>
// port: src/utils/bit_reader_utils.[ch], src/utils/bit_reader_inl_utils.h (BITS = 56)
internal sealed class Vp8BitReader
{
    private const int Bits = 56;

    private readonly byte[] _buf;
    private readonly int _end;      // end of read buffer
    private readonly int _max;      // max packed-read position on buffer
    private ulong _value;           // current value
    private uint _range;            // current range minus 1. In [127, 254] interval.
    private int _bits;              // number of valid bits left
    private int _pos;               // next byte to be read
    private bool _eof;              // true if input is exhausted

    // port: VP8InitBitReader
    public Vp8BitReader(byte[] buffer, int offset, int size)
    {
        _buf = buffer;
        _range = 255 - 1;
        _value = 0;
        _bits = -8;  // to load the very first 8 bits
        _eof = false;
        _pos = offset;
        _end = offset + size;
        _max = size >= sizeof(ulong) ? offset + size - sizeof(ulong) + 1 : offset;
        LoadNewBytes();
    }

    public bool Eof => _eof;

    // port: VP8LoadNewBytes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LoadNewBytes()
    {
        if (_pos < _max)
        {
            ulong bits = BinaryPrimitives.ReadUInt64BigEndian(_buf.AsSpan(_pos, 8)) >> (64 - Bits);
            _pos += Bits >> 3;
            _value = bits | (_value << Bits);
            _bits += Bits;
        }
        else
        {
            LoadFinalBytes();
        }
    }

    // port: VP8LoadFinalBytes
    private void LoadFinalBytes()
    {
        // Only read 8 bits at a time.
        if (_pos < _end)
        {
            _bits += 8;
            _value = _buf[_pos++] | (_value << 8);
        }
        else if (!_eof)
        {
            _value <<= 8;
            _bits += 8;
            _eof = true;
        }
        else
        {
            _bits = 0;  // to avoid undefined behaviour with shifts
        }
    }

    // port: VP8GetBit
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBit(int prob)
    {
        uint range = _range;
        if (_bits < 0)
        {
            LoadNewBytes();
        }

        int pos = _bits;
        uint split = (range * (uint)prob) >> 8;
        uint value = (uint)(_value >> pos);
        int bit;
        if (value > split)
        {
            range -= split;
            _value -= (ulong)(split + 1) << pos;
            bit = 1;
        }
        else
        {
            range = split + 1;
            bit = 0;
        }

        int shift = 7 ^ BitOperations.Log2(range);
        range <<= shift;
        _bits -= shift;
        _range = range - 1;
        return bit;
    }

    // port: VP8GetSigned. Simplified GetBit() for prob=0x80, returning +v or -v.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetSigned(int v)
    {
        if (_bits < 0)
        {
            LoadNewBytes();
        }

        int pos = _bits;
        uint split = _range >> 1;
        uint value = (uint)(_value >> pos);
        int mask = (int)(split - value) >> 31;  // -1 or 0
        _bits -= 1;
        _range += (uint)mask;
        _range |= 1;
        _value -= (ulong)((split + 1) & (uint)mask) << pos;
        return (v ^ mask) - mask;
    }

    // port: VP8GetValue
    public int GetValue(int bits)
    {
        int v = 0;
        while (bits-- > 0)
        {
            v |= GetBit(0x80) << bits;
        }

        return v;
    }

    // port: VP8Get
    public int Get() => GetValue(1);

    // port: VP8GetSignedValue
    public int GetSignedValue(int bits)
    {
        int value = GetValue(bits);
        return Get() != 0 ? -value : value;
    }
}
