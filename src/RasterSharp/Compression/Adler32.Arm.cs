using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace RasterSharp.Compression;

internal static partial class Adler32
{
    /// <summary>Adds whole 32-byte blocks to <paramref name="adler"/> and returns the number of bytes consumed.</summary>
    // port: Chromium zlib adler32_simd.c adler32_simd_ (NEON)
    private static int UpdateArm(ref uint adler, ReadOnlySpan<byte> data)
    {
        uint s1 = adler & 0xffff;
        uint s2 = adler >> 16;
        int blocks = data.Length / BlockSize;
        ref byte buffer = ref MemoryMarshal.GetReference(data);
        nuint pos = 0;

        // Byte i of a block is counted (32 - i) times in s2.
        Vector64<ushort> tap1 = Vector64.Create((ushort)32, 31, 30, 29), tap2 = Vector64.Create((ushort)28, 27, 26, 25);
        Vector64<ushort> tap3 = Vector64.Create((ushort)24, 23, 22, 21), tap4 = Vector64.Create((ushort)20, 19, 18, 17);
        Vector64<ushort> tap5 = Vector64.Create((ushort)16, 15, 14, 13), tap6 = Vector64.Create((ushort)12, 11, 10, 9);
        Vector64<ushort> tap7 = Vector64.Create((ushort)8, 7, 6, 5), tap8 = Vector64.Create((ushort)4, 3, 2, 1);
        while (blocks > 0)
        {
            int n = Math.Min(blocks, NMax / BlockSize);
            blocks -= n;

            // vS2 collects s1 before each block; every such s1 is counted 32 times in s2.
            Vector128<uint> vS2 = Vector128.CreateScalar(s1 * (uint)n);
            Vector128<uint> vS1 = Vector128<uint>.Zero;
            Vector128<ushort> column1 = Vector128<ushort>.Zero, column2 = Vector128<ushort>.Zero;
            Vector128<ushort> column3 = Vector128<ushort>.Zero, column4 = Vector128<ushort>.Zero;
            do
            {
                Vector128<byte> bytes1 = Vector128.LoadUnsafe(ref buffer, pos);
                Vector128<byte> bytes2 = Vector128.LoadUnsafe(ref buffer, pos + 16);
                vS2 += vS1;
                vS1 = AdvSimd.AddPairwiseWideningAndAdd(vS1, AdvSimd.AddPairwiseWideningAndAdd(AdvSimd.AddPairwiseWidening(bytes1), bytes2));
                column1 = AdvSimd.AddWideningLower(column1, bytes1.GetLower());
                column2 = AdvSimd.AddWideningUpper(column2, bytes1);
                column3 = AdvSimd.AddWideningLower(column3, bytes2.GetLower());
                column4 = AdvSimd.AddWideningUpper(column4, bytes2);
                pos += BlockSize;
            }
            while (--n > 0);

            vS2 <<= 5;
            vS2 = AdvSimd.MultiplyWideningLowerAndAdd(vS2, column1.GetLower(), tap1);
            vS2 = AdvSimd.MultiplyWideningLowerAndAdd(vS2, column1.GetUpper(), tap2);
            vS2 = AdvSimd.MultiplyWideningLowerAndAdd(vS2, column2.GetLower(), tap3);
            vS2 = AdvSimd.MultiplyWideningLowerAndAdd(vS2, column2.GetUpper(), tap4);
            vS2 = AdvSimd.MultiplyWideningLowerAndAdd(vS2, column3.GetLower(), tap5);
            vS2 = AdvSimd.MultiplyWideningLowerAndAdd(vS2, column3.GetUpper(), tap6);
            vS2 = AdvSimd.MultiplyWideningLowerAndAdd(vS2, column4.GetLower(), tap7);
            vS2 = AdvSimd.MultiplyWideningLowerAndAdd(vS2, column4.GetUpper(), tap8);
            s1 += Vector128.Sum(vS1);
            s2 += Vector128.Sum(vS2);
            s1 %= Base;
            s2 %= Base;
        }

        adler = (s2 << 16) | s1;
        return (int)pos;
    }
}
