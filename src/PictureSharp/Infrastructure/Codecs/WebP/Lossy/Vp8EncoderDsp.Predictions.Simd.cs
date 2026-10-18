using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

// Intra predictions of the encoder (port: src/dsp/enc_sse2.c Intra16Preds_SSE2, IntraChromaPreds_SSE2,
// Intra4Preds_SSE2). Each one writes exactly the bytes of its scalar counterpart in Vp8EncoderDsp.cs.
internal static partial class Vp8EncoderDsp
{
    private const int I4Size = 1668 + (3 * Bps) + 4;

    public static void PredLuma16(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        if (!Simd.IsSupported128)
        {
            PredLuma16Scalar(output, left, top);
            return;
        }

        if (output.Length < 528 + (15 * Bps) + 16 || (!left.IsEmpty && left.Length < 17) || (!top.IsEmpty && top.Length < 16))
            Vp8Dsp.ThrowBlockOutOfRange();
        ref byte dst = ref MemoryMarshal.GetReference(output);
        bool hasLeft = !left.IsEmpty, hasTop = !top.IsEmpty;
        ref byte l = ref hasLeft ? ref MemoryMarshal.GetReference(left) : ref dst;
        Vector128<byte> above = hasTop ? Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(top)) : default;

        // DC16Mode
        int dc = 128;
        if (hasTop || hasLeft)
        {
            int sum = (hasTop ? SumBytes(above) : 0) + (hasLeft ? SumBytes(Vector128.LoadUnsafe(ref l, 1)) : 0);
            dc = hasTop && hasLeft ? (sum + 16) >> 5 : (sum + 8) >> 4;
        }

        Fill16(ref dst, Vector128.Create((byte)dc));
        // VerticalPred, HorizontalPred
        Fill16(ref Unsafe.Add(ref dst, 512), hasTop ? above : Vector128.Create((byte)127));
        if (hasLeft) Horizontal16(ref Unsafe.Add(ref dst, 528), ref l);
        else Fill16(ref Unsafe.Add(ref dst, 528), Vector128.Create((byte)129));

