using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RasterSharp.Internal;

/// <summary>x86 vector bodies for <see cref="ScanlineSimd"/>. Each method returns how far it got; the scalar file finishes the tail.</summary>
internal static partial class ScanlineSimd
{
    // port: src/dsp/lossless_sse2.c PredictorAdd1_SSE2, src/dsp/lossless_avx2.c PredictorAdd1_AVX2
    private static int AddLeftPackedSimd(ref uint residuals, int count)
    {
        int done = 0;
        if (Avx2.IsSupported)
        {
            Vector256<byte> prev = Vector256.Create(Unsafe.Add(ref residuals, -1)).AsByte();
            for (; done + 8 <= count; done += 8)
            {
                ref byte at = ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref residuals, done));
                Vector256<byte> src = Vector256.LoadUnsafe(ref at);
                Vector256<byte> sum0 = Avx2.Add(src, Avx2.ShiftLeftLogical128BitLane(src, 4));
                Vector256<byte> sum1 = Avx2.Add(sum0, Avx2.ShiftLeftLogical128BitLane(sum0, 8));
                int block = sum1.AsInt32().GetElement(3);
                Vector256<byte> sum2 = Avx2.Add(sum1, Vector256.Create(0, 0, 0, 0, block, block, block, block).AsByte());
                Vector256<byte> res = Avx2.Add(sum2, prev);
                res.StoreUnsafe(ref at);
                prev = Avx2.PermuteVar8x32(res.AsInt32(), Vector256.Create(7, 7, 7, 7, 7, 7, 7, 7)).AsByte();
            }
        }

        if (Sse2.IsSupported)
        {
            Vector128<byte> prev = Vector128.Create(Unsafe.Add(ref residuals, done - 1)).AsByte();
            for (; done + 4 <= count; done += 4)
            {
                ref byte at = ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref residuals, done));
                Vector128<byte> src = Vector128.LoadUnsafe(ref at);
                Vector128<byte> sum0 = Sse2.Add(src, Sse2.ShiftLeftLogical128BitLane(src, 4));
                Vector128<byte> sum1 = Sse2.Add(sum0, Sse2.ShiftLeftLogical128BitLane(sum0, 8));
                Vector128<byte> res = Sse2.Add(sum1, prev);
                res.StoreUnsafe(ref at);
                prev = Sse2.Shuffle(res.AsInt32(), 0xFF).AsByte();
            }
        }

        return done;
    }

    private static (int Done, byte Carry) PrefixSumBytesSimd(Span<byte> data, byte seed)
    {
        if (data.IsEmpty || !Sse2.IsSupported)
        {
            return (0, seed);
        }

        ref byte b = ref MemoryMarshal.GetReference(data);
        int i = 0;
        byte carry = seed;
        if (Avx2.IsSupported)
        {
            for (; i + 32 <= data.Length; i += 32)
            {
                Vector256<byte> block = Avx2.Add(Prefix32(Vector256.LoadUnsafe(ref b, (nuint)i)), Vector256.Create(carry));
                block.StoreUnsafe(ref b, (nuint)i);
                carry = block.GetElement(31);
            }
        }

        for (; i + 16 <= data.Length; i += 16)
        {
            Vector128<byte> block = Sse2.Add(Prefix16(Vector128.LoadUnsafe(ref b, (nuint)i)), Vector128.Create(carry));
            block.StoreUnsafe(ref b, (nuint)i);
            carry = block.GetElement(15);
        }

        return (i, carry);
    }

    private static int AddByteRowsSimd(Span<byte> row, ReadOnlySpan<byte> previous)
    {
        int n = Math.Min(row.Length, previous.Length);
        if (n == 0 || !Sse2.IsSupported)
        {
            return 0;
        }

        ref byte r = ref MemoryMarshal.GetReference(row);
        ref byte p = ref MemoryMarshal.GetReference(previous);
        int i = 0;
        if (Avx2.IsSupported)
        {
            for (; i + 32 <= n; i += 32)
            {
                Avx2.Add(Vector256.LoadUnsafe(ref r, (nuint)i), Vector256.LoadUnsafe(ref p, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
            }
        }

        for (; i + 16 <= n; i += 16)
        {
            Sse2.Add(Vector128.LoadUnsafe(ref r, (nuint)i), Vector128.LoadUnsafe(ref p, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Prefix16(Vector128<byte> value)
    {
        value = Sse2.Add(value, Sse2.ShiftLeftLogical128BitLane(value, 1));
        value = Sse2.Add(value, Sse2.ShiftLeftLogical128BitLane(value, 2));
        value = Sse2.Add(value, Sse2.ShiftLeftLogical128BitLane(value, 4));
        return Sse2.Add(value, Sse2.ShiftLeftLogical128BitLane(value, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Prefix32(Vector256<byte> value)
    {
        value = Avx2.Add(value, Avx2.ShiftLeftLogical128BitLane(value, 1));
        value = Avx2.Add(value, Avx2.ShiftLeftLogical128BitLane(value, 2));
        value = Avx2.Add(value, Avx2.ShiftLeftLogical128BitLane(value, 4));
        value = Avx2.Add(value, Avx2.ShiftLeftLogical128BitLane(value, 8));
        Vector128<byte> high = Sse2.Add(value.GetUpper(), Vector128.Create(value.GetElement(15)));
        return Vector256.Create(value.GetLower(), high);
    }
}
