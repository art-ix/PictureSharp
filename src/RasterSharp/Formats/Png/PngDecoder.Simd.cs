using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using RasterSharp.Internal;

namespace RasterSharp.Formats.Png;

internal sealed partial class PngDecoder
{
    // Scanline reconstruction with vectors (the byte-row helpers use x86 intrinsics and fall back to scalar
    // code elsewhere). Average stays on the scalar methods, which work a pixel at a time for 3 and 4 bytes.
    private static void UnfilterRowSimd(Span<byte> row, ReadOnlySpan<byte> previous, int bpp, int filter)
    {
        switch (filter)
        {
            case 1:
                if (bpp == 1)
                {
                    ScanlineSimd.PrefixSumBytes(row, 0);
                }
                else if (bpp == 4 && row.Length >= 8)
                {
                    // Four independent byte streams, the same recurrence as VP8L predictor 1.
                    // The row is not necessarily aligned; x86 loads here are unaligned.
                    ref uint groups = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(row));
                    ScanlineSimd.AddLeftPacked(ref Unsafe.Add(ref groups, 1), (row.Length / 4) - 1);
                }
                else
                {
                    UnfilterSubScalar(row, bpp);
                }

                break;
            case 2:
                if (!previous.IsEmpty)
                {
                    ScanlineSimd.AddByteRows(row, previous);
                }

                break;
            case 3:
                UnfilterAverageScalar(row, previous, bpp);
                break;
            case 4:
                if (bpp is 3 or 4 && !previous.IsEmpty)
                {
                    UnfilterPaethPixels(row, previous, bpp);
                }
                else
                {
                    UnfilterPaethScalar(row, previous, bpp);
                }

                break;
            case not 0:
                throw Error($"Invalid scanline filter type {filter}.");
        }
    }

    /// <summary>Paeth for 3 and 4 bytes per pixel with a non-empty previous row, one pixel per vector.</summary>
    // port: PngConstants.Paeth on 16-bit lanes; with p = a + b - c: |p - a| = |b - c|, |p - b| = |a - c|.
    internal static void UnfilterPaethPixels(Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        Vector128<short> left = Vector128<short>.Zero;
        Vector128<short> upLeft = Vector128<short>.Zero;
        Vector128<short> byteMask = Vector128.Create((short)0xff);
        int last = row.Length - bpp;
        for (int i = 0; i <= last; i += bpp)
        {
            Vector128<short> up = Widen(LoadPixel(previous, i, bpp));
            Vector128<short> bc = up - upLeft;
            Vector128<short> ac = left - upLeft;
            Vector128<short> pa = Vector128.Abs(bc);
            Vector128<short> pb = Vector128.Abs(ac);
            Vector128<short> pc = Vector128.Abs(bc + ac);
            Vector128<short> useLeft = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
            Vector128<short> useUp = Vector128.LessThanOrEqual(pb, pc);
            Vector128<short> predicted = Vector128.ConditionalSelect(useLeft, left, Vector128.ConditionalSelect(useUp, up, upLeft));
            left = (Widen(LoadPixel(row, i, bpp)) + predicted) & byteMask;
            StorePixel(row, i, bpp, Vector128.Narrow(left.AsUInt16(), left.AsUInt16()).AsUInt32().ToScalar());
            upLeft = up;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Widen(uint pixel) => Vector128.WidenLower(Vector128.CreateScalarUnsafe(pixel).AsByte()).AsInt16();
}
