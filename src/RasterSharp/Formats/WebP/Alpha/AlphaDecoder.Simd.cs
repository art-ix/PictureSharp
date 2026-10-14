using RasterSharp.Internal;

namespace RasterSharp.Formats.WebP.Alpha;

internal static partial class AlphaDecoder
{
    private static void HorizontalUnfilterX86(ReadOnlySpan<byte> prev, Span<byte> row) =>
        ScanlineSimd.PrefixSumBytes(row, prev.IsEmpty ? (byte)0 : prev[0]);

    private static void VerticalUnfilterX86(ReadOnlySpan<byte> prev, Span<byte> row)
    {
        if (prev.IsEmpty)
        {
            HorizontalUnfilterX86(prev, row);
        }
        else
        {
            ScanlineSimd.AddByteRows(row, prev);
        }
    }
}
