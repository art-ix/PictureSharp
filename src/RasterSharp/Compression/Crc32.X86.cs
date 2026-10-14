using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RasterSharp.Compression;

internal static partial class Crc32
{
    // Folding constants for the reflected polynomial 0x04C11DB7 (Intel, "Fast CRC Computation for Generic
    // Polynomials Using PCLMULQDQ"): x^(4*128+32), x^(4*128-32), x^(128+32), x^(128-32), x^64 mod P, P and mu.
    private const ulong K1 = 0x0154442bd4, K2 = 0x01c6e41596;
    private const ulong K3 = 0x01751997d0, K4 = 0x00ccaa009e;
    private const ulong K5 = 0x0163cd6124;
    private const ulong Poly = 0x01db710641, Mu = 0x01f7011641;

    /// <summary>Folds whole 16-byte blocks into <paramref name="crc"/> and returns the number of bytes consumed.</summary>
    // port: Chromium zlib crc32_simd.c crc32_sse42_simd_
    private static int UpdateX86(ref uint crc, ReadOnlySpan<byte> data)
    {
        int length = data.Length & ~15;
        ref byte buffer = ref MemoryMarshal.GetReference(data);
        Vector128<ulong> x1 = Vector128.LoadUnsafe(ref buffer).AsUInt64();
        Vector128<ulong> x2 = Vector128.LoadUnsafe(ref buffer, 16).AsUInt64();
        Vector128<ulong> x3 = Vector128.LoadUnsafe(ref buffer, 32).AsUInt64();
        Vector128<ulong> x4 = Vector128.LoadUnsafe(ref buffer, 48).AsUInt64();
        x1 ^= Vector128.CreateScalar(~crc).AsUInt64();
        Vector128<ulong> k = Vector128.Create(K1, K2);
        nuint pos = 64;
        for (; (int)pos + 64 <= length; pos += 64)
        {
            x1 = Fold(x1, k) ^ Vector128.LoadUnsafe(ref buffer, pos).AsUInt64();
            x2 = Fold(x2, k) ^ Vector128.LoadUnsafe(ref buffer, pos + 16).AsUInt64();
            x3 = Fold(x3, k) ^ Vector128.LoadUnsafe(ref buffer, pos + 32).AsUInt64();
            x4 = Fold(x4, k) ^ Vector128.LoadUnsafe(ref buffer, pos + 48).AsUInt64();
        }

        k = Vector128.Create(K3, K4);
        x1 = Fold(x1, k) ^ x2;
        x1 = Fold(x1, k) ^ x3;
        x1 = Fold(x1, k) ^ x4;
        for (; (int)pos < length; pos += 16)
        {
            x1 = Fold(x1, k) ^ Vector128.LoadUnsafe(ref buffer, pos).AsUInt64();
        }

        // 128 to 64 bits, then Barrett reduction to 32 bits.
        Vector128<ulong> mask = Vector128.Create(~0u, 0, ~0u, 0).AsUInt64();
        x2 = Pclmulqdq.CarrylessMultiply(x1, k, 0x10);
        x1 = Sse2.ShiftRightLogical128BitLane(x1, 8) ^ x2;
        x2 = Sse2.ShiftRightLogical128BitLane(x1, 4);
        x1 = Pclmulqdq.CarrylessMultiply(x1 & mask, Vector128.CreateScalar(K5), 0x00) ^ x2;
        Vector128<ulong> poly = Vector128.Create(Poly, Mu);
        x2 = Pclmulqdq.CarrylessMultiply(x1 & mask, poly, 0x10) & mask;
        x2 = Pclmulqdq.CarrylessMultiply(x2, poly, 0x00);
        x1 ^= x2;
        crc = ~Sse41.Extract(x1.AsUInt32(), 1);
        return length;
    }

    private static Vector128<ulong> Fold(Vector128<ulong> x, Vector128<ulong> k) =>
        Pclmulqdq.CarrylessMultiply(x, k, 0x00) ^ Pclmulqdq.CarrylessMultiply(x, k, 0x11);
}
