using RasterSharp.Formats.Jpeg;

namespace RasterSharp.Tests.Jpeg;

/// <summary>Vector DCTs, quantization, coefficient masks and the bit writer equal their scalar definitions.</summary>
public class JpegSimdTests
{
    [Fact]
    public void ForwardDct_MatchesScalar()
    {
        var rng = new Random(60);
        for (int trial = 0; trial < 2000; trial++)
        {
            int range = trial % 4 == 0 ? 128 : 1 + rng.Next(128);
            int[] samples = Enumerable.Range(0, 64).Select(_ => rng.Next(-range, range)).ToArray();
            int[] scalar = (int[])samples.Clone();
            int[] simd = (int[])samples.Clone();
            JpegFdct.TransformScalar(scalar);
            JpegFdct.Transform(simd);
            Assert.Equal(scalar, simd);
        }
    }

    [Fact]
    public void QuantizeZigzag_MatchesDivision()
    {
        var rng = new Random(61);
        for (int trial = 0; trial < 500; trial++)
        {
            byte[] table = Enumerable.Range(0, 64).Select(_ => (byte)rng.Next(1, 256)).ToArray();
            int[] data = Enumerable.Range(0, 64).Select(_ => rng.Next(-32768, 32768)).ToArray();
            var natural = new short[64];
            JpegFdct.Quantize(data, table, natural);
            var zigzag = new short[64];
            JpegFdct.QuantizeZigzag(data, new JpegFdct.Divisors(table), zigzag);
            for (int k = 0; k < 64; k++)
            {
                Assert.Equal(natural[JpegTables.NaturalOrder[k]], zigzag[k]);
            }
        }
    }

    [Fact]
    public void InverseDct_MatchesScalar_IncludingOutOfRangeBlocks()
    {
        var rng = new Random(62);
        for (int trial = 0; trial < 4000; trial++)
        {
            var block = new short[64];
            var quant = new short[64];
            int kind = trial % 5;
            for (int i = 0; i < 64; i++)
            {
                // Typical sparse blocks, dense ones, extreme 16-bit tables and values around the vector limit.
                quant[i] = kind == 3 ? (short)rng.Next(1, short.MaxValue) : (short)rng.Next(1, 100);
                bool used = kind == 1 || rng.Next(i < 10 ? 2 : 12) == 0;
                block[i] = !used ? (short)0 : kind switch
                {
                    2 => (short)rng.Next(-12000 / quant[i] - 2, 12000 / quant[i] + 2),
                    3 => (short)rng.Next(short.MinValue, short.MaxValue),
                    _ => (short)rng.Next(-1024 / quant[i] - 1, 1024 / quant[i] + 1),
                };
            }

            const int stride = 11;
            var scalar = new byte[(7 * stride) + 8];
            var simd = new byte[(7 * stride) + 8];
            JpegIdct.TransformScalar(block, quant, scalar, stride);
            JpegIdct.Transform(block, quant, simd, stride);
            Assert.Equal(scalar, simd);
        }
    }

    [Fact]
    public void CoefficientMasks_MatchScalar()
    {
        var rng = new Random(63);
        for (int trial = 0; trial < 1000; trial++)
        {
            short[] block = Enumerable.Range(0, 64).Select(_ => rng.Next(3) == 0 ? (short)rng.Next(-300, 300) : (short)0).ToArray();
            if (trial % 7 == 0)
            {
                block[rng.Next(64)] = short.MinValue + 1;
            }

            Assert.Equal(JpegScanEncoder.NonZeroMaskScalar(block), JpegScanEncoder.NonZeroMask(block));
            foreach (int threshold in new[] { 1, 2, 4, 8, 64 })
            {
                Assert.Equal(JpegScanEncoder.AtLeastMaskScalar(block, threshold), JpegScanEncoder.AtLeastMask(block, threshold));
            }
        }
    }

    // The writer before buffering: one byte at a time with stuffing (with a 64-bit buffer for writes above 25 bits).
    private static byte[] ReferenceBits(List<(int Value, int Count)> writes, bool restart)
    {
        var output = new MemoryStream();
        ulong buffer = 0;
        int bits = 0;
        void Write(int value, int count)
        {
            if (count == 0) return;
            buffer = (buffer << count) | ((uint)value & ((1u << count) - 1));
            bits += count;
            while (bits >= 8)
            {
                bits -= 8;
                byte b = (byte)(buffer >> bits);
                output.WriteByte(b);
                if (b == 255) output.WriteByte(0);
            }
        }

        void Flush()
        {
            if (bits > 0) Write(127, 8 - bits);
            bits = 0;
            buffer = 0;
        }

        for (int i = 0; i < writes.Count; i++)
        {
            Write(writes[i].Value, writes[i].Count);
            if (restart && i == writes.Count / 2)
            {
                Flush();
                output.WriteByte(255);
                output.WriteByte(0xD3);
            }
        }

        Flush();
        return output.ToArray();
    }

    [Fact]
    public void BitWriter_MatchesByteWiseReference()
    {
        var rng = new Random(64);
        for (int trial = 0; trial < 200; trial++)
        {
            var writes = new List<(int, int)>();
            int count = rng.Next(0, 20000);
            for (int i = 0; i < count; i++)
            {
                int bits = rng.Next(0, 28);
                int value = trial % 3 == 0 ? -1 : rng.Next();  // all-ones data forces stuffing
                writes.Add((value, bits));
            }

            bool restart = trial % 2 == 1;
            var output = new MemoryStream();
            var writer = new JpegBitWriter(output);
            for (int i = 0; i < writes.Count; i++)
            {
                writer.Write(writes[i].Item1, writes[i].Item2);
                if (restart && i == writes.Count / 2)
                {
                    writer.Restart(3);
                }
            }

            writer.Flush();
            Assert.Equal(ReferenceBits(writes, restart), output.ToArray());
        }
    }

    [Theory]
    [InlineData(JpegSubsampling.Ratio420, false)]
    [InlineData(JpegSubsampling.Ratio422, false)]
    [InlineData(JpegSubsampling.Ratio440, true)]
    [InlineData(JpegSubsampling.Ratio444, true)]
    public void Encode_DoesNotDependOnThreads_AndDecodesTheSameInParallel(JpegSubsampling subsampling, bool progressive)
    {
        var rng = new Random(65);
        var pixels = new Rgba32[701 * 403];
        for (int i = 0; i < pixels.Length; i++)
        {
            int x = i % 701, y = i / 701;
            pixels[i] = new Rgba32((byte)(x + rng.Next(20)), (byte)(y * 2), (byte)((x ^ y) + rng.Next(9)), (byte)(i % 97 == 0 ? 128 : 255));
        }

        using Image image = Image.LoadPixelData(pixels, 701, 403);
        var options = new JpegEncoderOptions { Subsampling = subsampling, Progressive = progressive, Quality = 90, MaxDegreeOfParallelism = 1 };
        byte[] sequential = image.ToJpeg(options);
        Assert.Equal(sequential, image.ToJpeg(options with { MaxDegreeOfParallelism = 3 }));
        Assert.Equal(sequential, image.ToJpeg(options with { MaxDegreeOfParallelism = -1 }));

        using Image serial = Image.Load(sequential, new DecoderOptions { MaxDegreeOfParallelism = 1 });
        using Image parallel = Image.Load(sequential);
        Assert.True(serial.Frames[0].GetPixelSpan().SequenceEqual(parallel.Frames[0].GetPixelSpan()));
    }
}
