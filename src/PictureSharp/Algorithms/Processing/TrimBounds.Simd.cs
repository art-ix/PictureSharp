using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

internal static partial class TrimBounds
{
    /// <summary>
    /// The first content pixel among whole groups of four from the start of the row, or -1; <paramref name="scanned"/>
    /// is where the scalar scan continues.
    /// </summary>
    private static int FirstContentSimd(ReadOnlySpan<Rgba32> row, AutoCropOptions options, Rgba32 background, out int scanned)
    {
        scanned = 0;
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian)
        {
            return -1;
        }

        ref uint pixels = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(row));
        int count = row.Length & ~3;
        for (int x = 0; x < count; x += 4)
        {
            uint mask = Content(Vector128.LoadUnsafe(ref pixels, (nuint)x), options, background).ExtractMostSignificantBits();
            if (mask != 0)
            {
                return x + BitOperations.TrailingZeroCount(mask);
            }
        }

        scanned = count;
        return -1;
    }

    /// <summary>
    /// The last content pixel among whole groups of four from the end of the row down to <paramref name="from"/>, or
    /// -1; the scalar scan continues below <paramref name="end"/>.
    /// </summary>
    private static int LastContentSimd(ReadOnlySpan<Rgba32> row, int from, AutoCropOptions options, Rgba32 background, out int end)
    {
        end = row.Length;
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian)
        {
            return -1;
        }

        ref uint pixels = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(row));
        for (; end - 4 >= from; end -= 4)
        {
            uint mask = Content(Vector128.LoadUnsafe(ref pixels, (nuint)(end - 4)), options, background).ExtractMostSignificantBits();
            if (mask != 0)
            {
                return end - 4 + 31 - BitOperations.LeadingZeroCount(mask);
            }
        }

        return -1;
    }

    /// <summary>All-ones lanes for the pixels that <see cref="IsContent"/> accepts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Content(Vector128<uint> p, AutoCropOptions options, Rgba32 background)
    {
        Vector128<uint> alpha = p >> 24;
        if (options.Mode == AutoCropMode.Alpha)
        {
            return Vector128.GreaterThan(alpha, Vector128.Create((uint)options.AlphaThreshold));
        }

        Vector128<byte> bytes = p.AsByte(), bg = Vector128.Create(background.PackedValue).AsByte();
        Vector128<uint> d = (Vector128.Max(bytes, bg) - Vector128.Min(bytes, bg)).AsUInt32();
        Vector128<uint> low = Vector128.Create(0xFFu);
        Vector128<uint> difference = Vector128.Max(Vector128.Max(d & low, (d >> 8) & low), Vector128.Max((d >> 16) & low, d >> 24));
        Vector128<uint> content = Vector128.GreaterThan(difference, Vector128.Create((uint)options.Tolerance));
        return background.A == 0 ? Vector128.AndNot(content, Vector128.Equals(alpha, Vector128<uint>.Zero)) : content;
    }
}