        // TrueMotion
        ref byte tm = ref Unsafe.Add(ref dst, 16);
        if (!hasLeft) Fill16(ref tm, hasTop ? above : Vector128.Create((byte)129));
        else if (!hasTop) Horizontal16(ref tm, ref l);
        else
        {
            Vector128<short> low = Vector128.WidenLower(above).AsInt16(), high = Vector128.WidenUpper(above).AsInt16();
            for (int y = 0; y < 16; y++)
            {
                Vector128<short> delta = Vector128.Create((short)(Unsafe.Add(ref l, y + 1) - l));
                Simd.PackUnsignedSaturate(low + delta, high + delta).StoreUnsafe(ref tm, (nuint)(y * Bps));
            }
        }
    }

    public static void PredChroma8(Span<byte> output, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        if (!Simd.IsSupported128)
        {
            PredChroma8Scalar(output, left, top);
            return;
        }

        if (output.Length < 1296 + (7 * Bps) + 16 || (!left.IsEmpty && left.Length < 25) || (!top.IsEmpty && top.Length < 16))
            Vp8Dsp.ThrowBlockOutOfRange();
        ref byte dst = ref MemoryMarshal.GetReference(output);
        bool hasLeft = !left.IsEmpty, hasTop = !top.IsEmpty;
        ref byte l = ref hasLeft ? ref MemoryMarshal.GetReference(left) : ref dst;
        ref byte t = ref hasTop ? ref MemoryMarshal.GetReference(top) : ref dst;
        // U in columns 0-7 (left[0..8], top[0..7]), V in columns 8-15 (left[16..24], top[8..15]).
        for (int plane = 0; plane < 2; plane++)
        {
            ref byte pl = ref Unsafe.Add(ref l, 16 * plane);
            ulong above = hasTop ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref t, 8 * plane)) : 0;
            int dc = 128;
            if (hasTop || hasLeft)
            {
                int sum = (hasTop ? SumBytes(Vector128.CreateScalar(above).AsByte()) : 0)
                        + (hasLeft ? SumBytes(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pl, 1))).AsByte()) : 0);
                dc = hasTop && hasLeft ? (sum + 8) >> 4 : (sum + 4) >> 3;
            }

            ref byte block = ref Unsafe.Add(ref dst, 8 * plane);
            Fill8(ref Unsafe.Add(ref block, 1024), 0x0101010101010101UL * (byte)dc);
            Fill8(ref Unsafe.Add(ref block, 1280), hasTop ? above : 0x7F7F7F7F7F7F7F7FUL);
            if (hasLeft) Horizontal8(ref Unsafe.Add(ref block, 1296), ref pl);
            else Fill8(ref Unsafe.Add(ref block, 1296), 0x8181818181818181UL);

            ref byte tm = ref Unsafe.Add(ref block, 1040);
            if (!hasLeft) Fill8(ref tm, hasTop ? above : 0x8181818181818181UL);
            else if (!hasTop) Horizontal8(ref tm, ref pl);
            else
            {
                Vector128<short> top16 = Vector128.WidenLower(Vector128.CreateScalar(above).AsByte()).AsInt16();
                for (int y = 0; y < 8; y += 2)
                {
                    Vector128<short> delta0 = Vector128.Create((short)(Unsafe.Add(ref pl, y + 1) - pl));
                    Vector128<short> delta1 = Vector128.Create((short)(Unsafe.Add(ref pl, y + 2) - pl));
                    Vector128<ulong> rows = Simd.PackUnsignedSaturate(top16 + delta0, top16 + delta1).AsUInt64();
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref tm, y * Bps), rows.ToScalar());
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref tm, (y + 1) * Bps), rows.GetElement(1));
                }
            }
        }
    }

    // 4x4 predictions. boundary holds L K J I X A B C D E F G H (left column bottom-up, top-left, top and
    // top-right); every mode is a pattern of the 3-tap averages around each sample and the 2-tap averages
    // of neighbouring samples, so those are computed once and the rows are assembled from them.
    public static void PredLuma4(Span<byte> output, ReadOnlySpan<byte> boundary)
    {
        if (output.Length < I4Size || boundary.Length < 13) Vp8Dsp.ThrowBlockOutOfRange();
        ref byte edge = ref MemoryMarshal.GetReference(boundary);
        ulong edgeLow = Unsafe.ReadUnaligned<ulong>(ref edge);
        ulong edgeHigh = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref edge, 8)) | ((ulong)Unsafe.Add(ref edge, 12) << 32);
        ulong avg3Low, avg3High, avg2Low, avg2High;
        if (Simd.IsSupportedSsse3)
        {
            Averages4Simd(edgeLow, edgeHigh, out avg3Low, out avg3High, out avg2Low, out avg2High);
        }
        else
        {
            Averages4Scalar(ref edge, out avg3Low, out avg3High, out avg2Low, out avg2High);
        }

        ref byte dst = ref MemoryMarshal.GetReference(output);
        int L = edge, K = Unsafe.Add(ref edge, 1), J = Unsafe.Add(ref edge, 2), I = Unsafe.Add(ref edge, 3), X = Unsafe.Add(ref edge, 4);
        int A = Unsafe.Add(ref edge, 5), B = Unsafe.Add(ref edge, 6), C = Unsafe.Add(ref edge, 7), D = Unsafe.Add(ref edge, 8);

        // DC4
        Fill4(ref Unsafe.Add(ref dst, 1536), 0x01010101u * (uint)((4 + L + K + J + I + A + B + C + D) >> 3));

        // TM4: clip(left[y] + top[x] - top-left)
        Rows4(ref Unsafe.Add(ref dst, 1540), TrueMotion4(A, B, C, D, I - X), TrueMotion4(A, B, C, D, J - X),
            TrueMotion4(A, B, C, D, K - X), TrueMotion4(A, B, C, D, L - X));

        // VE4, HE4
        Fill4(ref Unsafe.Add(ref dst, 1544), Bytes4(avg3Low, avg3High, 5));
        Rows4(ref Unsafe.Add(ref dst, 1548), 0x01010101u * Byte(avg3Low, avg3High, 3), 0x01010101u * Byte(avg3Low, avg3High, 2),
            0x01010101u * Byte(avg3Low, avg3High, 1), 0x01010101u * Byte(avg3Low, avg3High, 0));

        // RD4, VR4, LD4, VL4
        Rows4(ref Unsafe.Add(ref dst, 1552), Bytes4(avg3Low, avg3High, 4), Bytes4(avg3Low, avg3High, 3),
            Bytes4(avg3Low, avg3High, 2), Bytes4(avg3Low, avg3High, 1));
        Rows4(ref Unsafe.Add(ref dst, 1556), Bytes4(avg2Low, avg2High, 4), Bytes4(avg3Low, avg3High, 4),
            Byte(avg3Low, avg3High, 3) | (Bytes4(avg2Low, avg2High, 4) << 8), Byte(avg3Low, avg3High, 2) | (Bytes4(avg3Low, avg3High, 4) << 8));
        Rows4(ref Unsafe.Add(ref dst, 1560), Bytes4(avg3Low, avg3High, 6), Bytes4(avg3Low, avg3High, 7),
            Bytes4(avg3Low, avg3High, 8), Bytes4(avg3Low, avg3High, 9));
        Rows4(ref Unsafe.Add(ref dst, 1564), Bytes4(avg2Low, avg2High, 5), Bytes4(avg3Low, avg3High, 6),
            (Bytes4(avg2Low, avg2High, 6) & 0xFFFFFF) | (Byte(avg3Low, avg3High, 10) << 24),
            (Bytes4(avg3Low, avg3High, 7) & 0xFFFFFF) | (Byte(avg3Low, avg3High, 11) << 24));

        // HD4, HU4
        Rows4(ref Unsafe.Add(ref dst, 1664),
            Byte(avg2Low, avg2High, 3) | (Bytes4(avg3Low, avg3High, 4) << 8),
            Interleave(Byte(avg2Low, avg2High, 2), Byte(avg3Low, avg3High, 3), Byte(avg2Low, avg2High, 3), Byte(avg3Low, avg3High, 4)),
            Interleave(Byte(avg2Low, avg2High, 1), Byte(avg3Low, avg3High, 2), Byte(avg2Low, avg2High, 2), Byte(avg3Low, avg3High, 3)),
            Interleave(Byte(avg2Low, avg2High, 0), Byte(avg3Low, avg3High, 1), Byte(avg2Low, avg2High, 1), Byte(avg3Low, avg3High, 2)));
        Rows4(ref Unsafe.Add(ref dst, 1668),
            Interleave(Byte(avg2Low, avg2High, 2), Byte(avg3Low, avg3High, 2), Byte(avg2Low, avg2High, 1), Byte(avg3Low, avg3High, 1)),
            Interleave(Byte(avg2Low, avg2High, 1), Byte(avg3Low, avg3High, 1), Byte(avg2Low, avg2High, 0), Byte(avg3Low, avg3High, 0)),
            Interleave(Byte(avg2Low, avg2High, 0), Byte(avg3Low, avg3High, 0), (uint)L, (uint)L),
            0x01010101u * (uint)L);
    }

    // avg3[c] = AVG3(e[c - 1], e[c], e[c + 1]) with e[-1] = e[0] and e[13] = e[12]; avg2[c] = AVG2(e[c], e[c + 1]).
    // AVG3 uses (a + 2b + c + 2) >> 2 = (((a + c) >> 1) + b + 1) >> 1, with (a + c) >> 1 = avg(a, c) - ((a ^ c) & 1).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Averages4Simd(ulong edgeLow, ulong edgeHigh, out ulong avg3Low, out ulong avg3High, out ulong avg2Low, out ulong avg2High)
    {
        Vector128<byte> e = Vector128.Create(edgeLow, edgeHigh).AsByte();
        Vector128<byte> previous = Simd.ShuffleBytes(e, Vector128.Create((byte)0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14));
        Vector128<byte> next = Simd.ShuffleBytes(e, Vector128.Create((byte)1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 12, 13, 14, 15));
        Vector128<byte> outer = Simd.AverageRounded(previous, next) - ((previous ^ next) & Vector128<byte>.One);
        Vector128<ulong> avg3 = Simd.AverageRounded(outer, e).AsUInt64();
        Vector128<ulong> avg2 = Simd.AverageRounded(e, Simd.ShiftRightBytes(e, 1)).AsUInt64();
        avg3Low = avg3.ToScalar();
        avg3High = avg3.GetElement(1);
        avg2Low = avg2.ToScalar();
        avg2High = avg2.GetElement(1);
    }

    private static void Averages4Scalar(ref byte edge, out ulong avg3Low, out ulong avg3High, out ulong avg2Low, out ulong avg2High)
    {
        avg3Low = avg3High = avg2Low = avg2High = 0;
        for (int c = 0; c < 13; c++)
        {
            int previous = Unsafe.Add(ref edge, Math.Max(c - 1, 0)), current = Unsafe.Add(ref edge, c), next = Unsafe.Add(ref edge, Math.Min(c + 1, 12));
            ulong avg3 = Avg3(previous, current, next), avg2 = Avg2(current, next);
            if (c < 8)
            {
                avg3Low |= avg3 << (8 * c);
                avg2Low |= avg2 << (8 * c);
            }
            else
            {
                avg3High |= avg3 << (8 * (c - 8));
                avg2High |= avg2 << (8 * (c - 8));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint TrueMotion4(int a, int b, int c, int d, int delta) =>
        Interleave(Clip8(a + delta), Clip8(b + delta), Clip8(c + delta), Clip8(d + delta));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Clip8(int v) => (uint)v <= 255 ? (uint)v : v < 0 ? 0u : 255u;

    // Byte 'index' and the four bytes from 'index' of the 16-byte value (low, high).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Byte(ulong low, ulong high, int index) => (uint)((index < 8 ? low >> (8 * index) : high >> (8 * (index - 8))) & 0xFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Bytes4(ulong low, ulong high, int index) =>
        index <= 4 ? (uint)(low >> (8 * index)) : index >= 8 ? (uint)(high >> (8 * (index - 8))) : (uint)((low >> (8 * index)) | (high << (64 - (8 * index))));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Interleave(uint b0, uint b1, uint b2, uint b3) => b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Rows4(ref byte dst, uint row0, uint row1, uint row2, uint row3)
    {
        Unsafe.WriteUnaligned(ref dst, row0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Bps), row1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2 * Bps), row2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 3 * Bps), row3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill4(ref byte dst, uint value) => Rows4(ref dst, value, value, value, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SumBytes(Vector128<byte> value)
    {
        Vector128<ushort> sums = Simd.SumAbsoluteDifferences(value, Vector128<byte>.Zero);
        return sums.ToScalar() + sums.GetElement(4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill16(ref byte dst, Vector128<byte> value)
    {
        for (int y = 0; y < 16; y++) value.StoreUnsafe(ref dst, (nuint)(y * Bps));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Horizontal16(ref byte dst, ref byte left)
    {
        for (int y = 0; y < 16; y++) Vector128.Create(Unsafe.Add(ref left, y + 1)).StoreUnsafe(ref dst, (nuint)(y * Bps));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill8(ref byte dst, ulong value)
    {
        for (int y = 0; y < 8; y++) Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, y * Bps), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Horizontal8(ref byte dst, ref byte left)
    {
        for (int y = 0; y < 8; y++) Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, y * Bps), 0x0101010101010101UL * Unsafe.Add(ref left, y + 1));
    }
}
