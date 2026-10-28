using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace PictureSharp.Algorithms.Processing;

internal static partial class TrimBounds
{
    /// <summary>
    /// The first content pixel among whole groups of two from the start of the row, or -1; <paramref name="scanned"/>
    /// is where the scalar scan continues.
    /// </summary>
    private static int FirstContentSimd(ReadOnlySpan<Rgba64> row, Criteria64 options, Rgba64 background, out int scanned)
    {
        scanned = 0;
        // On Cortex-A55, scalar alpha-only scans are faster; background comparisons benefit from NEON.
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian || AdvSimd.Arm64.IsSupported && options.AlphaMode)
        {
            return -1;
        }

        ref ulong pixels = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba64, ulong>(row));
        int count = row.Length & ~1;
        for (int x = 0; x < count; x += 2)
        {
            uint mask = ContentMask(Vector128.LoadUnsafe(ref pixels, (nuint)x), options, background);
            if (mask != 0)
            {
                return x + BitOperations.TrailingZeroCount(mask);
            }
        }

        scanned = count;
        return -1;
    }

    /// <summary>
    /// The last content pixel among whole groups of two from the end of the row down to <paramref name="from"/>, or
    /// -1; the scalar scan continues below <paramref name="end"/>.
    /// </summary>
    private static int LastContentSimd(ReadOnlySpan<Rgba64> row, int from, Criteria64 options, Rgba64 background, out int end)
    {
        end = row.Length;
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian || AdvSimd.Arm64.IsSupported && options.AlphaMode)
        {
            return -1;
        }

        ref ulong pixels = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba64, ulong>(row));
        for (; end - 2 >= from; end -= 2)
        {
            uint mask = ContentMask(Vector128.LoadUnsafe(ref pixels, (nuint)(end - 2)), options, background);
            if (mask != 0)
            {
                return end - 2 + 31 - BitOperations.LeadingZeroCount(mask);
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ContentMask(Vector128<ulong> p, Criteria64 options, Rgba64 background)
    {
        if (AdvSimd.Arm64.IsSupported) return ContentMaskArm64(p, options, background);
        // Use 32-bit comparisons: the channels fit signed int, and SSE2 has no 64-bit greater-than instruction.
        Vector128<int> highChannels = (p.AsUInt32() >> 16).AsInt32();
        if (options.AlphaMode)
        {
            uint mask = Vector128.GreaterThan(highChannels, Vector128.Create((int)options.AlphaThreshold)).ExtractMostSignificantBits();
            return ((mask >> 1) & 1) | ((mask >> 2) & 2);
        }

        uint visible = 3;
        if (background.A == 0)
        {
            uint alpha = Vector128.GreaterThan(highChannels, Vector128<int>.Zero).ExtractMostSignificantBits();
            visible = ((alpha >> 1) & 1) | ((alpha >> 2) & 2);
            if (visible == 0) return 0;
        }

        Vector128<ushort> channels = p.AsUInt16(), bg = Vector128.Create(background.PackedValue).AsUInt16();
        Vector128<uint> d = (Vector128.Max(channels, bg) - Vector128.Min(channels, bg)).AsUInt32();
        Vector128<uint> difference = Vector128.Max(d & Vector128.Create(65535u), d >> 16);
        uint pairs = Vector128.GreaterThan(difference.AsInt32(), Vector128.Create((int)options.Tolerance)).ExtractMostSignificantBits();
        pairs |= pairs >> 1;
        return (pairs & 1 | ((pairs >> 1) & 2)) & visible;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ContentMaskArm64(Vector128<ulong> p, Criteria64 options, Rgba64 background)
    {
        // NEON compares both 64-bit lanes directly; avoid extracting four 32-bit mask lanes into general registers.
        Vector128<ulong> alpha = p >> 48;
        Vector128<ulong> visible = Vector128.GreaterThan(alpha, Vector128<ulong>.Zero);
        if (background.A == 0 && Vector128.EqualsAll(visible, Vector128<ulong>.Zero)) return 0;
        Vector128<ushort> channels = p.AsUInt16(), bg = Vector128.Create(background.PackedValue).AsUInt16();
        Vector128<uint> d = (Vector128.Max(channels, bg) - Vector128.Min(channels, bg)).AsUInt32();
        Vector128<uint> difference = Vector128.Max(d & Vector128.Create(65535u), d >> 16);
        Vector128<ulong> content = Vector128.GreaterThan(difference.AsInt32(), Vector128.Create((int)options.Tolerance)).AsUInt64();
        content |= content << 32;
        if (background.A == 0) content &= visible;
        return content.ExtractMostSignificantBits();
    }
}
