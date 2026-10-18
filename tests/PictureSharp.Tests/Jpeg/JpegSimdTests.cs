using PictureSharp.Formats.Jpeg;
using PictureSharp.Infrastructure.Codecs.Jpeg;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Jpeg;

/// <summary>
/// Vector DCTs, quantization, sampling, color conversion, coefficient masks and the bit writer equal their scalar
/// definitions.
/// </summary>
public class JpegSimdTests
{
    [Test]
    public async Task ForwardDct_MatchesScalar()
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
            await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task QuantizeZigzag_MatchesDivision()
    {
        var rng = new Random(61);
        for (int trial = 0; trial < 500; trial++)
        {
            byte[] table = Enumerable.Range(0, 64).Select(_ => (byte)rng.Next(1, 256)).ToArray();
            int[] data = Enumerable.Range(0, 64).Select(_ => rng.Next(-32768, 32768)).ToArray();
            var natural = new short[64];
            JpegFdct.Quantize(data, table, natural);
            var zigzag = new short[64];
            var scalar = new short[64];
            JpegFdct.QuantizeZigzag(data, new JpegFdct.Divisors(table), zigzag);
            JpegFdct.QuantizeZigzagScalar(data, new JpegFdct.Divisors(table), scalar);
            for (int k = 0; k < 64; k++)
            {
                await Assert.That(zigzag[k]).IsEqualTo(natural[JpegTables.NaturalOrder[k]]);
                await Assert.That(scalar[k]).IsEqualTo(natural[JpegTables.NaturalOrder[k]]);
            }
        }
    }

    [Test]
    public async Task InverseDct_MatchesScalar_IncludingOutOfRangeBlocks()
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
            await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task InverseDctPair_MatchesScalar_WhenEitherBlockIsOutOfRange()
    {
        var rng = new Random(66);
        for (int trial = 0; trial < 2000; trial++)
        {
            (short[] blockA, short[] quantA) = RandomBlock(rng, trial % 4 == 1);
            (short[] blockB, short[] quantB) = RandomBlock(rng, trial % 4 == 2);
            const int strideA = 13, strideB = 9;
            var expectedA = new byte[(7 * strideA) + 8];
            var expectedB = new byte[(7 * strideB) + 8];
            JpegIdct.TransformScalar(blockA, quantA, expectedA, strideA);
            JpegIdct.TransformScalar(blockB, quantB, expectedB, strideB);
            var actualA = new byte[expectedA.Length];
            var actualB = new byte[expectedB.Length];
            JpegIdct.Transform(blockA, quantA, actualA, strideA, blockB, quantB, actualB, strideB);
            await Assert.That(actualA).IsEquivalentTo(expectedA, CollectionOrdering.Matching);
            await Assert.That(actualB).IsEquivalentTo(expectedB, CollectionOrdering.Matching);
        }

        static (short[] Block, short[] Quant) RandomBlock(Random rng, bool extreme)
        {
            var block = new short[64];
            var quant = new short[64];
            for (int i = 0; i < 64; i++)
            {
                quant[i] = extreme ? (short)rng.Next(1, short.MaxValue) : (short)rng.Next(1, 100);
                if (rng.Next(i < 10 ? 2 : 6) == 0)
                {
                    block[i] = extreme ? (short)rng.Next(short.MinValue, short.MaxValue) : (short)rng.Next(-12000 / quant[i] - 1, 12000 / quant[i] + 1);
                }
            }

            return (block, quant);
        }
    }

