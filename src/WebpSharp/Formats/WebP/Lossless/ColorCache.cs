using System.Runtime.CompilerServices;

namespace WebpSharp.Formats.WebP.Lossless;

// port: src/utils/color_cache_utils.[ch]
internal sealed class ColorCache
{
    private const uint HashMul = 0x1e35a7bdu;

    public ColorCache(int hashBits)
    {
        HashBits = hashBits;
        HashShift = 32 - hashBits;
        Colors = new uint[1 << hashBits];
    }

    public uint[] Colors { get; }

    public int HashBits { get; }

    public int HashShift { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HashPix(uint argb, int shift) => (int)((argb * HashMul) >> shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Lookup(int key) => Colors[key];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Insert(uint argb) => Colors[HashPix(argb, HashShift)] = argb;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetIndex(uint argb) => HashPix(argb, HashShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Contains(uint argb)
    {
        int key = HashPix(argb, HashShift);
        return Colors[key] == argb ? key : -1;
    }

    public void Set(int key, uint argb) => Colors[key] = argb;
}
