namespace RasterSharp.Formats.Jpeg;

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
