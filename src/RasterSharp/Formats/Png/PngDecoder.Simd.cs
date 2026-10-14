using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using RasterSharp.Internal;

namespace RasterSharp.Formats.Png;

internal sealed partial class PngDecoder
{
    // x86 scanline reconstruction. Average and Paeth stay on the scalar methods.
    private static void UnfilterX86(byte[] data, int offset, int width, int height, int bitsPerPixel)
    {
        WalkRows(data, offset, width, height, bitsPerPixel, static (row, previous, bpp, filter) =>
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
                    UnfilterPaethScalar(row, previous, bpp);
                    break;
                case not 0:
                    throw Error($"Invalid scanline filter type {filter}.");
            }
        });
    }
}
