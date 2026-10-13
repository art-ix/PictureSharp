using RasterSharp.Formats.WebP.Lossless;

namespace RasterSharp.Tests.WebP;

public class LosslessEncoderPrimitiveTests
{
    // Scalar libwebp v1.6.0 VP8LFastLog2 / VP8LFastSLog2, including approximation boundaries.
    [Theory]
    [InlineData(0, 0L, 0L)]
    [InlineData(1, 0L, 0L)]
    [InlineData(2, 8388608L, 16777216L)]
    [InlineData(255, 67061497L, 17100681785L)]
    [InlineData(256, 67108864L, 17179869184L)]
    [InlineData(4095, 100615929L, 412203762300L)]
    [InlineData(4096, 100663296L, 412316860416L)]
    [InlineData(65535, 134217451L, 8795940669900L)]
    [InlineData(65536, 134217728L, 8796093022208L)]
    [InlineData(16777216, 201326592L, 3377699720527872L)]
    [InlineData(100000000, 222930821L, 22293082073757512L)]
    public void FixedPointEntropy_MatchesLibwebp160(uint value, long logarithm, long weightedLogarithm)
    {
        Assert.Equal(logarithm, Vp8LEntropy.Log(value));
        Assert.Equal(weightedLogarithm, Vp8LEntropy.SLog(value));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(15)]
    public void SkewedHistogram_ProducesCompleteLengthLimitedTree(int limit)
    {
        uint[] histogram = new uint[40];
        histogram[0] = histogram[1] = 1;
        for (int i = 2; i < 30; i++) histogram[i] = histogram[i - 1] + histogram[i - 2];
        var encoder = new Vp8LHuffmanEncoder(histogram, limit);
        int kraftSum = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] != 0) Assert.InRange(encoder.Lengths[i], (byte)1, (byte)limit);
            if (encoder.Lengths[i] != 0) kraftSum += 1 << (limit - encoder.Lengths[i]);
        }
        Assert.Equal(1 << limit, kraftSum);
        Assert.NotNull(HuffmanTableBuilder.Build(8, encoder.Lengths.Select(x => (int)x).ToArray()));
    }

    [Fact]
    public void TwoSymbolEntropy_DoesNotOverflowAtLargeImageSizes()
    {
        long entropy = Vp8LEntropy.Bits([50000000u, 50000000u]);
        Assert.InRange(entropy, 99999999L * Vp8LEntropy.One, 100000001L * Vp8LEntropy.One);
    }
}
