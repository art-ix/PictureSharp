using System.Numerics;

namespace RasterSharp.Formats.Bmp;

/// <summary>A color channel described by a contiguous bit mask, scaled to 8 bits on extraction.</summary>
internal readonly struct BmpChannel
{
    private readonly uint _mask;
    private readonly int _shift;
    private readonly uint _max;

    public BmpChannel(uint mask)
    {
        _mask = mask;
        _shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
        _max = mask >> _shift;
    }

    public uint Mask => _mask;

    public int Bits => BitOperations.PopCount(_mask);

    public bool IsContiguous => (_max & (_max + 1)) == 0;

    /// <summary>Extracts the channel; values are scaled with v * 255 / max (truncating), as Pillow does for 15/16-bit pixels.</summary>
    public byte Extract(uint pixel, byte ifAbsent)
    {
        if (_mask == 0)
        {
            return ifAbsent;
        }

        uint v = (pixel & _mask) >> _shift;
        return _max == 255 ? (byte)v : (byte)((ulong)v * 255 / _max);
    }
}
