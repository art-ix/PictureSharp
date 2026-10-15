using PictureSharp.Algorithms.Compression;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Compression;

/// <summary>The vector checksums (PCLMULQDQ CRC-32, SSSE3 Adler-32) equal the scalar ones for every length and seed.</summary>
public class ChecksumSimdTests
{
    private static byte[] Data(int length, int seed, bool saturated)
    {
        var data = new byte[length];
        if (saturated)
        {
            data.AsSpan().Fill(255);
        }
        else
        {
            new Random(seed).NextBytes(data);
        }

        return data;
    }

    public static TestCases<int> Lengths => new() { 0, 1, 15, 16, 31, 32, 33, 63, 64, 65, 79, 127, 128, 129, 200, 1000, 5551, 5552, 5553, 5568, 11104, 65536, 100_003 };

    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task Crc32_MatchesScalar(int length)
    {
        foreach (bool saturated in new[] { false, true })
        {
            byte[] data = Data(length, length, saturated);
            foreach (uint seed in new[] { 0u, 0xffffffffu, 0x12345678u })
            {
                await Assert.That(Crc32.Update(seed, data)).IsEqualTo(Crc32.UpdateScalar(seed, data));
            }

            // Misaligned starts.
            for (int offset = 1; offset < Math.Min(length, 17); offset += 5)
            {
                await Assert.That(Crc32.Update(7, data.AsSpan(offset))).IsEqualTo(Crc32.UpdateScalar(7, data.AsSpan(offset)));
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task Adler32_MatchesScalar(int length)
    {
        foreach (bool saturated in new[] { false, true })
        {
            byte[] data = Data(length, length + 1, saturated);
            foreach (uint seed in new[] { 1u, 0xfff0fff0u, 0x00010000u, 0xfff00000u })
            {
                await Assert.That(Adler32.Update(seed, data)).IsEqualTo(Adler32.UpdateScalar(seed, data));
            }

            for (int offset = 1; offset < Math.Min(length, 17); offset += 5)
            {
                await Assert.That(Adler32.Update(1, data.AsSpan(offset))).IsEqualTo(Adler32.UpdateScalar(1, data.AsSpan(offset)));
            }
        }
    }
}
