using System.Runtime.CompilerServices;

namespace PictureSharp.Domain.Pixels;

/// <summary>Row conversions between 8-bit and 16-bit samples: exact widening and rounding or dithered reduction.</summary>
internal static class PixelDepth
{
    public static void Widen(ReadOnlySpan<Rgba32> src, Span<Rgba64> dst)
    {
        dst = dst[..src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = Rgba64.FromRgba32(src[i]);
        }
    }

    public static void Narrow(ReadOnlySpan<Rgba64> src, Span<Rgba32> dst)
    {
        dst = dst[..src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = src[i].ToRgba32();
        }
    }

    /// <summary>Whether every sample is a multiple of 257, i.e. the pixels reduce to 8 bits without loss.</summary>
    public static bool IsExact8Bit(ReadOnlySpan<Rgba64> pixels)
    {
        foreach (Rgba64 p in pixels)
        {
            if (p.R % 257 != 0 || p.G % 257 != 0 || p.B % 257 != 0 || p.A % 257 != 0)
            {
                return false;
            }
        }

        return true;
    }

    // An 8x8 Bayer matrix: the threshold of pixel (x, y) depends only on its image coordinates.
    private static ReadOnlySpan<byte> Bayer8 =>
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    /// <summary>
    /// Reduces row <paramref name="y"/> with an ordered dither of R, G and B; alpha is rounded. Samples that are exact
    /// 8-bit values (multiples of 257) are never changed, and the result is independent of threads and chunks.
    /// </summary>
    public static void NarrowDithered(ReadOnlySpan<Rgba64> src, Span<Rgba32> dst, int y)
    {
        dst = dst[..src.Length];
        ReadOnlySpan<byte> row = Bayer8.Slice((y & 7) * 8, 8);
        for (int x = 0; x < src.Length; x++)
        {
            Rgba64 p = src[x];
            // Threshold (t + 0.5) / 64 of one 8-bit step, compared in integers: r / 257 > (2t + 1) / 128.
            int threshold = ((2 * row[x & 7]) + 1) * 257;
            dst[x] = new Rgba32(Dither(p.R, threshold), Dither(p.G, threshold), Dither(p.B, threshold), Rgba64.Narrow(p.A));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Dither(ushort v, int threshold)
    {
        int q = v / 257;
        int r = v - (q * 257);
        return (byte)(q + ((r * 128) > threshold ? 1 : 0));
    }
}
