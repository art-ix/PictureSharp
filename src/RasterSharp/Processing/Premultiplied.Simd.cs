using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace RasterSharp.Processing;

internal static partial class Premultiplied
{
    /// <summary>Premultiplies whole groups of four pixels and returns how many pixels it converted.</summary>
    // MULDIV255 in 16-bit lanes: a * b + 128 and its sum with itself >> 8 stay below 65536.
    internal static int FromStraightSimd(ReadOnlySpan<Rgba32> src, Span<Rgba32> dst)
    {
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian)
        {
            return 0;
        }

        ref byte s = ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(src));
        ref byte d = ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(dst));
        Vector128<byte> alphaIndices = Vector128.Create((byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);
        Vector128<byte> alphaLanes = Vector128.Create(0xff000000u).AsByte();
        Vector128<ushort> half = Vector128.Create((ushort)128);
        int count = src.Length & ~3;
        for (int i = 0; i < count; i += 4)
        {
            Vector128<byte> pixels = Vector128.LoadUnsafe(ref s, (nuint)(i * 4));
            if ((pixels & alphaLanes) == alphaLanes)
            {
                pixels.StoreUnsafe(ref d, (nuint)(i * 4));
                continue;
            }

            // Multiply R, G, B by alpha and alpha by 255 (which MULDIV255 leaves unchanged).
            Vector128<byte> factors = Vector128.Shuffle(pixels, alphaIndices) | alphaLanes;
            (Vector128<ushort> lowPixels, Vector128<ushort> highPixels) = Vector128.Widen(pixels);
            (Vector128<ushort> lowFactors, Vector128<ushort> highFactors) = Vector128.Widen(factors);
            Vector128<ushort> low = (lowPixels * lowFactors) + half;
            Vector128<ushort> high = (highPixels * highFactors) + half;
            low = ((low >> 8) + low) >> 8;
            high = ((high >> 8) + high) >> 8;
            Vector128.Narrow(low, high).StoreUnsafe(ref d, (nuint)(i * 4));
        }

        return count;
    }

    /// <summary>Whether every pixel has alpha 255.</summary>
    public static bool IsOpaque(ReadOnlySpan<Rgba32> pixels)
    {
        ReadOnlySpan<uint> packed = MemoryMarshal.Cast<Rgba32, uint>(pixels);
        int i = 0;
        if (Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            Vector128<uint> alphaMask = Vector128.Create(0xff000000u);
            Vector128<uint> all = alphaMask;
            for (; i + Vector128<uint>.Count <= packed.Length; i += Vector128<uint>.Count)
            {
                all &= Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(packed), (nuint)i);
                if ((i & 4095) == 0 && (all & alphaMask) != alphaMask)
                {
                    return false;
                }
            }

            if ((all & alphaMask) != alphaMask)
            {
                return false;
            }
        }

        for (; i < packed.Length; i++)
        {
            if (packed[i] < 0xff000000u)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The number of leading pixels with alpha 0 or 255.</summary>
    private static int CountUnchanged(ReadOnlySpan<Rgba32> pixels)
    {
        ReadOnlySpan<uint> packed = MemoryMarshal.Cast<Rgba32, uint>(pixels);
        int i = 0;
        if (Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            Vector128<uint> alphaMask = Vector128.Create(0xff000000u);
            for (; i + Vector128<uint>.Count <= packed.Length; i += Vector128<uint>.Count)
            {
                Vector128<uint> alpha = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(packed), (nuint)i) & alphaMask;
                Vector128<uint> unchanged = Vector128.Equals(alpha, alphaMask) | Vector128.Equals(alpha, Vector128<uint>.Zero);
                if (unchanged != Vector128<uint>.AllBitsSet)
                {
                    break;
                }
            }
        }

        while (i < packed.Length && (packed[i] >> 24) is 0 or 255)
        {
            i++;
        }

        return i;
    }
}
