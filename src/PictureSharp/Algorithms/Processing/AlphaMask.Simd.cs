using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Algorithms.Processing;

internal static partial class AlphaMask
{
    /// <summary>Applies the mask to whole groups of four pixels; returns the pixels processed.</summary>
    // round(a · v / 255) = (a · v + 127) / 255 = MULDIV255(a · v + 128) for a · v ≤ 65025, in 32-bit lanes.
    private static int ApplyRowSimd(Span<Rgba32> target, ReadOnlySpan<Rgba32> maskRow, bool luminance, int flip)
    {
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian)
        {
            return 0;
        }

        ref uint t = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(target));
        ref uint m = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(maskRow));
        Vector128<uint> low = Vector128.Create(0xFFu);
        Vector128<uint> colors = Vector128.Create(0x00FFFFFFu);
        Vector128<uint> half = Vector128.Create(128u);
        int count = target.Length & ~3;
        for (int i = 0; i < count; i += 4)
        {
            Vector128<uint> mask = Vector128.LoadUnsafe(ref m, (nuint)i);
            Vector128<uint> v = luminance
                ? ((Vector128.Create(19595u) * (mask & low)) + (Vector128.Create(38470u) * ((mask >> 8) & low))
                    + (Vector128.Create(7471u) * ((mask >> 16) & low)) + Vector128.Create(32768u)) >> 16
                : mask >> 24;
            v ^= Vector128.Create((uint)flip);
            Vector128<uint> pixels = Vector128.LoadUnsafe(ref t, (nuint)i);
            Vector128<uint> product = ((pixels >> 24) * v) + half;
            Vector128<uint> alpha = ((product >> 8) + product) >> 8;
            ((pixels & colors) | (alpha << 24)).StoreUnsafe(ref t, (nuint)i);
        }

        return count;
    }

    /// <summary>Clears the alpha of whole groups of four pixels; returns the pixels processed.</summary>
    private static int ClearAlphaSimd(Span<Rgba32> pixels)
    {
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian)
        {
            return 0;
        }

        ref uint p = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(pixels));
        Vector128<uint> colors = Vector128.Create(0x00FFFFFFu);
        int count = pixels.Length & ~3;
        for (int i = 0; i < count; i += 4)
        {
            (Vector128.LoadUnsafe(ref p, (nuint)i) & colors).StoreUnsafe(ref p, (nuint)i);
        }

        return count;
    }
}
