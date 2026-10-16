using System.Runtime.Intrinsics;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Tests.Core;

/// <summary>Accuracy of the deterministic exponentials and bit equality of their vector forms with the scalar ones.</summary>
public class DeterministicMathSimdTests
{
    private static float[] Inputs()
    {
        var rng = new Random(70);
        var values = new List<float>
        {
            0f, -0f, 1f, -1f, 0.5f, 2f, 127.99f, 128f, 1000f, -126f, -126.01f, -127f, -1000f, 1.17549435e-38f, 1e-38f,
            float.Epsilon, float.MaxValue, float.PositiveInfinity, float.NegativeInfinity, float.NaN, 1.4142135f, 1.4142137f,
        };
        for (int i = 0; i < 4000; i++)
        {
            values.Add((float)((rng.NextDouble() * 300) - 150));
            values.Add((float)rng.NextDouble());
            values.Add(float.Exp2((float)((rng.NextDouble() * 250) - 125)));
        }

        return [.. values];
    }

    [Test]
    public async Task Exp2_IsWithinTwoUlp()
    {
        int worst = 0;
        for (double x = -125.9; x < 127.9; x += 0.000731)
        {
            float actual = DeterministicMath.Exp2((float)x);
            float expected = (float)Math.Pow(2, (float)x);
            worst = Math.Max(worst, Math.Abs(BitConverter.SingleToInt32Bits(actual) - BitConverter.SingleToInt32Bits(expected)));
        }

        await Assert.That(worst).IsLessThanOrEqualTo(2);
        await Assert.That(DeterministicMath.Exp2(0f)).IsEqualTo(1f);
        await Assert.That(DeterministicMath.Exp2(3f)).IsEqualTo(8f);
        await Assert.That(DeterministicMath.Exp2(-2f)).IsEqualTo(0.25f);
        await Assert.That(DeterministicMath.Exp2(128f)).IsEqualTo(float.PositiveInfinity);
        await Assert.That(DeterministicMath.Exp2(-126.5f)).IsEqualTo(0f);
        await Assert.That(float.IsNaN(DeterministicMath.Exp2(float.NaN))).IsTrue();
    }

    [Test]
    public async Task Log2_IsAccurate()
    {
        double worst = 0;
        for (double l = -125.9; l < 127.9; l += 0.000731)
        {
            float x = (float)Math.Pow(2, l);
            double expected = Math.Log2(x);
            worst = Math.Max(worst, Math.Abs(DeterministicMath.Log2(x) - expected) / Math.Max(1, Math.Abs(expected)));
        }

        await Assert.That(worst).IsLessThanOrEqualTo(Math.Pow(2, -23));
        await Assert.That(DeterministicMath.Log2(1f)).IsEqualTo(0f);
        await Assert.That(DeterministicMath.Log2(8f)).IsEqualTo(3f);
    }

    [Test]
    public async Task Pow_IsAccurateForGainMapRanges()
    {
        double worst = 0;
        foreach (float y in new[] { 1 / 0.82f, 1 / 0.85f, 1 / 0.9f, 1 / 0.45f, 1 / 1.3f, 2.5f })
        {
            for (double x = 1e-6; x <= 1; x *= 1.0007)
            {
                if (Math.Abs(y * Math.Log2(x)) > 24)
                {
                    continue;
                }

                double expected = Math.Pow((float)x, y);
                worst = Math.Max(worst, Math.Abs(DeterministicMath.Pow((float)x, y) - expected) / expected);
            }

            await Assert.That(DeterministicMath.Pow(0f, y)).IsEqualTo(0f);
            await Assert.That(DeterministicMath.Pow(1f, y)).IsEqualTo(1f);
        }

        await Assert.That(worst).IsLessThanOrEqualTo(2e-6);
    }

    [Test]
    public async Task Vector128_MatchesScalar()
    {
        float[] x = Inputs();
        float[] y = [.. x.Select((v, i) => (i % 7) switch { 0 => 1 / 0.82f, 1 => 2.2222223f, 2 => 1f, _ => 0.5f + (i % 5) })];
        var scalar = new int[x.Length * 3];
        var simd = new int[x.Length * 3];
        for (int i = 0; i < x.Length; i++)
        {
            scalar[i * 3] = BitConverter.SingleToInt32Bits(DeterministicMath.Exp2(x[i]));
            scalar[(i * 3) + 1] = BitConverter.SingleToInt32Bits(DeterministicMath.Log2(x[i]));
            scalar[(i * 3) + 2] = BitConverter.SingleToInt32Bits(DeterministicMath.Pow(x[i], y[i]));
        }

        for (int i = 0; i + 4 <= x.Length; i += 4)
        {
            var vx = Vector128.Create(x, i);
            var vy = Vector128.Create(y, i);
            (Vector128<float> e, Vector128<float> l, Vector128<float> p) = (DeterministicMath.Exp2(vx), DeterministicMath.Log2(vx), DeterministicMath.Pow(vx, vy));
            for (int k = 0; k < 4; k++)
            {
                simd[(i + k) * 3] = BitConverter.SingleToInt32Bits(e[k]);
                simd[((i + k) * 3) + 1] = BitConverter.SingleToInt32Bits(l[k]);
                simd[((i + k) * 3) + 2] = BitConverter.SingleToInt32Bits(p[k]);
            }
        }

        await AssertSameUpTo(simd, scalar, x.Length & ~3);
    }

    [Test]
    public async Task Vector256_MatchesScalar()
    {
        float[] x = Inputs();
        float[] y = [.. x.Select((v, i) => (i % 3) switch { 0 => 1 / 0.9f, 1 => 2.2222223f, _ => 0.25f + (i % 9) })];
        var scalar = new int[x.Length * 3];
        var simd = new int[x.Length * 3];
        for (int i = 0; i < x.Length; i++)
        {
            scalar[i * 3] = BitConverter.SingleToInt32Bits(DeterministicMath.Exp2(x[i]));
            scalar[(i * 3) + 1] = BitConverter.SingleToInt32Bits(DeterministicMath.Log2(x[i]));
            scalar[(i * 3) + 2] = BitConverter.SingleToInt32Bits(DeterministicMath.Pow(x[i], y[i]));
        }

        for (int i = 0; i + 8 <= x.Length; i += 8)
        {
            var vx = Vector256.Create(x, i);
            var vy = Vector256.Create(y, i);
            (Vector256<float> e, Vector256<float> l, Vector256<float> p) = (DeterministicMath.Exp2(vx), DeterministicMath.Log2(vx), DeterministicMath.Pow(vx, vy));
            for (int k = 0; k < 8; k++)
            {
                simd[(i + k) * 3] = BitConverter.SingleToInt32Bits(e[k]);
                simd[((i + k) * 3) + 1] = BitConverter.SingleToInt32Bits(l[k]);
                simd[((i + k) * 3) + 2] = BitConverter.SingleToInt32Bits(p[k]);
            }
        }

        await AssertSameUpTo(simd, scalar, x.Length & ~7);
    }

    // NaN payloads may differ between paths; any NaN matches any NaN.
    private static async Task AssertSameUpTo(int[] simd, int[] scalar, int count)
    {
        static int Canonical(int bits) => float.IsNaN(BitConverter.Int32BitsToSingle(bits)) ? 0x7FC00000 : bits;
        int[] a = [.. simd.Take(count * 3).Select(Canonical)];
        int[] b = [.. scalar.Take(count * 3).Select(Canonical)];
        await Assert.That(a).IsEquivalentTo(b, CollectionOrdering.Matching);
    }
}
