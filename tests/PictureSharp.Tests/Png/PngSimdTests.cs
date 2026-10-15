using PictureSharp.Infrastructure.Codecs.Png;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Png;

/// <summary>Vector scanline filters equal the scalar ones and a byte-wise reference of the PNG definitions.</summary>
public class PngSimdTests
{
    private static readonly int[] Bpps = [1, 2, 3, 4, 6, 8];
    private static readonly int[] RowLengths = [1, 2, 3, 7, 15, 16, 17, 31, 32, 33, 64, 100, 255, 256, 4097, 8200];

    private static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private static byte Predict(int filter, byte[] row, byte[]? prev, int i, int bpp)
    {
        int a = i >= bpp ? row[i - bpp] : 0;
        int b = prev?[i] ?? 0;
        int c = i >= bpp && prev is not null ? prev[i - bpp] : 0;
        return filter switch { 0 => 0, 1 => (byte)a, 2 => (byte)b, 3 => (byte)((a + b) >> 1), _ => Paeth(a, b, c) };
    }

    // Rows with bpp leading zeros, as the encoder passes them.
    private static byte[] Padded(byte[]? row, int bpp, int length)
    {
        var padded = new byte[bpp + length];
        row?.CopyTo(padded, bpp);
        return padded;
    }

    [Fact]
    public void EncoderFilters_MatchReference()
    {
        var rng = new Random(46);
        foreach (int bpp in Bpps)
            foreach (int length in RowLengths)
                foreach (bool first in new[] { false, true })
                    foreach (bool smooth in new[] { false, true })
                    {
                        var row = new byte[length];
                        var prev = new byte[length];
                        rng.NextBytes(row);
                        rng.NextBytes(prev);
                        if (smooth)
                        {
                            for (int i = 1; i < length; i++)
                            {
                                row[i] = (byte)(row[i - 1] + (rng.Next(7) - 3));
                                prev[i] = (byte)(row[i] + (rng.Next(5) - 2));
                            }
                        }

                        byte[]? up = first ? null : prev;
                        byte[] paddedRow = Padded(row, bpp, length);
                        byte[] paddedPrev = Padded(up, bpp, length);

                        var expectedCosts = new long[5];
                        var filtered = new byte[5][];
                        for (int f = 0; f < 5; f++)
                        {
                            filtered[f] = new byte[length];
                            for (int i = 0; i < length; i++)
                            {
                                filtered[f][i] = (byte)(row[i] - Predict(f, row, up, i, bpp));
                                expectedCosts[f] += filtered[f][i] < 128 ? filtered[f][i] : 256 - filtered[f][i];
                            }

                            var actual = new byte[length];
                            PngEncoder.ApplyFilter(f, paddedRow, paddedPrev, bpp, actual);
                            Assert.Equal(filtered[f], actual);
                            var scalar = new byte[length];
                            if (f != 0)
                            {
                                PngEncoder.ApplyFilterScalar(f, paddedRow, paddedPrev, bpp, 0, scalar);
                                Assert.Equal(filtered[f], scalar);
                            }
                        }

                        var scalarCosts = new long[5];
                        PngEncoder.FilterCostsScalar(paddedRow, paddedPrev, bpp, 0, scalarCosts);
                        Assert.Equal(expectedCosts, scalarCosts);
                        int best = Array.IndexOf(expectedCosts, expectedCosts.Min());
                        Assert.Equal(best, PngEncoder.SelectFilter(paddedRow, paddedPrev, bpp));
                    }
    }

    [Fact]
    public void Unfilter_MatchesReference()
    {
        var rng = new Random(47);
        foreach (int bpp in Bpps)
            foreach (int width in new[] { 1, 2, 3, 5, 16, 17, 33, 200 })
            {
                int rowBytes = width * bpp;
                const int height = 7;
                var data = new byte[(rowBytes + 1) * height];
                rng.NextBytes(data);
                for (int y = 0; y < height; y++)
                {
                    data[y * (rowBytes + 1)] = (byte)rng.Next(5);
                }

                // Byte-wise reference of the PNG definitions.
                byte[] expected = (byte[])data.Clone();
                for (int y = 0; y < height; y++)
                {
                    int start = (y * (rowBytes + 1)) + 1;
                    var row = new byte[rowBytes];
                    byte[]? prev = y == 0 ? null : expected.AsSpan(start - rowBytes - 1, rowBytes).ToArray();
                    for (int i = 0; i < rowBytes; i++)
                    {
                        row[i] = (byte)(expected[start + i] + Predict(expected[start - 1], row, prev, i, bpp));
                    }

                    row.CopyTo(expected, start);
                }

                byte[] scalar = (byte[])data.Clone();
                byte[] simd = (byte[])data.Clone();
                PngDecoder.UnfilterScalar(scalar, 0, width, height, bpp * 8);
                PngDecoder.Unfilter(simd, 0, width, height, bpp * 8);
                Assert.Equal(expected, scalar);
                Assert.Equal(expected, simd);
            }
    }

    [Fact]
    public void ScanAlphaAndGray_MatchesScalar()
    {
        var rng = new Random(48);
        foreach (int length in new[] { 0, 1, 3, 4, 5, 15, 1023, 1024, 1030, 5000 })
        {
            for (int trial = 0; trial < 8; trial++)
            {
                var pixels = new Rgba32[length];
                for (int i = 0; i < length; i++)
                {
                    byte g = (byte)rng.Next(256);
                    pixels[i] = new Rgba32(g, g, g, 255);
                }

                if (length > 0 && (trial & 1) != 0)
                {
                    pixels[rng.Next(length)].A = 254;
                }

                if (length > 0 && (trial & 2) != 0)
                {
                    pixels[rng.Next(length)].B ^= 1;
                }

                bool alpha = pixels.Any(p => p.A != 255);
                bool gray = pixels.All(p => p.R == p.G && p.G == p.B);
                Assert.Equal((alpha, gray), PngEncoder.ScanAlphaAndGray(pixels));
            }
        }
    }
}
