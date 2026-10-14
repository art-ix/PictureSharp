using RasterSharp.Internal;

namespace RasterSharp.Formats.WebP.Alpha;

internal static partial class AlphaDecoder
{
    private static void UnfilterX86(AlphaFilterType filter, Span<byte> plane, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = plane.Slice(y * width, width);
            ReadOnlySpan<byte> prev = y == 0 ? default : plane.Slice((y - 1) * width, width);
            switch (filter)
            {
                case AlphaFilterType.Horizontal:
                    ScanlineSimd.PrefixSumBytes(row, prev.IsEmpty ? (byte)0 : prev[0]);
                    break;
                case AlphaFilterType.Vertical:
                    if (prev.IsEmpty)
                    {
                        ScanlineSimd.PrefixSumBytes(row, 0);
                    }
                    else
                    {
                        ScanlineSimd.AddByteRows(row, prev);
                    }

                    break;
                case AlphaFilterType.Gradient:
                    GradientUnfilter(prev, row);
                    break;
            }
        }
    }
}
