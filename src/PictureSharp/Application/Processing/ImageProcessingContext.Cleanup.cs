using PictureSharp.Algorithms.Processing;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Removes dust specks: a pixel whose luma is brighter (or darker) than the median of every one of the four
    /// segments of 2 · <paramref name="size"/> + 1 pixels through it (horizontal, vertical, both diagonals) by more
    /// than a limit is a speck. The limit is <paramref name="threshold"/> or three times the local noise (from the
    /// median absolute deviation of each 32 × 32 tile), whichever is larger, so film grain is not taken for dust. The
    /// soft rim then joins over at most <paramref name="size"/> rounds: a neighbour of a speck whose luma differs from
    /// the median of its (2 · size + 1)² window in the same direction by more than half the limit. A connected group
    /// of speck pixels is removed only when it spans at most <paramref name="size"/> + 2 pixels and no pixel around it
    /// differs from the median of its window in the same direction by more than half the largest difference of the
    /// group's core, so larger detail that the four directions miss, such as the strokes of a slanted letter, stays.
    /// The specks, their rims and the ring of pixels around them get the per-channel median of that window without
    /// those pixels. Edges and thin lines are kept, because they always have a segment along themselves. Alpha is
    /// kept.
    /// </summary>
    /// <remarks>
    /// Only luma is examined, so a faint speck that stands out no more than the grain stays, and small isolated detail
    /// no larger than <paramref name="size"/>, such as a catchlight, the dot of an i or the hole of a small 0, is taken
    /// for dust. Keep <paramref name="size"/> near the size of the dust; a scanner's infrared dust channel separates
    /// dust reliably.
    /// </remarks>
    /// <param name="size">The largest speck diameter in pixels, 1 to 64.</param>
    /// <param name="threshold">The smallest difference from the surroundings, 0 to 1 of the luma range (0.1 is about 26 levels); 0 is not allowed.</param>
    /// <example>
    /// <code>image.Mutate(x => x.RemoveDust(size: 6));   // dust on a photographed film negative</code>
    /// </example>
    public ImageProcessingContext RemoveDust(int size = 3, float threshold = 0.1f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 64);
        if (!(threshold is > 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Expected a number above 0 and at most 1.");
        }

        return LocalFilter((size, threshold), static (source, destination, w, h, state, token) =>
            DustFilter.Remove(source, destination, w, h, state.size, state.threshold, token));
    }

    /// <summary>
    /// Reduces color noise (blotches of random hue) and keeps the luma: the chroma differences B − Y and R − Y are
    /// smoothed with a guided filter that the luma Y guides, over windows of (2 · <paramref name="radius"/> + 1)²
    /// pixels, so flat areas lose their color noise while color edges that are also luma edges stay sharp. Works on
    /// premultiplied values like <see cref="Denoise(float, int)"/>; alpha is kept.
    /// </summary>
    /// <param name="strength">0 to 1: the share of the smoothed chroma; 0 keeps the image.</param>
    /// <param name="radius">Window radius in pixels; 0 (the default) uses 1% of the shorter side, at least 2.</param>
    /// <remarks>An edge between two colors of the same brightness is softened, as in any chroma-only filter.</remarks>
    public ImageProcessingContext ReduceColorNoise(float strength = 1f, int radius = 0)
    {
        if (!(strength is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "Expected a number between 0 and 1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        if (strength == 0f)
        {
            return this;
        }

        int r = radius > 0 ? radius : ChromaFilter.AutomaticRadius(Target.Width, Target.Height);
        return LocalFilter((strength, r), static (source, destination, w, h, state, token) =>
            ChromaFilter.Reduce(source, destination, w, h, state.strength, state.r, token));
    }
}
