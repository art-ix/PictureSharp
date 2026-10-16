using System.Runtime.CompilerServices;

namespace PictureSharp.Primitives.Numerics;

/// <summary>
/// <c>float</c> exponentials and logarithms built only from IEEE additions, multiplications and divisions in a fixed
/// order, without fused multiply-add. Unlike <see cref="MathF"/>, which calls the platform's C runtime, the results
/// are identical on every architecture and between these scalar methods and their vector forms in
/// <c>DeterministicMath.Simd.cs</c>. Exp2 is within 2 ulp, Log2 within 2^-23 · max(1, |result|).
/// </summary>
internal static partial class DeterministicMath
{
    private const float Sqrt2 = 1.4142135f;
    private const float SmallestNormal = 1.17549435e-38f;

    // Least-squares fit of 2^f at Chebyshev nodes on [-0.5, 0.5].
    private const float E1 = 0.6931471824645996f, E2 = 0.24022650718688965f, E3 = 0.05550327152013779f;
    private const float E4 = 0.00961802527308464f, E5 = 0.0013400432653725147f, E6 = 0.00015469732170458883f;

    // log2(m) = t · P(t²) with t = (m − 1) / (m + 1), |t| ≤ 0.1716 (fit of the atanh series).
    private const float L0 = 2.885390043258667f, L1 = 0.9617967009544373f, L2 = 0.5770835876464844f;
    private const float L3 = 0.4116728603839874f, L4 = 0.340727835893631f;

    /// <summary>2^<paramref name="x"/>: +∞ from 128, 0 below −126, NaN for NaN.</summary>
    public static float Exp2(float x)
    {
        if (x >= 128f)
        {
            return float.PositiveInfinity;
        }

        if (x < -126f)
        {
            return 0f;
        }

        float n = MathF.Round(x);
        float f = x - n;
        float p = ExpPolynomial(f);
        // 2^n in two factors: n rounds up to 128 just below x = 128, beyond the exponent of a single float.
        int half = (int)n >> 1;
        return p * BitConverter.Int32BitsToSingle((half + 127) << 23) * BitConverter.Int32BitsToSingle(((int)n - half + 127) << 23);
    }

    /// <summary>log2 of a positive normal <paramref name="x"/>; other inputs give unspecified results.</summary>
    public static float Log2(float x)
    {
        int bits = BitConverter.SingleToInt32Bits(x);
        int e = (bits >> 23) - 127;
        float m = BitConverter.Int32BitsToSingle((bits & 0x7FFFFF) | 0x3F800000);
        if (m > Sqrt2)
        {
            m *= 0.5f;
            e++;
        }

        float t = (m - 1f) / (m + 1f);
        return e + (t * LogPolynomial(t * t));
    }

    /// <summary>
    /// <paramref name="x"/>^<paramref name="y"/> for <paramref name="x"/> ≥ 0 and <paramref name="y"/> &gt; 0, as
    /// Exp2(y · Log2(x)); inputs below the smallest normal give 0. Relative error ≤ 2e-6 while |y · log2 x| ≤ 24.
    /// </summary>
    public static float Pow(float x, float y) => x < SmallestNormal ? 0f : Exp2(y * Log2(x));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ExpPolynomial(float f) => (((((((((((E6 * f) + E5) * f) + E4) * f) + E3) * f) + E2) * f) + E1) * f) + 1f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LogPolynomial(float t2) => (((((((L4 * t2) + L3) * t2) + L2) * t2) + L1) * t2) + L0;
}
