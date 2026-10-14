using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace RasterSharp.Formats.Png;

internal sealed partial class PngEncoder
{
    /// <summary>Scans whole groups of four pixels; stops early once a pixel is translucent and one is not gray.</summary>
    private static (int Done, bool HasAlpha, bool IsGray) ScanAlphaAndGraySimd(ReadOnlySpan<Rgba32> pixels)
    {
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian)
        {
            return (0, false, true);
        }

        ReadOnlySpan<uint> packed = MemoryMarshal.Cast<Rgba32, uint>(pixels);
        Vector128<uint> opaque = Vector128.Create(0xff000000u);
        Vector128<uint> channel = Vector128.Create(0xffu);
        Vector128<uint> notOpaque = Vector128<uint>.Zero;
        Vector128<uint> notGray = Vector128<uint>.Zero;
        int i = 0;
        for (; i + Vector128<uint>.Count <= packed.Length; i += Vector128<uint>.Count)
        {
            Vector128<uint> v = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(packed), (nuint)i);
            notOpaque |= (v & opaque) ^ opaque;
            Vector128<uint> r = v & channel;
            notGray |= (r ^ ((v >> 8) & channel)) | (r ^ ((v >> 16) & channel));
            if ((i & 1023) == 0 && notOpaque != Vector128<uint>.Zero && notGray != Vector128<uint>.Zero)
            {
                return (i + Vector128<uint>.Count, true, false);
            }
        }

        return (i, notOpaque != Vector128<uint>.Zero, notGray == Vector128<uint>.Zero);
    }
}
