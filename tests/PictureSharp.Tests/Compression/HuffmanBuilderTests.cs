using PictureSharp.Algorithms.Compression;

namespace PictureSharp.Tests.Compression;

public class HuffmanBuilderTests
{
    private static async Task AssertValidCode(int[] freqs, int maxLength)
    {
        var lengths = new byte[freqs.Length];
        HuffmanBuilder.BuildLengths(freqs, maxLength, lengths);
        int used = freqs.Count(f => f > 0);
        double kraft = 0;
        for (int i = 0; i < freqs.Length; i++)
        {
            await Assert.That(lengths[i] <= maxLength).IsTrue().Because($"length {lengths[i]} > {maxLength}");
            await Assert.That(freqs[i] > 0 == lengths[i] > 0).IsTrue().Because($"symbol {i}: freq {freqs[i]} length {lengths[i]}; freqs=[{string.Join(",", freqs)}] lengths=[{string.Join(",", lengths)}]");
            if (lengths[i] > 0)
            {
                kraft += Math.Pow(2, -lengths[i]);
            }
        }

        if (used >= 2)
        {
            await Assert.That(Math.Round((double)kraft, 9)).IsEqualTo(Math.Round((double)1.0, 9));
        }
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task RandomFrequencies_GiveCompletePrefixCodes()
    {
        var rng = new Random(42);
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            int maxLength = iteration % 2 == 0 ? 15 : 7;
            int n = rng.Next(1, maxLength == 7 ? 128 : 320);  // at most 2^maxLength symbols fit
            var freqs = new int[n];
            int mode = iteration % 4;
            for (int i = 0; i < n; i++)
            {
                freqs[i] = mode switch
                {
                    0 => rng.Next(0, 3),
                    1 => rng.Next(0, 1000),
                    2 => (int)Math.Min(int.MaxValue / 400, Math.Pow(1.7, rng.Next(0, 40))),  // skewed: forces length limiting
                    _ => 1,
                };
            }

            await AssertValidCode(freqs, maxLength);
        }
    }

    [Test]
    public async Task Fibonacci_IsLimited()
    {
        var freqs = new int[30];
        freqs[0] = freqs[1] = 1;
        for (int i = 2; i < freqs.Length; i++)
        {
            freqs[i] = freqs[i - 1] + freqs[i - 2];
        }

        await AssertValidCode(freqs, 15);
        await AssertValidCode(freqs, 7);
    }
}
