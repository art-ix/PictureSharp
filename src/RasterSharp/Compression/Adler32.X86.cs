using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RasterSharp.Compression;

internal static partial class Adler32
{
    private const int BlockSize = 32;

    /// <summary>Adds whole 32-byte blocks to <paramref name="adler"/> and returns the number of bytes consumed.</summary>
    // port: Chromium zlib adler32_simd.c adler32_simd_ (SSSE3)
    private static int UpdateSimd(ref uint adler, ReadOnlySpan<byte> data)
    {
        if (!Ssse3.IsSupported || data.Length < BlockSize)
        {
            return 0;
        }

        uint s1 = adler & 0xffff;
        uint s2 = adler >> 16;
        int blocks = data.Length / BlockSize;
        ref byte buffer = ref MemoryMarshal.GetReference(data);
        nuint pos = 0;

        // Byte i of a block is counted (32 - i) times in s2.
        Vector128<sbyte> tap1 = Vector128.Create(32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17);
        Vector128<sbyte> tap2 = Vector128.Create(16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
        Vector128<short> ones = Vector128.Create((short)1);
        while (blocks > 0)
        {
            int n = Math.Min(blocks, NMax / BlockSize);
            blocks -= n;

            // vPs collects s1 before each block; every such s1 is counted 32 times in s2.
            Vector128<uint> vPs = Vector128.CreateScalar(s1 * (uint)n);
            Vector128<uint> vS2 = Vector128.CreateScalar(s2);
            Vector128<uint> vS1 = Vector128<uint>.Zero;
            do
            {
                Vector128<byte> bytes1 = Vector128.LoadUnsafe(ref buffer, pos);
                Vector128<byte> bytes2 = Vector128.LoadUnsafe(ref buffer, pos + 16);
                vPs += vS1;
                vS1 += Sse2.SumAbsoluteDifferences(bytes1, Vector128<byte>.Zero).AsUInt32();
                vS2 += Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(bytes1, tap1), ones).AsUInt32();
                vS1 += Sse2.SumAbsoluteDifferences(bytes2, Vector128<byte>.Zero).AsUInt32();
                vS2 += Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(bytes2, tap2), ones).AsUInt32();
                pos += BlockSize;
            }
            while (--n > 0);

            vS2 += Sse2.ShiftLeftLogical(vPs, 5);
            s1 += Vector128.Sum(vS1);
            s2 = Vector128.Sum(vS2);
            s1 %= Base;
            s2 %= Base;
        }

        adler = (s2 << 16) | s1;
        return (int)pos;
    }
}
