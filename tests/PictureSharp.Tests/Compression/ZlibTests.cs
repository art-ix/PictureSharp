using System.IO.Compression;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Compression;

public class ZlibTests
{
    [Test]
    public async Task Crc32_KnownValues()
    {
        await Assert.That(Crc32.Compute("123456789"u8)).IsEqualTo(0xCBF43926u);
        await Assert.That(Crc32.Compute([])).IsEqualTo(0u);
        byte[] data = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
        await Assert.That(Crc32.Update(Crc32.Compute(data.AsSpan(0, 333)), data.AsSpan(333))).IsEqualTo(Crc32.Compute(data));
        await Assert.That(Crc32.Compute(data)).IsEqualTo(Crc32Reference(data));
    }

    private static uint Crc32Reference(byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    [Test]
    public async Task Adler32_KnownValues()
    {
        await Assert.That(Adler32.Compute("Wikipedia"u8)).IsEqualTo(0x11E60398u);
        await Assert.That(Adler32.Compute([])).IsEqualTo(1u);
        var big = new byte[100_000];
        big.AsSpan().Fill(255);
        uint a = 1, b = 0;
        foreach (byte v in big)
        {
            a = (a + v) % 65521;
            b = (b + a) % 65521;
        }

        await Assert.That(Adler32.Compute(big)).IsEqualTo((b << 16) | a);
    }

    public static TestCases<string> Samples => new() { "empty", "zeros", "random", "text", "image", "repetitive", "large", "ramp", "short" };

    private static byte[] Sample(string name)
    {
        var rng = new Random(name.GetHashCode(StringComparison.Ordinal) & 0xffff);
        switch (name)
        {
            case "empty":
                return [];
            case "ramp":
                return Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
            case "short":
                return "abcabcabcabcx"u8.ToArray();
            case "zeros":
                return new byte[70_000];
            case "random":
                {
                    var data = new byte[50_000];
                    rng.NextBytes(data);
                    return data;
                }

            case "text":
                return System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i % 97} lorem ipsum dolor sit amet; ")));
            case "repetitive":
                return Enumerable.Range(0, 200_000).Select(i => (byte)((i % 7) * (i % 13))).ToArray();
            case "large":
                {
                    var data = new byte[1_500_000];
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] = (byte)((i / 300) + rng.Next(3));
                    }

                    return data;
                }

