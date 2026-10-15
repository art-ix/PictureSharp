using PictureSharp.Infrastructure.Codecs.WebP.Lossless;

namespace PictureSharp.Tests.WebP;

/// <summary>SIMD paths of the lossless encoder must match the scalar code on every input.</summary>
public class LosslessEncoderSimdTests
{
    [Test]
    public async Task CollectColorRedTransforms_MatchesScalar()
    {
        var rng = new Random(11);
        foreach (int greenToRed in Multipliers.ToArray())
            for (int length = 0; length <= 40; length++)
            {
                uint[] pixels = RandomPixels(rng, length);
                uint[] scalar = new uint[256], simd = new uint[256];
                Vp8LForwardTransforms.CollectColorRedTransformsScalar(pixels, greenToRed, scalar);
                Vp8LForwardTransforms.CollectColorRedTransforms(pixels, greenToRed, simd);
                await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
            }
    }

    [Test]
    public async Task CollectColorBlueTransforms_MatchesScalar()
    {
        var rng = new Random(12);
        foreach (int greenToBlue in Multipliers.ToArray())
            foreach (int redToBlue in Multipliers.ToArray())
                foreach (int length in new[] { 0, 1, 3, 4, 7, 8, 9, 15, 16, 33 })
                {
                    uint[] pixels = RandomPixels(rng, length);
                    uint[] scalar = new uint[256], simd = new uint[256];
                    Vp8LForwardTransforms.CollectColorBlueTransformsScalar(pixels, greenToBlue, redToBlue, scalar);
                    Vp8LForwardTransforms.CollectColorBlueTransforms(pixels, greenToBlue, redToBlue, simd);
                    await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
                }
    }

    [Test]
    public async Task PredictorSub_MatchesScalar()
    {
        var rng = new Random(13);
        const int Width = 40;
        for (int mode = 0; mode < 14; mode++)
            for (int start = 0; start <= 2; start++)
                for (int end = start; end <= Width; end++)
                {
                    // Two rows one pixel wider than the image, as in the encoder; the last pixel is the top-right of the edge.
                    uint[] rows = RandomPixels(rng, 2 * (Width + 1));
                    uint[] scalar = new uint[end - start], simd = new uint[end - start];
                    Vp8LForwardTransforms.PredictBatchScalar(rows, 0, Width + 1, mode, start, end, scalar);
                    Vp8LForwardTransforms.PredictBatch(rows, 0, Width + 1, mode, start, end, simd);
                    await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
                }
    }

    [Test]
    public async Task NonZeroMask_MatchesScalar()
    {
        var rng = new Random(14);
        for (int i = 0; i < 200; i++)
        {
            uint[] counts = new uint[32];
            for (int j = 0; j < counts.Length; j++) counts[j] = rng.Next(3) == 0 ? (uint)rng.Next(1, 1 << 28) : 0;
            await Assert.That(Vp8LEntropy.NonZeroMask32(counts)).IsEqualTo(Vp8LEntropy.NonZeroMask32Scalar(counts));
        }
    }

    // Saturating byte multipliers; the transforms only use their low byte.
    private static ReadOnlySpan<int> Multipliers => [-128, -127, -64, -33, -1, 0, 1, 31, 64, 127, 255, -255];

    private static uint[] RandomPixels(Random rng, int length)
    {
        uint[] pixels = new uint[length];
        for (int i = 0; i < length; i++)
        {
            // Mix extreme bytes with random ones to cover the sign and saturation cases.
            uint value = (uint)rng.NextInt64(0, 1L << 32);
            if (rng.Next(4) == 0) value |= 0x80808080u;
            if (rng.Next(4) == 0) value &= 0x7f7f7f7fu;
            pixels[i] = value;
        }

        return pixels;
    }
}
