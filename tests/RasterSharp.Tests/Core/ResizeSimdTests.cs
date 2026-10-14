using System.Runtime.InteropServices;
using RasterSharp.Processing;

namespace RasterSharp.Tests.Core;

/// <summary>Vector resampling and premultiplication equal the scalar code (integer arithmetic, so exactly).</summary>
public class ResizeSimdTests
{
    // Pillow-like coefficients: PrecisionBits = 22, negative lobes, sums near 1 << 22.
    private static (int Ksize, int[] Bounds, int[] Kk) Coefficients(Random rng, int inSize, int outSize, int ksize)
    {
        var bounds = new int[outSize * 2];
        var kk = new int[outSize * ksize];
        for (int i = 0; i < outSize; i++)
        {
            int count = rng.Next(1, Math.Min(ksize, inSize) + 1);
            int start = rng.Next(0, inSize - count + 1);
            bounds[i * 2] = start;
            bounds[(i * 2) + 1] = count;
            for (int t = 0; t < count; t++)
            {
                kk[(i * ksize) + t] = rng.Next(-(1 << 20), 1 << 22);
            }
        }

        return (ksize, bounds, kk);
    }

    [Fact]
    public void HorizontalRow_MatchesScalar()
    {
        var rng = new Random(50);
        foreach (int inWidth in new[] { 1, 3, 17, 200 })
        foreach (int outWidth in new[] { 1, 5, 64, 333 })
        foreach (int ksize in new[] { 1, 3, 7, 19 })
        {
            var row = new byte[inWidth * 4];
            rng.NextBytes(row);
            (_, int[] bounds, int[] kk) = Coefficients(rng, inWidth, outWidth, ksize);
            var scalar = new Rgba32[outWidth];
            var simd = new Rgba32[outWidth];
            Resizer.HorizontalRowScalar(row, scalar, ksize, bounds, kk, 0);
            int done = Resizer.HorizontalRowSimd(row, simd, ksize, bounds, kk);
            Resizer.HorizontalRowScalar(row, simd, ksize, bounds, kk, done);
            Assert.Equal(scalar, simd);
        }
    }

    [Fact]
    public void VerticalRow_MatchesScalar()
    {
        var rng = new Random(51);
        foreach (int width in new[] { 1, 3, 4, 5, 8, 33, 250 })
        foreach (int taps in new[] { 1, 2, 5, 13 })
        {
            int stride = width * 4;
            var rows = new byte[stride * taps];
            rng.NextBytes(rows);
            int[] k = Enumerable.Range(0, taps).Select(_ => rng.Next(-(1 << 21), 1 << 22)).ToArray();
            var scalar = new byte[stride];
            var simd = new byte[stride];
            Resizer.VerticalRowScalar(rows, stride, k, scalar, 0);
            int done = Resizer.VerticalRowSimd(rows, stride, k, simd);
            Resizer.VerticalRowScalar(rows, stride, k, simd, done);
            Assert.Equal(scalar, simd);
        }
    }

    [Fact]
    public void Premultiply_MatchesScalar()
    {
        var rng = new Random(52);
        foreach (int length in new[] { 0, 1, 3, 4, 7, 64, 1001 })
        {
            var pixels = new Rgba32[length];
            rng.NextBytes(MemoryMarshal.AsBytes(pixels.AsSpan()));
            for (int i = 0; i < length; i += 3)
            {
                pixels[i].A = (byte)(i % 2 == 0 ? 255 : 0);
            }

            var scalar = new Rgba32[length];
            var simd = new Rgba32[length];
            Premultiplied.FromStraightScalar(pixels, scalar);
            int done = Premultiplied.FromStraightSimd(pixels, simd);
            Premultiplied.FromStraightScalar(pixels.AsSpan(done), simd.AsSpan(done));
            Assert.Equal(scalar, simd);

            // Every alpha and channel value against the definition.
            Assert.Equal(pixels.All(p => p.A == 255), Premultiplied.IsOpaque(pixels));
        }

        var all = new Rgba32[256 * 256];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = new Rgba32((byte)i, (byte)(255 - (i & 0xff)), (byte)(i >> 3), (byte)(i >> 8));
        }

        var expected = new Rgba32[all.Length];
        var actual = new Rgba32[all.Length];
        Premultiplied.FromStraightScalar(all, expected);
        Premultiplied.FromStraight(all, actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ToStraight_MatchesDefinition()
    {
        var rng = new Random(53);
        var pixels = new Rgba32[5000];
        rng.NextBytes(MemoryMarshal.AsBytes(pixels.AsSpan()));
        for (int i = 0; i < pixels.Length; i++)
        {
            // Long opaque and transparent runs between semi-transparent pixels.
            if (i % 700 < 500)
            {
                pixels[i].A = (byte)(i % 1400 < 700 ? 255 : 0);
            }
        }

        Rgba32[] expected = pixels.Select(p => p.A is 0 or 255 ? p : new Rgba32(
            (byte)Math.Min(255, 255 * p.R / p.A), (byte)Math.Min(255, 255 * p.G / p.A), (byte)Math.Min(255, 255 * p.B / p.A), p.A)).ToArray();
        Premultiplied.ToStraight(pixels);
        Assert.Equal(expected, pixels);
    }
}