            default:
                return Infrastructure.Corpus.Read("src/photo.png");
        }
    }

    [Test]
    [MethodDataSource(nameof(Samples))]
    public async Task Deflate_RoundTrip_AllLevels(string sample)
    {
        byte[] data = Sample(sample);
        for (int level = 0; level <= 9; level++)
        {
            byte[] compressed = Deflater.CompressZlib(data, level);
            byte[] back = Inflater.InflateZlib(compressed, 0, compressed.Length, data.Length, long.MaxValue);
            await Assert.That(data.AsSpan().SequenceEqual(back)).IsTrue().Because($"level {level}");
        }
    }

    [Test]
    [MethodDataSource(nameof(Samples))]
    public async Task Deflate_IsReadableByBclZLibStream(string sample)
    {
        byte[] data = Sample(sample);
        foreach (int level in new[] { 0, 1, 6, 9 })
        {
            byte[] compressed = Deflater.CompressZlib(data, level);
            using var zs = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            zs.CopyTo(output);
            await Assert.That(data.AsSpan().SequenceEqual(output.ToArray())).IsTrue().Because($"level {level}");
        }
    }

    [Test]
    [MethodDataSource(nameof(Samples))]
    public async Task Inflate_ReadsBclZLibStream(string sample)
    {
        byte[] data = Sample(sample);
        foreach (CompressionLevel level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        {
            using var ms = new MemoryStream();
            using (var zs = new ZLibStream(ms, level, leaveOpen: true))
            {
                zs.Write(data);
            }

            byte[] compressed = ms.ToArray();
            if (compressed.Length == 0)
            {
                continue;  // ZLibStream writes nothing at all for empty input
            }

            byte[] back = Inflater.InflateZlib(compressed, 0, compressed.Length, 0, long.MaxValue);
            await Assert.That(data.AsSpan().SequenceEqual(back)).IsTrue().Because(level.ToString());
        }
    }

    [Test]
    public async Task Deflate_RandomStructuredInputs_AreReadableByBclZLibStream()
    {
        var rng = new Random(2024);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            int length = rng.Next(0, 20_000);
            var data = new byte[length];
            int pos = 0;
            while (pos < length)
            {
                int run = Math.Min(length - pos, rng.Next(1, 600));
                switch (rng.Next(4))
                {
                    case 0:
                        rng.NextBytes(data.AsSpan(pos, run));
                        break;
                    case 1:
                        data.AsSpan(pos, run).Fill((byte)rng.Next(256));
                        break;
                    case 2:
                        int dist = rng.Next(1, Math.Max(2, Math.Min(pos, 40_000)));
                        for (int i = 0; i < run; i++)
                        {
                            data[pos + i] = pos + i - dist >= 0 ? data[pos + i - dist] : (byte)i;
                        }

                        break;
                    default:
                        for (int i = 0; i < run; i++)
                        {
                            data[pos + i] = (byte)(i * rng.Next(1, 5));
                        }

                        break;
                }

                pos += run;
            }

            int level = rng.Next(0, 10);
            byte[] compressed = Deflater.CompressZlib(data, level);
            using var zs = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            zs.CopyTo(output);
            await Assert.That(data.AsSpan().SequenceEqual(output.ToArray())).IsTrue().Because($"iteration {iteration}, level {level}");
            await Assert.That(data.AsSpan().SequenceEqual(Inflater.InflateZlib(compressed, 0, compressed.Length, 0, long.MaxValue))).IsTrue();
        }
    }

    [Test]
    public async Task Deflate_CompressionIsCompetitiveWithZlib()
    {
        byte[] data = Sample("text").Concat(Sample("image")).Concat(Sample("repetitive")).ToArray();
        byte[] ours = Deflater.CompressZlib(data, 6);
        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zs.Write(data);
        }

        double ratio = (double)ours.Length / ms.Length;
        await Assert.That(ratio < 1.05).IsTrue().Because($"ours {ours.Length} vs zlib {ms.Length} ({ratio:F3})");
    }

    [Test]
    public void Inflate_RejectsCorruptStreams()
    {
        byte[] compressed = Deflater.CompressZlib(Sample("text"), 6);
        Assert.ThrowsExactly<InvalidDataStreamException>(() => Inflater.InflateZlib([0x78, 0x9c], 0, 2, 0, 1000));
        Assert.ThrowsExactly<InvalidDataStreamException>(() => Inflater.InflateZlib([0x00, 0x00, 1, 2], 0, 4, 0, 1000));
        Assert.ThrowsExactly<InvalidDataStreamException>(() => Inflater.InflateZlib(compressed, 0, compressed.Length / 2, 0, long.MaxValue));
        Assert.ThrowsExactly<OutputLimitExceededException>(() => Inflater.InflateZlib(compressed, 0, compressed.Length, 0, 1000));

        byte[] badChecksum = (byte[])compressed.Clone();
        badChecksum[^1] ^= 1;
        Assert.ThrowsExactly<InvalidDataStreamException>(() => Inflater.InflateZlib(badChecksum, 0, badChecksum.Length, 0, long.MaxValue));

        var rng = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            byte[] corrupt = (byte[])compressed.Clone();
            corrupt[rng.Next(2, corrupt.Length)] ^= (byte)(1 << rng.Next(8));
            try
            {
                Inflater.InflateZlib(corrupt, 0, corrupt.Length, 0, 1 << 20);
            }
            catch (InvalidDataStreamException)
            {
            }
        }
    }
}
