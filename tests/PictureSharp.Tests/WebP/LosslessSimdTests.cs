using PictureSharp.Infrastructure.Codecs.Png;
using PictureSharp.Infrastructure.Codecs.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Alpha;
using PictureSharp.Infrastructure.Codecs.WebP.Lossless;
using PictureSharp.Primitives.Numerics;
using PictureSharp.Processing;

namespace PictureSharp.Tests.WebP;

/// <summary>SIMD paths of the lossless transforms and scanline filters must match the scalar code on every input.</summary>
public class LosslessSimdTests
{
    [Test]
    public async Task AddGreen_MatchesScalar() =>
        await AssertPixels(static (src, simd) =>
        {
            LosslessTransforms.AddGreenToBlueAndRedScalar(src);
            LosslessTransforms.AddGreenToBlueAndRed(simd);
        });

    [Test]
    public async Task SubtractGreen_MatchesScalar() =>
        await AssertPixels(static (src, simd) =>
        {
            LosslessTransforms.SubtractGreenFromBlueAndRedScalar(src);
            LosslessTransforms.SubtractGreenFromBlueAndRed(simd);
        });

    [Test]
    public async Task TransformColor_MatchesScalar()
    {
        var rng = new Random(3);
        foreach (sbyte greenToRed in Sweep.ToArray())
            foreach (sbyte greenToBlue in Anchors.ToArray())
                foreach (sbyte redToBlue in Anchors.ToArray())
                {
                    uint[] scalar = RandomPixels(rng, 24);
                    uint[] simd = (uint[])scalar.Clone();
                    LosslessTransforms.TransformColorScalar(greenToRed, greenToBlue, redToBlue, scalar);
                    LosslessTransforms.TransformColor(greenToRed, greenToBlue, redToBlue, simd);
                    await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
                }
    }

    [Test]
    public async Task TransformColorInverse_MatchesScalar()
    {
        var rng = new Random(4);
        foreach (sbyte greenToRed in Sweep.ToArray())
            foreach (sbyte greenToBlue in Anchors.ToArray())
                foreach (sbyte redToBlue in Anchors.ToArray())
                {
                    uint[] scalar = RandomPixels(rng, 24);
                    uint[] simd = (uint[])scalar.Clone();
                    LosslessTransforms.TransformColorInverseScalar(greenToRed, greenToBlue, redToBlue, scalar);
                    LosslessTransforms.TransformColorInverse(greenToRed, greenToBlue, redToBlue, simd);
                    await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
                }
    }

    [Test]
    public async Task PredictorInverse_MatchesScalar()
    {
        var rng = new Random(5);
        int[] widths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 511, 512, 513];
        int[] heights = [1, 2, 3, 8];
        foreach (int width in widths)
            foreach (int height in heights)
                foreach (int bits in Enumerable.Range(0, 10))
                {
                    for (int mode = 0; mode < 16; mode++)
                    {
                        await AssertPredictor(rng, width, height, bits, mode);
                    }

                    await AssertPredictor(rng, width, height, bits, mode: -1);
                }