    [Test]
    public async Task Upsampling_AndYccToRgb_MatchScalar()
    {
        var rng = new Random(67);
        foreach (int width in new[] { 3, 8, 9, 10, 16, 17, 18, 31, 33, 100, 257 })
        {
            byte[] near = RandomBytes(rng, width), far = RandomBytes(rng, width);
            var simd = new byte[(2 * width) + 16];
            var scalar = new byte[(2 * width) + 16];
            JpegColorConverter.H2V2(near, far, simd);
            JpegColorConverter.H2V2(near, far, scalar, simd: false);
            await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
            JpegColorConverter.H2V1(near, simd);
            JpegColorConverter.H2V1(near, scalar, simd: false);
            await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
            foreach (int bias in new[] { 1, 2 })
            {
                JpegColorConverter.H1V2(near, far, bias, simd);
                JpegColorConverter.H1V2(near, far, bias, scalar, simd: false);
                await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
            }

            byte[] y = RandomBytes(rng, width);
            var rgbSimd = new Rgba32[width];
            var rgbScalar = new Rgba32[width];
            JpegColorConverter.YccToRgb(y, near, far, rgbSimd);
            JpegColorConverter.YccToRgb(y, near, far, rgbScalar, simd: false);
            await Assert.That(rgbSimd).IsEquivalentTo(rgbScalar, CollectionOrdering.Matching);
        }

        // Extremes saturate in every channel.
        byte[] ends = [0, 255, 0, 255, 0, 255, 0, 255, 255, 0, 255, 0, 255, 0, 255, 0, 0, 255];
        var a = new Rgba32[ends.Length];
        var b = new Rgba32[ends.Length];
        JpegColorConverter.YccToRgb(ends, ends.Reverse().ToArray(), ends, a);
        JpegColorConverter.YccToRgb(ends, ends.Reverse().ToArray(), ends, b, simd: false);
        await Assert.That(a).IsEquivalentTo(b, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EncoderSampling_AndColorConversion_MatchScalar()
    {
        var rng = new Random(68);
        const int width = 37;
        byte[] plane = RandomBytes(rng, width * 20);
        foreach ((int dx, int dy) in new[] { (1, 1), (2, 1), (1, 2), (2, 2) })
        {
            var simd = new int[64];
            var scalar = new int[64];
            if (!JpegEncoderComponent.LoadBlockSimd(plane.AsSpan(3), width, dx, dy, simd))
            {
                continue;   // no vector hardware: only the scalar code runs
            }

            JpegEncoderComponent.LoadBlock(plane.AsSpan(3), width, dx, dy, scalar);
            await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
        }

        // Opaque runs, translucent pixels inside groups of eight, a gray image and a tail.
        var pixels = new Rgba32[203];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)(i % 29 == 0 ? rng.Next(256) : 255));
        }

        var background = new Rgba32(30, 90, 200, 255);
        foreach (int components in new[] { 1, 3 })
        {
            byte[][] planes = Enumerable.Range(0, components).Select(_ => new byte[pixels.Length + 5]).ToArray();
            byte[][] expected = Enumerable.Range(0, components).Select(_ => new byte[pixels.Length + 5]).ToArray();
            JpegEncoderComponent.ConvertColors(pixels, planes, 5, background);
            JpegEncoderComponent.ConvertColorsScalar(pixels, expected[0].AsSpan(5), components == 1 ? default : expected[1].AsSpan(5),
                components == 1 ? default : expected[2].AsSpan(5), background);
            for (int c = 0; c < components; c++)
            {
                await Assert.That(planes[c]).IsEquivalentTo(expected[c], CollectionOrdering.Matching);
            }
        }
    }

    private static byte[] RandomBytes(Random rng, int length)
    {
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    [Test]
    public async Task CoefficientMasks_MatchScalar()
    {
        var rng = new Random(63);
        for (int trial = 0; trial < 1000; trial++)
        {
            short[] block = Enumerable.Range(0, 64).Select(_ => rng.Next(3) == 0 ? (short)rng.Next(-300, 300) : (short)0).ToArray();
            if (trial % 7 == 0)
            {
                block[rng.Next(64)] = short.MinValue + 1;
            }

            await Assert.That(JpegScanEncoder.NonZeroMask(block)).IsEqualTo(JpegScanEncoder.NonZeroMaskScalar(block));
            foreach (int threshold in new[] { 1, 2, 4, 8, 64 })
            {
                await Assert.That(JpegScanEncoder.AtLeastMask(block, threshold)).IsEqualTo(JpegScanEncoder.AtLeastMaskScalar(block, threshold));
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

    [Test]
    public async Task BitWriter_MatchesByteWiseReference()
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
            await Assert.That(output.ToArray()).IsEquivalentTo(ReferenceBits(writes, restart), CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments(JpegSubsampling.Ratio420, false)]
    [Arguments(JpegSubsampling.Ratio422, false)]
    [Arguments(JpegSubsampling.Ratio440, true)]
    [Arguments(JpegSubsampling.Ratio444, true)]
    public async Task Encode_DoesNotDependOnThreads_AndDecodesTheSameInParallel(JpegSubsampling subsampling, bool progressive)
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
        await Assert.That(image.ToJpeg(options with { MaxDegreeOfParallelism = 3 })).IsEquivalentTo(sequential, CollectionOrdering.Matching);
        await Assert.That(image.ToJpeg(options with { MaxDegreeOfParallelism = -1 })).IsEquivalentTo(sequential, CollectionOrdering.Matching);

        using Image serial = Image.Load(sequential, new DecoderOptions { MaxDegreeOfParallelism = 1 });
        using Image parallel = Image.Load(sequential);
        await Assert.That(serial.Frames[0].GetPixelSpan().SequenceEqual(parallel.Frames[0].GetPixelSpan())).IsTrue();
    }
}
