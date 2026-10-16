using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Algorithms.Processing;

// 256-bit form of Compositor.Simd.cs for AVX2: the same lane-wise integer operations on eight pixels.
internal static partial class Compositor
{
    /// <summary>Draws whole groups of eight pixels of a row over the destination with AVX2; returns the pixels processed.</summary>
    private static int OverRowAvx2(Span<Rgba32> dstRow, ReadOnlySpan<Rgba32> srcRow, float opacity)
    {
        ref uint d = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(dstRow));
        ref uint s = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(srcRow));
        Vector256<float> scale = Vector256.Create(opacity);
        Vector256<uint> bytes = Vector256.Create(0xFFu);
        int count = dstRow.Length & ~7;
        for (int i = 0; i < count; i += 8)
        {
            Vector256<uint> sp = Vector256.LoadUnsafe(ref s, (nuint)i);
            Vector256<uint> sa = Vector256.ConvertToInt32Native((Vector256.ConvertToSingle((sp >> 24).AsInt32()) * scale) + Vector256.Create(0.5f)).AsUInt32();
            if (sa == Vector256<uint>.Zero)
            {
                continue;
            }

            Vector256<uint> dp = Vector256.LoadUnsafe(ref d, (nuint)i);
            Vector256<uint> blend = (dp >> 24) * (Vector256.Create(255u) - sa);
            Vector256<uint> outa255 = (sa << 8) - sa + blend;
            Vector256<uint> coef1 = Coefficient(sa, outa255);
            Vector256<uint> r = Mix(sp & bytes, dp & bytes, coef1);
            Vector256<uint> g = Mix((sp >> 8) & bytes, (dp >> 8) & bytes, coef1);
            Vector256<uint> b = Mix((sp >> 16) & bytes, (dp >> 16) & bytes, coef1);

            // Every result is at most 255, so the byte casts of Over are no-ops here.
            Vector256<uint> rounding = Vector256.Create(0x80u << PrecisionBits);
            Vector256<uint> blended = (ShiftForDiv255(r + rounding) >> PrecisionBits)
                | ((ShiftForDiv255(g + rounding) >> PrecisionBits) << 8)
                | ((ShiftForDiv255(b + rounding) >> PrecisionBits) << 16)
                | (ShiftForDiv255(outa255 + Vector256.Create(0x80u)) << 24);
            Vector256.ConditionalSelect(Vector256.Equals(sa, Vector256<uint>.Zero), dp, blended).StoreUnsafe(ref d, (nuint)i);
        }

        return count;
    }

    // coef1 = sa · 255 · 255 · 2^7 / outa255 in integers. The float quotient is below 2^15 with a relative error below
    // 2^-22, so its truncation is off by at most one; the remainder check corrects it. Lanes with outa255 = 0 have
    // sa = 0 and are not stored.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Coefficient(Vector256<uint> sa, Vector256<uint> outa255)
    {
        Vector256<int> numerator = (sa * Vector256.Create((uint)CoefficientScale)).AsInt32();
        Vector256<int> divisor = Vector256.Max(outa255.AsInt32(), Vector256<int>.One);
        Vector256<int> q = Vector256.ConvertToInt32Native(Vector256.ConvertToSingle(numerator) / Vector256.ConvertToSingle(divisor));
        Vector256<int> remainder = numerator - (q * divisor);
        q = q - Vector256.GreaterThanOrEqual(remainder, divisor) + Vector256.LessThan(remainder, Vector256<int>.Zero);
        return q.AsUInt32();
    }

    // src · coef1 + dst · coef2 with coef2 = 255 · 2^7 − coef1, as dst · 255 · 2^7 + (src − dst) · coef1: one multiply
    // instead of two. The exact value fits in 32 bits, so the wrapping of the unsigned terms cancels out.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Mix(Vector256<uint> source, Vector256<uint> backdrop, Vector256<uint> coef1) =>
        (backdrop << (PrecisionBits + 8)) - (backdrop << PrecisionBits) + ((source - backdrop) * coef1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> ShiftForDiv255(Vector256<uint> a) => ((a >> 8) + a) >> 8;
}
