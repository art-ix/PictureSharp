using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

internal static partial class GrayWorld
{
    /// <summary>Length of the 16-bit luma counts: one per value.</summary>
    public const int LumaLength16 = ushort.MaxValue + 1;

    /// <summary>Length of the 16-bit red, green and blue counts, back to back.</summary>
    public const int ChannelLength16 = 3 * (ushort.MaxValue + 1);

    // The 8-bit limits 3–252 for the samples that round to them: (v + 128) / 257 in 3–252.
    public const int LowestCandidate16 = (3 * 257) - 128, HighestCandidate16 = (253 * 257) - 129;

    /// <summary>Integer Rec. 709 luma of 16-bit encoded values, rounded half up.</summary>
    public static int Luma(Rgba64 p) => ((2126 * p.R) + (7152 * p.G) + (722 * p.B) + 5000) / 10000;

    /// <summary>
    /// Whether <paramref name="p"/> takes part in the measurement: not fully transparent, and every channel rounds to
    /// an 8-bit value outside the clipped 0–2 and 253–255, so an exactly widened image selects the same pixels.
    /// </summary>
    public static bool IsCandidate(Rgba64 p) =>
        p.A != 0 && p.R is >= LowestCandidate16 and <= HighestCandidate16 && p.G is >= LowestCandidate16 and <= HighestCandidate16
            && p.B is >= LowestCandidate16 and <= HighestCandidate16;

    /// <summary>Adds the luma of the candidate pixels to <paramref name="counts"/> (<see cref="LumaLength16"/>).</summary>
    public static void CountLuma(ReadOnlySpan<Rgba64> pixels, Span<long> counts, CancellationToken cancellationToken)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Rgba64 p = pixels[i];
            if (IsCandidate(p))
            {
                counts[Luma(p)]++;
            }
        }
    }

    /// <summary>Adds the channel values of the candidate pixels with luma in <paramref name="range"/> to <paramref name="counts"/> (<see cref="ChannelLength16"/>).</summary>
    public static void CountChannels(ReadOnlySpan<Rgba64> pixels, (int Low, int High) range, Span<long> counts, CancellationToken cancellationToken)
    {
        const int N = ushort.MaxValue + 1;
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Rgba64 p = pixels[i];
            if (IsCandidate(p) && Luma(p) is int y && y >= range.Low && y <= range.High)
            {
                counts[p.R]++;
                counts[N + p.G]++;
                counts[(2 * N) + p.B]++;
            }
        }
    }
}