        await AssertPredictor(rng, 320, 6, 3, mode: -1);
        foreach (int bits in Enumerable.Range(2, 8))
        {
            await AssertPredictor(rng, 67, (1 << bits) + 1, bits, mode: -1);
        }
    }

    [Test]
    [Arguments(5, 3, 0, 1, 2, 255, 0)]
    [Arguments(6, 3, 2, 0, 0, 255, 1)]
    [Arguments(7, 3, 0, 2, 0, 255, 1)]
    [Arguments(13, 0, 3, 2, 0, 0, 0)] // Negative even difference.
    [Arguments(13, 0, 2, 2, 0, 0, 1)] // Negative odd difference truncates toward zero.
    [Arguments(13, 254, 0, 255, 0, 1, 0)] // Saturation precedes wrapping addition.
    [Arguments(13, 0, 255, 0, 0, 255, 255)]
    [Arguments(10, 3, 0, 1, 2, 255, 0)] // Nested odd averages round down, then residual wraps.
    [Arguments(11, 255, 128, 1, 0, 0, 1)] // Equal distances choose top, although top differs from left.
    [Arguments(11, 200, 0, 10, 0, 0, 200)]
    [Arguments(11, 10, 0, 200, 0, 0, 200)]
    [Arguments(12, 250, 0, 20, 0, 1, 0)] // Clamp to 255 before adding the residual modulo 256.
    [Arguments(12, 0, 255, 0, 0, 255, 255)]
    [Arguments(12, 100, 50, 80, 0, 200, 74)]
    public async Task PredictorInverse_DirectedArithmetic(int mode, byte left, byte topLeft, byte top, byte topRight, byte residual, byte expected)
    {
        const int width = 33;
        foreach (int bits in new[] { 2, 3, 5 })
            foreach (int x in new[] { 1, 4, 5, 8, 9, 16, 17, 31, 32 })
            {
                uint[] desired = new uint[width * 2];
                Array.Fill(desired, 0x12345678u);
                desired[x - 1] = Repeat(topLeft);
                desired[x] = Repeat(top);
                // At the right edge top-right wraps to the reconstructed first pixel of this row.
                desired[x == width - 1 ? width : x + 1] = Repeat(topRight);
                desired[width + x - 1] = Repeat(left);
                desired[width + x] = Repeat(expected);
                uint[] encoded = new uint[desired.Length];
                encoded[0] = LosslessTransforms.SubPixels(desired[0], WebPConstants.ArgbBlack);
                for (int i = 1; i < width; i++)
                {
                    encoded[i] = LosslessTransforms.SubPixels(desired[i], desired[i - 1]);
                }

                encoded[width] = LosslessTransforms.SubPixels(desired[width], desired[0]);
                for (int i = width + 1; i < desired.Length; i++)
                {
                    encoded[i] = LosslessTransforms.SubPixels(desired[i], LosslessTransforms.Predict(mode, desired, i, width));
                }

                await Assert.That(encoded[width + x]).IsEqualTo(Repeat(residual));
                uint[] modes = new uint[Vp8LDecoder.SubSampleSize(width, bits) * Vp8LDecoder.SubSampleSize(2, bits)];
                Array.Fill(modes, WebPConstants.ArgbBlack | (uint)(mode << 8));
                var transform = new Vp8LTransform { XSize = width, YSize = 2, Bits = bits, Data = modes };
                LosslessTransforms.PredictorInverseTransform(transform, encoded);
                await Assert.That(encoded).IsEquivalentTo(desired, CollectionOrdering.Matching);
            }

        static uint Repeat(byte value) => value * 0x01010101u;
    }

    [Test]
    public async Task ColorTransforms_ShortLengthsMatchScalar()
    {
        var rng = new Random(11);
        foreach (int length in new[] { 0, 1, 3, 4, 5, 6, 7, 9, 12, 15, 17, 31 })
            foreach (sbyte greenToRed in Anchors.ToArray())
                foreach (sbyte greenToBlue in Anchors.ToArray())
                    foreach (sbyte redToBlue in Anchors.ToArray())
                    {
                        uint[] pixels = RandomPixels(rng, length);
                        uint[] forwardScalar = (uint[])pixels.Clone(), forwardSimd = (uint[])pixels.Clone();
                        LosslessTransforms.TransformColorScalar(greenToRed, greenToBlue, redToBlue, forwardScalar);
                        LosslessTransforms.TransformColor(greenToRed, greenToBlue, redToBlue, forwardSimd);
                        await Assert.That(forwardSimd).IsEquivalentTo(forwardScalar, CollectionOrdering.Matching);
                        uint[] inverseScalar = (uint[])pixels.Clone(), inverseSimd = (uint[])pixels.Clone();
                        LosslessTransforms.TransformColorInverseScalar(greenToRed, greenToBlue, redToBlue, inverseScalar);
                        LosslessTransforms.TransformColorInverse(greenToRed, greenToBlue, redToBlue, inverseSimd);
                        await Assert.That(inverseSimd).IsEquivalentTo(inverseScalar, CollectionOrdering.Matching);
                    }
    }

    [Test]
    public async Task ArgbToRgba_MatchesScalar()
    {
        var rng = new Random(6);
        for (int length = 0; length <= 40; length++)
        {
            uint[] argb = RandomPixels(rng, length);
            Rgba32[] scalar = new Rgba32[length], simd = new Rgba32[length];
            WebPDecoder.ArgbToRgbaScalar(argb, scalar);
            WebPDecoder.ArgbToRgba(argb, simd);
            await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task PngUnfilter_MatchesScalar()
    {
        var rng = new Random(7);
        foreach (int bpp in new[] { 1, 2, 3, 4, 6, 8 })
            foreach (int width in new[] { 1, 2, 3, 4, 5, 7, 8, 15, 16, 17, 31, 32 })
                foreach (int filter in new[] { 0, 1, 2, 3, 4 })
                {
                    int rowBytes = width * bpp;
                    int height = 3;
                    byte[] scalar = new byte[(rowBytes + 1) * height];
                    rng.NextBytes(scalar);
                    for (int y = 0; y < height; y++)
                    {
                        scalar[y * (rowBytes + 1)] = (byte)filter;
                    }

                    byte[] simd = (byte[])scalar.Clone();
                    PngDecoder.UnfilterScalar(scalar, 0, width, height, bpp * 8);
                    PngDecoder.Unfilter(simd, 0, width, height, bpp * 8);
                    await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
                }
    }

    [Test]
    public async Task AlphaUnfilter_MatchesScalar()
    {
        var rng = new Random(8);
        foreach (AlphaFilterType filter in new[] { AlphaFilterType.None, AlphaFilterType.Horizontal, AlphaFilterType.Vertical, AlphaFilterType.Gradient })
            foreach (int width in new[] { 1, 2, 3, 7, 8, 15, 16, 17, 31, 32, 33 })
            {
                byte[] scalar = new byte[width * 4];
                rng.NextBytes(scalar);
                byte[] simd = (byte[])scalar.Clone();
                AlphaDecoder.UnfilterScalar(filter, scalar, width, 4);
                AlphaDecoder.Unfilter(filter, simd, width, 4);
                await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
            }
    }

    [Test]
    public async Task PrefixSumBytes_MatchesScalar()
    {
        var rng = new Random(9);
        for (int length = 0; length <= 80; length++)
        {
            byte[] original = new byte[length];
            rng.NextBytes(original);
            byte seed = (byte)rng.Next(256);
            byte[] scalar = (byte[])original.Clone();
            ScanlineSimd.PrefixSumBytesScalar(scalar, seed);
            byte[] simd = (byte[])original.Clone();
            ScanlineSimd.PrefixSumBytes(simd, seed);
            await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
        }
    }

    private static async Task AssertPredictor(Random rng, int width, int height, int bits, int mode)
    {
        int tilesPerRow = Vp8LDecoder.SubSampleSize(width, bits);
        int tiles = tilesPerRow * Vp8LDecoder.SubSampleSize(height, bits);
        uint[] modes = new uint[tiles];
        for (int i = 0; i < tiles; i++)
        {
            int value = mode < 0 ? rng.Next(16) : mode;
            modes[i] = WebPConstants.ArgbBlack | (uint)(value << 8);
        }

        uint[] scalar = RandomPixels(rng, width * height);
        uint[] simd = (uint[])scalar.Clone();
        var transform = new Vp8LTransform { Bits = bits, XSize = width, YSize = height, Data = modes };
        LosslessTransforms.PredictorInverseTransformScalar(transform, scalar);
        LosslessTransforms.PredictorInverseTransform(transform, simd);
        await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
    }

    private static async Task AssertPixels(Action<uint[], uint[]> apply)
    {
        var rng = new Random(2);
        for (int length = 0; length <= 40; length++)
        {
            uint[] scalar = RandomPixels(rng, length);
            uint[] simd = (uint[])scalar.Clone();
            apply(scalar, simd);
            await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
        }
    }

    private static uint[] RandomPixels(Random rng, int length)
    {
        uint[] pixels = new uint[length];
        for (int i = 0; i < length; i++)
        {
            pixels[i] = rng.Next(4) switch
            {
                0 => (uint)rng.Next(),
                1 => 0u,
                2 => 0xffffffffu,
                _ => (uint)((rng.Next(2) * 255) | ((rng.Next(2) * 255) << 8) | ((rng.Next(2) * 255) << 16) | ((rng.Next(2) * 255) << 24)),
            };
        }

        return pixels;
    }

    private static ReadOnlySpan<sbyte> Anchors => [-128, -64, -1, 0, 1, 64, 127];

    private static IEnumerable<sbyte> Sweep
    {
        get
        {
            for (int i = -128; i <= 127; i++)
            {
                yield return (sbyte)i;
            }
        }
    }
}
