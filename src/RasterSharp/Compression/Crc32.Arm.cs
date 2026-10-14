using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace RasterSharp.Compression;

internal static partial class Crc32
{
    /// <summary>Adds whole 8-byte words to <paramref name="crc"/> with the ARMv8 CRC32 instructions (same reflected polynomial).</summary>
    // port: Chromium zlib crc32_simd.c armv8_crc32_little
    private static int UpdateArm(ref uint crc, ReadOnlySpan<byte> data)
    {
        int length = data.Length & ~31;
        ref byte buffer = ref MemoryMarshal.GetReference(data);
        uint c = ~crc;
        for (nuint pos = 0; (int)pos < length; pos += 32)
        {
            c = ArmCrc32.Arm64.ComputeCrc32(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, pos)));
            c = ArmCrc32.Arm64.ComputeCrc32(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, pos + 8)));
            c = ArmCrc32.Arm64.ComputeCrc32(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, pos + 16)));
            c = ArmCrc32.Arm64.ComputeCrc32(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, pos + 24)));
        }

        crc = ~c;
        return length;
    }
}
