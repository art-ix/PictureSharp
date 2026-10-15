using PictureSharp.Algorithms.Compression;

namespace PictureSharp.Tests.Compression;

public class HuffmanBuilderTests
{
    private static void AssertValidCode(int[] freqs, int maxLength)
    {
        var lengths = new byte[freqs.Length];
        HuffmanBuilder.BuildLengths(freqs, maxLength, lengths);
        int used = freqs.Count(f => f > 0);
        double kraft = 0;
        for (int i = 0; i < freqs.Length; i++)
        {
            Assert.True(lengths[i] <= maxLength, $"length {lengths[i]} > {maxLength}");
            Assert.True(freqs[i] > 0 == lengths[i] > 0, $"symbol {i}: freq {freqs[i]} length {lengths[i]}; freqs=[{string.Join(",", freqs)}] lengths=[{string.Join(",", lengths)}]");
            if (lengths[i] > 0)
            {
                kraft += Math.Pow(2, -lengths[i]);
            }
        }

        if (used >= 2)
        {
            Assert.Equal(1.0, kraft, 9);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void RandomFrequencies_GiveCompletePrefixCodes()
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

            AssertValidCode(freqs, maxLength);
        }
    }

    [Fact]
    public void Fibonacci_IsLimited()
    {
        var freqs = new int[30];
        freqs[0] = freqs[1] = 1;
        for (int i = 2; i < freqs.Length; i++)
        {
            freqs[i] = freqs[i - 1] + freqs[i - 2];
        }

        AssertValidCode(freqs, 15);
        AssertValidCode(freqs, 7);
    }
}
