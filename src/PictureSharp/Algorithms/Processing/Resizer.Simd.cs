using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Resizer
{
    /// <summary>Resamples a row with one RGBA pixel per vector of four 32-bit sums; returns the pixels written.</summary>
    internal static int HorizontalRowSimd(ReadOnlySpan<byte> inRow, Span<Rgba32> outRow, int ksize, int[] bounds, int[] kk)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        ref byte input = ref MemoryMarshal.GetReference(inRow);
        ref int coefficients = ref MemoryMarshal.GetArrayDataReference(kk);
        Vector128<int> bias = Vector128.Create(1 << (PrecisionBits - 1));
        for (int xx = 0; xx < outRow.Length; xx++)
        {
            int xmin = bounds[xx * 2];
            int xmax = bounds[(xx * 2) + 1];
            ref byte px = ref Unsafe.Add(ref input, xmin * 4);
            ref int k = ref Unsafe.Add(ref coefficients, xx * ksize);
            Vector128<int> sum = bias;
            for (int x = 0; x < xmax; x++)
            {
                sum += Widen(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref px, x * 4))) * Vector128.Create(Unsafe.Add(ref k, x));
            }

            outRow[xx] = new Rgba32 { PackedValue = Clip8(sum).ToScalar() };
        }

        return outRow.Length;
    }

    /// <summary>Resamples 16 bytes at a time with the taps' sums in registers; returns the bytes written.</summary>
    internal static int VerticalRowSimd(ReadOnlySpan<byte> rows, int stride, ReadOnlySpan<int> k, Span<byte> outRow)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        ref byte input = ref MemoryMarshal.GetReference(rows);
        ref byte output = ref MemoryMarshal.GetReference(outRow);
        Vector128<int> bias = Vector128.Create(1 << (PrecisionBits - 1));
        int i = 0;
        for (; i + Vector128<byte>.Count <= outRow.Length; i += Vector128<byte>.Count)
        {
            Vector128<int> s0 = bias, s1 = bias, s2 = bias, s3 = bias;
            ref byte column = ref Unsafe.Add(ref input, i);
            for (int y = 0; y < k.Length; y++)
            {
                Vector128<int> c = Vector128.Create(k[y]);
                Vector128<byte> bytes = Vector128.LoadUnsafe(ref column, (nuint)(y * stride));
                (Vector128<ushort> low, Vector128<ushort> high) = Vector128.Widen(bytes);
                (Vector128<uint> w0, Vector128<uint> w1) = Vector128.Widen(low);
                (Vector128<uint> w2, Vector128<uint> w3) = Vector128.Widen(high);
                s0 += w0.AsInt32() * c;
                s1 += w1.AsInt32() * c;
                s2 += w2.AsInt32() * c;
                s3 += w3.AsInt32() * c;
            }

            Vector128<ushort> lowWords = Vector128.Narrow(Clamp(s0), Clamp(s1)).AsUInt16();
            Vector128<ushort> highWords = Vector128.Narrow(Clamp(s2), Clamp(s3)).AsUInt16();
            Vector128.Narrow(lowWords, highWords).StoreUnsafe(ref output, (nuint)i);
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Widen(uint pixel) =>
        Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(pixel).AsByte())).AsInt32();

    // Clip8 of each lane: shift out the fraction and clamp to 0..255.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Clamp(Vector128<int> sum) =>
        Vector128.Min(Vector128.Max(sum >> PrecisionBits, Vector128<int>.Zero), Vector128.Create(255));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Clip8(Vector128<int> sum)
    {
        Vector128<short> words = Vector128.Narrow(Clamp(sum), Vector128<int>.Zero);
        return Vector128.Narrow(words.AsUInt16(), Vector128<ushort>.Zero).AsUInt32();
    }
}
