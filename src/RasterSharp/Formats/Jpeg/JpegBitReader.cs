using System.Runtime.CompilerServices;

namespace RasterSharp.Formats.Jpeg;

/// <summary>
/// Reads the entropy-coded segment of a scan: removes stuffed zero bytes and stops at the next marker.
/// Like libjpeg, it supplies zero bits once the segment is exhausted and flags the shortage.
/// </summary>
internal ref struct JpegBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private ulong _bits;   // left-aligned
    private int _count;
    private bool _atMarker;

    public JpegBitReader(ReadOnlySpan<byte> data, int position)
    {
        _data = data;
        _pos = position;
    }

    /// <summary>Position of the first byte not yet loaded (a marker once the segment is exhausted).</summary>
    public readonly int Position => _pos;

    /// <summary>Set when more bits were consumed than the segment holds; cleared by a successful restart.</summary>
    public bool InsufficientData;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBits(int n)
    {
        if (_count < n)
        {
            Fill();
        }

        int value = (int)(_bits >> 1 >> (63 - n));
        Consume(n);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBit() => GetBits(1);

    /// <summary>Reads <paramref name="n"/> bits and sign-extends them as a JPEG magnitude category value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Receive(int n)
    {
        int v = GetBits(n);
        // port: libjpeg-turbo jdhuff.h HUFF_EXTEND
        return v < 1 << (n - 1) ? v + (-1 << n) + 1 : v;
    }

    // port: libjpeg-turbo jdhuff.h HUFF_DECODE / jdhuff.c jpeg_huff_decode
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(JpegHuffmanTable table)
    {
        if (_count < 16)
        {
            Fill();
        }

        int entry = table.Lookup[(int)(_bits >> (64 - JpegHuffmanTable.LookupBits))];
        if (entry != 0)
        {
            Consume(entry >> 8);
            return entry & 0xFF;
        }

        return DecodeSlow(table);
    }

    private int DecodeSlow(JpegHuffmanTable table)
    {
        int code = (int)(_bits >> 48);
        for (int l = JpegHuffmanTable.LookupBits + 1; l <= 16; l++)
        {
            int c = code >> (16 - l);
            if (c <= table.MaxCode[l])
            {
                Consume(l);
                return table.Values[(c + table.ValOffset[l]) & 0xFF];
            }
        }

        // Corrupt data: libjpeg substitutes a zero symbol.
        Consume(16);
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Consume(int n)
    {
        if (n > _count)
        {
            InsufficientData = true;
            _bits = 0;
            _count = 0;
        }
        else
        {
            _bits <<= n;
            _count -= n;
        }
    }

    // port: libjpeg-turbo jdhuff.c jpeg_fill_bit_buffer
    private void Fill()
    {
        ReadOnlySpan<byte> data = _data;
        while (_count <= 56 && !_atMarker)
        {
            if (_pos >= data.Length)
            {
                _atMarker = true;
                break;
            }

            int b = data[_pos];
            if (b == 0xFF)
            {
                // FF 00 is a stuffed FF data byte; padding FFs before either are skipped.
                int p = _pos + 1;
                while (p < data.Length && data[p] == 0xFF)
                {
                    p++;
                }

                if (p >= data.Length || data[p] != 0)
                {
                    _atMarker = true;
                    break;
                }

                _pos = p + 1;
            }
            else
            {
                _pos++;
            }

            _bits |= (ulong)b << (56 - _count);
            _count += 8;
        }
    }

    /// <summary>
    /// Handles a restart interval boundary: discards the remaining bits and swallows the expected RSTn marker,
    /// resynchronizing like libjpeg when the markers are out of order.
    /// </summary>
    // port: libjpeg-turbo jdhuff.c process_restart, jdmarker.c read_restart_marker, jpeg_resync_to_restart
    public void Restart(int expected)
    {
        _bits = 0;
        _count = 0;
        _atMarker = false;
        int marker = JpegMarkers.Next(_data, ref _pos, out int after);
        while (true)
        {
            int action;
            if (marker < JpegMarkers.Sof0)
            {
                action = 2;   // invalid marker
            }
            else if (marker is < JpegMarkers.Rst0 or > JpegMarkers.Rst7)
            {
                action = 3;   // valid non-restart marker
            }
            else if (marker == JpegMarkers.Rst0 + ((expected + 1) & 7) || marker == JpegMarkers.Rst0 + ((expected + 2) & 7))
            {
                action = 3;   // one of the next two expected restarts
            }
            else if (marker == JpegMarkers.Rst0 + ((expected - 1) & 7) || marker == JpegMarkers.Rst0 + ((expected - 2) & 7))
            {
                action = 2;   // a prior restart, so advance
            }
            else
            {
                action = 1;   // the desired restart or too far away
            }

            if (action == 1)
            {
                _pos = after;
                InsufficientData = false;
                return;
            }

            if (action == 3)
            {
                // Leave the marker unread; the next segment decodes as empty.
                _atMarker = true;
                return;
            }

            _pos = after;
            marker = JpegMarkers.Next(_data, ref _pos, out after);
        }
    }
}
