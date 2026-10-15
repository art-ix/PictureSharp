using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

/// <summary>Little-endian bit reader for the VP8L bitstream with a 64-bit prefetch window.</summary>
// port: src/utils/bit_reader_utils.[ch] (VP8LBitReader)
internal sealed class Vp8LBitReader
{
    public const int MaxNumBitRead = 24;
    private const int LBits = 64;   // number of bits prefetched
    private const int WBits = 32;   // minimum number of bits ready after FillBitWindow

    private readonly byte[] _buf;
    private readonly int _end;      // absolute end of data in _buf
    private ulong _val;
    private int _pos;               // absolute byte position in _buf
    private int _bitPos;            // current bit-reading position in _val
    private bool _eos;

    // port: VP8LInitBitReader
    public Vp8LBitReader(byte[] buffer, int offset, int length)
    {
        _buf = buffer;
        _end = offset + length;
        int n = Math.Min(length, 8);
        ulong value = 0;
        for (int i = 0; i < n; i++)
        {
            value |= (ulong)buffer[offset + i] << (8 * i);
        }

        _val = value;
        _pos = offset + n;
    }

    public bool Eos
    {
        get => _eos;
        set => _eos = value;
    }

    public int BitPos
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bitPos;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _bitPos = value;
    }

    // port: VP8LReadBits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int nBits)
    {
        if (!_eos && nBits <= MaxNumBitRead)
        {
            uint val = PrefetchBits() & ((1u << nBits) - 1);
            _bitPos += nBits;
            // Keep at least 32 bits available, refilling a word instead of shifting each consumed byte.
            FillBitWindow();
            return val;
        }

        SetEndOfStream();
        return 0;
    }

    // port: VP8LPrefetchBits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PrefetchBits() => (uint)(_val >> (_bitPos & (LBits - 1)));

    // port: VP8LIsEndOfStream
    public bool IsEndOfStream
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _eos || (_pos == _end && _bitPos > LBits);
    }

    // port: VP8LFillBitWindow, VP8LDoFillBitWindow
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FillBitWindow()
    {
        if (_bitPos >= WBits)
        {
            if (_pos < _end - 8)
            {
                _val >>= WBits;
                _bitPos -= WBits;
                _val |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_pos, 4)) << (LBits - WBits);
                _pos += 4;
            }
            else
            {
                ShiftBytes();
            }
        }
    }

    // port: ShiftBytes
    private void ShiftBytes()
    {
        while (_bitPos >= 8 && _pos < _end)
        {
            _val >>= 8;
            _val |= (ulong)_buf[_pos] << (LBits - 8);
            _pos++;
            _bitPos -= 8;
        }

        if (IsEndOfStream)
        {
            SetEndOfStream();
        }
    }

    private void SetEndOfStream()
    {
        _eos = true;
        _bitPos = 0;  // to avoid undefined behaviour with shifts
    }
}
