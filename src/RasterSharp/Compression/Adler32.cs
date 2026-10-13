namespace RasterSharp.Compression;

/// <summary>Adler-32 checksum (RFC 1950).</summary>
internal static class Adler32
{
    private const uint Base = 65521;
    private const int NMax = 5552;  // largest n such that 255n(n+1)/2 + (n+1)(Base-1) <= 2^32-1

    public static uint Compute(ReadOnlySpan<byte> data) => Update(1, data);

    public static uint Update(uint adler, ReadOnlySpan<byte> data)
    {
        uint a = adler & 0xffff;
        uint b = adler >> 16;
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, NMax);
            foreach (byte v in data[..n])
            {
                a += v;
                b += a;
            }

            a %= Base;
            b %= Base;
            data = data[n..];
        }

        return (b << 16) | a;
    }
}
