using PictureSharp.Algorithms.Compression;
using PictureSharp.Infrastructure.Codecs.WebP.Lossless;

namespace PictureSharp.Tests.WebP;

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

    // Combined with the prepared terms of y must equal CombinedShannonEntropy, also after AddToCombined updates.
    [Fact]
    public void PreparedCombinedEntropy_MatchesDirect()
    {
        var rng = new Random(21);
        uint[] y = RandomHistogram(rng, 256, 0);
        long[] sLogY = new long[256];
        long total = Vp8LEntropy.PrepareCombined(y, sLogY, out uint sumY);
        for (int i = 0; i < 300; i++)
        {
            uint[] x = RandomHistogram(rng, 256, i % 5);
            Assert.Equal(Vp8LEntropy.Combined(x, y), Vp8LEntropy.Combined(x, y, sLogY, sumY, total));
            Vp8LEntropy.AddToCombined(x, y, sLogY, ref sumY, ref total);
            long[] fresh = new long[256];
            Assert.Equal(Vp8LEntropy.PrepareCombined(y, fresh, out uint freshSum), total);
            Assert.Equal(freshSum, sumY);
            Assert.Equal(fresh, sLogY);
        }
    }

    [Fact]
    public void PopulationCost_MatchesReference()
    {
        var rng = new Random(22);
        foreach (int length in new[] { 1, 2, 3, 8, 9, 15, 40, 256, 280, 300, 1304 })
            for (int i = 0; i < 60; i++)
            {
                uint[] x = RandomHistogram(rng, length, i % 5), y = RandomHistogram(rng, length, (i / 5) % 5);
                Assert.Equal(ReferencePopulationCost(x, y), Vp8LHistogram.PopulationCost(x, y));
                Assert.Equal(ReferencePopulationCost(x, new uint[length]), Vp8LHistogram.PopulationCost(x));
            }
    }

    [Fact]
    public void BitWriter_MatchesBitByBitReference()
    {
        var rng = new Random(23);
        for (int round = 0; round < 20; round++)
        {
            var expected = new List<bool>();
            using var writer = new Vp8LBitWriter();
            using var other = new Vp8LBitWriter();
            var otherBits = new List<bool>();
            for (int i = 0; i < 200; i++)
            {
                int bits = rng.Next(33);
                uint value = (uint)rng.NextInt64(0, 1L << 32);
                writer.Put(value, bits);
                for (int b = 0; b < bits; b++) expected.Add(((value >> b) & 1) != 0);
                int otherCount = rng.Next(20);
                other.Put(value, otherCount);
                for (int b = 0; b < otherCount; b++) otherBits.Add(((value >> b) & 1) != 0);
            }
            if (round % 2 == 0)
            {
                // Byte-aligned concatenation.
                int pad = (8 - expected.Count % 8) % 8;
                writer.Put(0, pad);
                for (int b = 0; b < pad; b++) expected.Add(false);
            }
            writer.Append(other);
            expected.AddRange(otherBits);
            Assert.Equal(expected.Count, writer.BitCount);
            byte[] packed = new byte[(expected.Count + 7) / 8];
            for (int b = 0; b < expected.Count; b++) if (expected[b]) packed[b >> 3] |= (byte)(1 << (b & 7));
            Assert.Equal(packed, writer.ToArray());
        }
    }

    // GetCombinedEntropyUnrefined_C, BitsEntropyRefine and FinalHuffmanCost as a plain run loop (libwebp 1.6.0).
    private static long ReferencePopulationCost(uint[] x, uint[] y)
    {
        uint sum = 0, max = 0;
        int nonzero = 0, zeroRuns = 0, nonzeroRuns = 0, shortZero = 0, longZero = 0, shortNonzero = 0, longNonzero = 0;
        long entropy = 0;
        for (int i = 0; i < x.Length;)
        {
            uint value = x[i] + y[i];
            int end = i + 1;
            while (end < x.Length && x[end] + y[end] == value) end++;
            int run = end - i;
            if (value == 0) { if (run > 3) { zeroRuns++; longZero += run; } else shortZero += run; }
            else
            {
                if (run > 3) { nonzeroRuns++; longNonzero += run; } else shortNonzero += run;
                sum += value * (uint)run; nonzero += run; max = Math.Max(max, value);
                entropy -= Vp8LEntropy.SLog(value) * run;
            }
            i = end;
        }
        long overhead = 57 * Vp8LEntropy.One - Vp8LEntropy.DivideRound(91 * Vp8LEntropy.One, 10);
        overhead += (zeroRuns * 1600L + 240L * longZero) << 13;
        overhead += (nonzeroRuns * 2640L + 720L * longNonzero) << 13;
        overhead += 1840L * shortZero << 13;
        overhead += 3360L * shortNonzero << 13;
        return Vp8LEntropy.Refine(sum, max, nonzero, entropy) + overhead;
    }

    // Kinds: dense small counts, sparse, long runs, large counts (beyond the SLog table) and single values.
    private static uint[] RandomHistogram(Random rng, int length, int kind)
    {
        uint[] counts = new uint[length];
        for (int i = 0; i < length;)
        {
            int run = kind == 2 ? rng.Next(1, 20) : 1;
            uint value = kind switch
            {
                0 => (uint)rng.Next(300),
                1 => rng.Next(6) == 0 ? (uint)rng.Next(1, 50) : 0,
                2 => rng.Next(3) == 0 ? 0 : (uint)rng.Next(1, 4),
                3 => (uint)rng.Next(70000),
                _ => i == length / 2 ? (uint)rng.Next(1, 1 << 24) : 0,
            };
            for (int k = 0; k < run && i < length; k++) counts[i++] = value;
        }

        return counts;
    }
}
