using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Algorithms.Processing;

// Vector form of DrawOver and Over: the same integer arithmetic in 32-bit lanes. The alpha map entry is recomputed with
// the float operations of FillAlphaMap, and the coefficient division is a float estimate corrected to the exact quotient.
// Every float converted to int is finite and in range, so ConvertToInt32Native truncates like the scalar casts.
internal static partial class Compositor
{
    // 255 · 255 · 2^PrecisionBits, the factor of the source alpha in the coef1 numerator.
    private const int CoefficientScale = 255 * 255 * (1 << PrecisionBits);

    /// <summary>Draws whole groups of eight (AVX2) and four pixels of a row over the destination; returns the pixels processed.</summary>
    private static int OverRowSimd(Span<Rgba32> dstRow, ReadOnlySpan<Rgba32> srcRow, float opacity)
    {
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian)
        {
            return 0;
        }

        int done = Avx2.IsSupported ? OverRowAvx2(dstRow, srcRow, opacity) : 0;
        return OverRowVector128(dstRow, srcRow, opacity, done);
    }

    private static int OverRowVector128(Span<Rgba32> dstRow, ReadOnlySpan<Rgba32> srcRow, float opacity, int start)
    {
        ref uint d = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(dstRow));
        ref uint s = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(srcRow));
        Vector128<float> scale = Vector128.Create(opacity);
        Vector128<uint> bytes = Vector128.Create(0xFFu);
        int count = start + ((dstRow.Length - start) & ~3);
        for (int i = start; i < count; i += 4)
        {
            Vector128<uint> sp = Vector128.LoadUnsafe(ref s, (nuint)i);
            Vector128<uint> sa = Vector128.ConvertToInt32Native((Vector128.ConvertToSingle((sp >> 24).AsInt32()) * scale) + Vector128.Create(0.5f)).AsUInt32();
            if (sa == Vector128<uint>.Zero)
            {
                continue;
            }

            Vector128<uint> dp = Vector128.LoadUnsafe(ref d, (nuint)i);
            Vector128<uint> blend = (dp >> 24) * (Vector128.Create(255u) - sa);
            Vector128<uint> outa255 = (sa << 8) - sa + blend;
            Vector128<uint> coef1 = Coefficient(sa, outa255);
            Vector128<uint> r = Mix(sp & bytes, dp & bytes, coef1);
            Vector128<uint> g = Mix((sp >> 8) & bytes, (dp >> 8) & bytes, coef1);
            Vector128<uint> b = Mix((sp >> 16) & bytes, (dp >> 16) & bytes, coef1);

            // Every result is at most 255, so the byte casts of Over are no-ops here.
            Vector128<uint> rounding = Vector128.Create(0x80u << PrecisionBits);
            Vector128<uint> blended = (ShiftForDiv255(r + rounding) >> PrecisionBits)
                | ((ShiftForDiv255(g + rounding) >> PrecisionBits) << 8)
                | ((ShiftForDiv255(b + rounding) >> PrecisionBits) << 16)
                | (ShiftForDiv255(outa255 + Vector128.Create(0x80u)) << 24);
            Vector128.ConditionalSelect(Vector128.Equals(sa, Vector128<uint>.Zero), dp, blended).StoreUnsafe(ref d, (nuint)i);
        }

        return count;
    }

    // coef1 = sa · 255 · 255 · 2^7 / outa255 in integers. The float quotient is below 2^15 with a relative error below
    // 2^-22, so its truncation is off by at most one; the remainder check corrects it. Lanes with outa255 = 0 have
    // sa = 0 and are not stored.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Coefficient(Vector128<uint> sa, Vector128<uint> outa255)
    {
        Vector128<int> numerator = (sa * Vector128.Create((uint)CoefficientScale)).AsInt32();
        Vector128<int> divisor = Vector128.Max(outa255.AsInt32(), Vector128<int>.One);
        Vector128<int> q = Vector128.ConvertToInt32Native(Vector128.ConvertToSingle(numerator) / Vector128.ConvertToSingle(divisor));
        Vector128<int> remainder = numerator - (q * divisor);
        q = q - Vector128.GreaterThanOrEqual(remainder, divisor) + Vector128.LessThan(remainder, Vector128<int>.Zero);
        return q.AsUInt32();
    }

    // src · coef1 + dst · coef2 with coef2 = 255 · 2^7 − coef1, as dst · 255 · 2^7 + (src − dst) · coef1: one multiply
    // instead of two. The exact value fits in 32 bits, so the wrapping of the unsigned terms cancels out.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Mix(Vector128<uint> source, Vector128<uint> backdrop, Vector128<uint> coef1) =>
        (backdrop << (PrecisionBits + 8)) - (backdrop << PrecisionBits) + ((source - backdrop) * coef1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ShiftForDiv255(Vector128<uint> a) => ((a >> 8) + a) >> 8;
}
