using System.Buffers;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Removal of small specks (dust on a scanned negative or print) that differ from their surroundings in luma, while
/// edges and thin lines stay: a pixel is a speck only when it is brighter (or darker) by more than the threshold than
/// the median of each of the four segments of 2·size + 1 pixels through it (horizontal, vertical and both diagonals).
/// A speck at most <c>size</c> pixels across is a minority on every segment, whereas an edge or a line always has a
/// segment along itself. The threshold rises to three times the local noise (the median absolute deviation of each
/// 32 × 32 tile), so film grain is not taken for dust. Detail between the four directions (a slanted stroke) is
/// detected too, but only small, isolated groups of speck pixels are replaced. Mirrored by
/// tests/assets/photo_adjust_reference.py. A 16-bit image is examined on its 16-bit luma with the same rules (limits
/// scaled to 0–65535) and replaced by 16-bit medians; 8-bit medians count values, 16-bit ones sort them.
/// </summary>
internal static class DustFilter
{
    private const byte Bright = 1, Dark = 2, Rejected = 4, Core = 8, Visited = 16, Ring = 32, Member = 64, Checked = 128;
    private const int SpeckMask = Bright | Dark;
    private const int Tile = 32;

    // σ of Gaussian noise from the median absolute deviation: 1 / Φ⁻¹(3/4). Film grain is correlated over several
    // pixels, so differences of neighbours would underestimate it.
    private const float NoiseScale = 1.4826f;
    private const float NoiseFactor = 3f;

    // Horizontal, vertical and the two diagonals.
    private static ReadOnlySpan<int> DirectionX => [1, 0, 1, 1];
    private static ReadOnlySpan<int> DirectionY => [0, 1, 1, -1];

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> with every speck, its rim and the ring around
    /// it replaced by the per-channel median of the (2·size + 1)² window around each pixel that excludes speck and ring
    /// pixels. Alpha is kept.
    /// The rim grows from the detected core for at most <c>size</c> rounds: a neighbour of the speck joins when it
    /// differs from the median of its window in the same direction by more than half its limit.
    /// </summary>
    public static void Remove<TPixel>(TPixel[] source, TPixel[] destination, int width, int height, int size, float threshold,
        CancellationToken cancellationToken) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        int length = width * height;
        if (typeof(TPixel) == typeof(Rgba32))
        {
            Rgba32[] narrow = Unsafe.As<Rgba32[]>(source);
            byte[] luma = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                Parallelism.ForBands(height, Rows(width), length, cancellationToken, (top, bottom) =>
                {
                    for (int i = top * width; i < bottom * width; i++)
                    {
                        luma[i] = (byte)GrayWorld.Luma(narrow[i]);
                    }
                });
                Remove(source, destination, new BytePlane(luma), width, height, size, threshold, cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(luma);
            }
        }
        else
        {
            ushort[] luma = ArrayPool<ushort>.Shared.Rent(length);
            try
            {
                Parallelism.ForBands(height, Rows(width), length, cancellationToken, (top, bottom) =>
                {
                    for (int i = top * width; i < bottom * width; i++)
                    {
                        luma[i] = (ushort)GrayWorld.Luma(TPixel.ToRgba64(source[i]));
                    }
                });
                Remove(source, destination, new WidePlane(luma), width, height, size, threshold, cancellationToken);
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(luma);
            }
        }
    }

    private static int Rows(int width) => Math.Max(1, (1 << 16) / width);

    private static void Remove<TPixel, TPlane>(TPixel[] source, TPixel[] destination, TPlane luma, int width, int height, int size, float threshold,
        CancellationToken cancellationToken) where TPixel : unmanaged, IPixelOps<TPixel> where TPlane : struct, ILumaPlane
    {
        int length = width * height;
        byte[] mask = ArrayPool<byte>.Shared.Rent(length);
        byte[] next = ArrayPool<byte>.Shared.Rent(length);
        int scratchLength = TPlane.ScratchLength(size);
        try
        {
            source.AsSpan(0, length).CopyTo(destination);
            int rows = Rows(width);
            int tilesX = (width + Tile - 1) / Tile, tilesY = (height + Tile - 1) / Tile;
            float[] limits = new float[tilesX * tilesY];
            float floor = threshold * TPlane.Max;
            Parallelism.ForBands(tilesY, 1, length, cancellationToken, (top, bottom) =>
            {
                int[] counts = ArrayPool<int>.Shared.Rent(scratchLength);
                for (int ty = top; ty < bottom; ty++)
                {
                    for (int tx = 0; tx < tilesX; tx++)
                    {
                        limits[(ty * tilesX) + tx] = MathF.Max(floor, NoiseFactor * luma.TileNoise(width, height, tx, ty, counts));
                    }
                }

                ArrayPool<int>.Shared.Return(counts);
            });

            Parallelism.ForBands(height, rows, length, cancellationToken, (top, bottom) =>
            {
                for (int y = top; y < bottom; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float limit = limits[((y / Tile) * tilesX) + (x / Tile)];
                        mask[(y * width) + x] = Classify(luma, width, height, x, y, size, limit);
                    }
                }
            });

            // A speck's soft rim deviates less than its core. Each round reads the mask of the previous one, so bands
            // may run in any order; a neighbour that was examined and did not join is not examined again.
            for (int round = 0; round < size; round++)
            {
                mask.AsSpan(0, length).CopyTo(next);
                int added = 0;
                Parallelism.ForBands(height, rows, length, cancellationToken, (top, bottom) =>
                {
                    int[] counts = ArrayPool<int>.Shared.Rent(scratchLength);
                    int found = 0;
                    for (int y = top; y < bottom; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int i = (y * width) + x;
                            if (mask[i] != 0 || NeighbourSpecks(mask, width, height, x, y) is not (int specks and not 0))
                            {
                                continue;
                            }

                            float deviation = luma.Get(i) - luma.WindowMedian(width, height, x, y, size, counts);
                            float half = 0.5f * limits[((y / Tile) * tilesX) + (x / Tile)];
                            if ((specks & Bright) != 0 && deviation > half)
                            {
                                next[i] = Bright;
                                found++;
                            }
                            else if ((specks & Dark) != 0 && -deviation > half)
                            {
                                next[i] = Dark;
                                found++;
                            }
                            else
                            {
                                next[i] = Rejected;
                            }
                        }
                    }

                    Interlocked.Add(ref added, found);
                    ArrayPool<int>.Shared.Return(counts);
                });
                next.AsSpan(0, length).CopyTo(mask);
                if (added == 0)
                {
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            KeepSpecks(mask, luma, width, height, size, scratchLength);

            Parallelism.ForBands(height, rows, length, cancellationToken, (top, bottom) =>
            {
                int[] counts = ArrayPool<int>.Shared.Rent(scratchLength);
                for (int y = top; y < bottom; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width) + x;
                        if ((mask[i] & (SpeckMask | Ring)) != 0)
                        {
                            Replace(source, destination, mask, width, height, x, y, size, counts);
                        }
                    }
                }

                ArrayPool<int>.Shared.Return(counts);
            });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(mask);
            ArrayPool<byte>.Shared.Return(next);
        }
    }

    /// <summary>
    /// Whether the pixel exceeds the median of every segment through it by more than <paramref name="limit"/> (bright)
    /// or falls below it by more (dark). The median m is the element (n − 1) / 2 of the sorted segment, so
    /// m &lt; Y − limit exactly when at least (n − 1) / 2 + 1 values lie below Y − limit; counting needs no sorting.
    /// </summary>
    private static byte Classify<TPlane>(TPlane luma, int width, int height, int x, int y, int size, float limit) where TPlane : struct, ILumaPlane
    {
        int value = luma.Get((y * width) + x);
        float low = value - limit, high = value + limit;
        bool bright = true, dark = true;
        for (int d = 0; d < 4; d++)
        {
            int dx = DirectionX[d], dy = DirectionY[d];
            int n = 0, below = 0, above = 0;
            for (int k = -size; k <= size; k++)
            {
                int sx = x + (k * dx), sy = y + (k * dy);
                if ((uint)sx < (uint)width && (uint)sy < (uint)height)
                {
                    int v = luma.Get((sy * width) + sx);
                    n++;
                    below += v < low ? 1 : 0;
                    above += v > high ? 1 : 0;
                }
            }

            int rank = (n - 1) / 2;
            bright &= below >= rank + 1;
            dark &= above >= n - rank;
            if (!bright && !dark)
            {
                return 0;
            }
        }

        return (byte)((bright ? Bright : Dark) | Core);
    }

    /// <summary>
    /// Keeps only small, isolated specks, and marks the ring of pixels around each of them for replacement too, so no
    /// outline of its soft rim stays. Speck pixels form groups (8-connected, one polarity). The four segments miss
    /// detail that runs between their directions, such as a stroke of a slanted letter, so its pixels are detected as
    /// well; their group is not a speck when it extends beyond <paramref name="size"/> + 2 pixels (a speck and its
    /// rim), or when a pixel around it deviates from the median of its window in the same direction by more than half
    /// the largest deviation of the group's core: the group continues into larger detail, such as the rest of the
    /// letter. Such groups are kept as they are.
    /// </summary>
    private static void KeepSpecks<TPlane>(byte[] mask, TPlane luma, int width, int height, int size, int scratchLength) where TPlane : struct, ILumaPlane
    {
        int length = width * height, count = 0;
        for (int i = 0; i < length; i++)
        {
            count += (mask[i] & SpeckMask) != 0 ? 1 : 0;
        }

        if (count == 0)
        {
            return;
        }

        int[] counts = ArrayPool<int>.Shared.Rent(scratchLength);
        int[] pixels = ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (int seed = 0; seed < length; seed++)
            {
                int kind = mask[seed] & SpeckMask;
                if (kind == 0 || (mask[seed] & Visited) != 0)
                {
                    continue;
                }

                mask[seed] |= Visited | Member;
                pixels[0] = seed;
                int n = 1;
                int left = width, right = -1, top = height, bottom = -1;
                float peak = 0f;
                for (int k = 0; k < n; k++)
                {
                    int i = pixels[k], x = i % width, y = i / width;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    if ((mask[i] & Core) != 0)
                    {
                        float deviation = luma.Get(i) - luma.WindowMedian(width, height, x, y, size, counts);
                        peak = MathF.Max(peak, kind == Bright ? deviation : -deviation);
                    }

                    for (int sy = Math.Max(0, y - 1); sy <= Math.Min(height - 1, y + 1); sy++)
                    {
                        for (int sx = Math.Max(0, x - 1); sx <= Math.Min(width - 1, x + 1); sx++)
                        {
                            int j = (sy * width) + sx;
                            if ((mask[j] & (SpeckMask | Visited)) == kind)
                            {
                                mask[j] |= Visited | Member;
                                pixels[n++] = j;
                            }
                        }
                    }
                }

                bool speck = Math.Max(right - left, bottom - top) + 1 <= size + 2
                    && IsIsolated(mask, luma, width, height, size, pixels.AsSpan(0, n), kind, 0.5f * peak, counts);
                for (int k = 0; k < n; k++)
                {
                    int i = pixels[k], x = i % width, y = i / width;
                    mask[i] = speck ? (byte)(mask[i] & ~Member) : Visited;
                    for (int sy = Math.Max(0, y - 1); sy <= Math.Min(height - 1, y + 1); sy++)
                    {
                        for (int sx = Math.Max(0, x - 1); sx <= Math.Min(width - 1, x + 1); sx++)
                        {
                            int j = (sy * width) + sx;
                            mask[j] = (byte)(mask[j] & ~Checked);
                            if (speck && (mask[j] & SpeckMask) == 0)
                            {
                                mask[j] |= Ring;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pixels);
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    /// <summary>
    /// Whether no pixel around the group (outside it, 8-connected) deviates from the median of its window in the
    /// group's direction by more than <paramref name="limit"/>. Examined pixels are marked <see cref="Checked"/>.
    /// </summary>
    private static bool IsIsolated<TPlane>(byte[] mask, TPlane luma, int width, int height, int size, ReadOnlySpan<int> group,
        int kind, float limit, Span<int> counts) where TPlane : struct, ILumaPlane
    {
        foreach (int i in group)
        {
            int x = i % width, y = i / width;
            for (int sy = Math.Max(0, y - 1); sy <= Math.Min(height - 1, y + 1); sy++)
            {
                for (int sx = Math.Max(0, x - 1); sx <= Math.Min(width - 1, x + 1); sx++)
                {
                    int j = (sy * width) + sx;
                    if ((mask[j] & (Member | Checked)) != 0)
                    {
                        continue;
                    }

                    mask[j] |= Checked;
                    float deviation = luma.Get(j) - luma.WindowMedian(width, height, sx, sy, size, counts);
                    if ((kind == Bright ? deviation : -deviation) > limit)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static int NeighbourSpecks(byte[] mask, int width, int height, int x, int y)
    {
        int specks = 0;
        for (int sy = Math.Max(0, y - 1); sy <= Math.Min(height - 1, y + 1); sy++)
        {
            for (int sx = Math.Max(0, x - 1); sx <= Math.Min(width - 1, x + 1); sx++)
            {
                specks |= mask[(sy * width) + sx] & SpeckMask;
            }
        }

        return specks;
    }

    private static void Replace<TPixel>(TPixel[] source, TPixel[] destination, byte[] mask, int width, int height, int x, int y, int size,
        Span<int> counts) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        if (typeof(TPixel) != typeof(Rgba32))
        {
            ReplaceWide(source, destination, mask, width, height, x, y, size, counts);
            return;
        }

        counts = counts[..(3 * 256)];
        counts.Clear();
        int n = 0;
        for (int sy = Math.Max(0, y - size); sy <= Math.Min(height - 1, y + size); sy++)
        {
            for (int sx = Math.Max(0, x - size); sx <= Math.Min(width - 1, x + size); sx++)
            {
                int i = (sy * width) + sx;
                if ((mask[i] & (SpeckMask | Ring)) != 0)
                {
                    continue;
                }

                Rgba32 p = Unsafe.As<TPixel, Rgba32>(ref source[i]);
                counts[p.R]++;
                counts[256 + p.G]++;
                counts[512 + p.B]++;
                n++;
            }
        }

        if (n == 0)
        {
            return;
        }

        int rank = (n - 1) / 2;
        ref Rgba32 target = ref Unsafe.As<TPixel, Rgba32>(ref destination[(y * width) + x]);
        target = new Rgba32((byte)Select(counts[..256], rank), (byte)Select(counts.Slice(256, 256), rank),
            (byte)Select(counts.Slice(512, 256), rank), target.A);
    }

    // Replace on 16-bit pixels: the three channels of the window's kept pixels side by side, each sorted for its median.
    private static void ReplaceWide<TPixel>(TPixel[] source, TPixel[] destination, byte[] mask, int width, int height, int x, int y, int size,
        Span<int> values) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        int window = ((2 * size) + 1) * ((2 * size) + 1);
        Span<int> red = values[..window], green = values.Slice(window, window), blue = values.Slice(2 * window, window);
        int n = 0;
        for (int sy = Math.Max(0, y - size); sy <= Math.Min(height - 1, y + size); sy++)
        {
            for (int sx = Math.Max(0, x - size); sx <= Math.Min(width - 1, x + size); sx++)
            {
                int i = (sy * width) + sx;
                if ((mask[i] & (SpeckMask | Ring)) != 0)
                {
                    continue;
                }

                Rgba64 p = TPixel.ToRgba64(source[i]);
                (red[n], green[n], blue[n]) = (p.R, p.G, p.B);
                n++;
            }
        }

        if (n == 0)
        {
            return;
        }

        int rank = (n - 1) / 2;
        ref TPixel target = ref destination[(y * width) + x];
        target = TPixel.FromRgba64(new Rgba64((ushort)Selected(red[..n], rank), (ushort)Selected(green[..n], rank), (ushort)Selected(blue[..n], rank),
            TPixel.ToRgba64(target).A));
    }

    /// <summary>The value at <paramref name="rank"/> (0-based) of <paramref name="values"/> sorted; reorders them.</summary>
    private static int Selected(Span<int> values, int rank)
    {
        values.Sort();
        return values[rank];
    }

    /// <summary>The value at <paramref name="rank"/> (0-based) of the sorted values counted in <paramref name="counts"/>.</summary>
    private static int Select(ReadOnlySpan<int> counts, int rank)
    {
        int cumulative = 0;
        for (int v = 0; v < 256; v++)
        {
            cumulative += counts[v];
            if (cumulative > rank)
            {
                return v;
            }
        }

        return 255;
    }

    /// <summary>The luma plane the detection examines, with the medians of its depth.</summary>
    private interface ILumaPlane
    {
        /// <summary>The largest luma value: 255 or 65535.</summary>
        static abstract int Max { get; }

        /// <summary>Scratch integers that <see cref="WindowMedian"/>, <see cref="TileNoise"/> and <c>Replace</c> need for windows of (2·size + 1)² pixels.</summary>
        static abstract int ScratchLength(int size);

        int Get(int index);

        /// <summary>The median of the (2·size + 1)² window around (x, y), clipped to the plane.</summary>
        int WindowMedian(int width, int height, int x, int y, int size, Span<int> scratch);

        /// <summary>The noise σ of a tile: 1.4826 · the median of |Y − m| over its pixels, m the median of Y in the tile.</summary>
        float TileNoise(int width, int height, int tx, int ty, Span<int> scratch);
    }

    // 8-bit luma: medians from counts of the 256 values.
    private readonly struct BytePlane(byte[] luma) : ILumaPlane
    {
        public static int Max => 255;

        public static int ScratchLength(int size) => 3 * 256;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Get(int index) => luma[index];

        public int WindowMedian(int width, int height, int x, int y, int size, Span<int> scratch)
        {
            Span<int> counts = scratch[..256];
            counts.Clear();
            int n = 0;
            for (int sy = Math.Max(0, y - size); sy <= Math.Min(height - 1, y + size); sy++)
            {
                for (int sx = Math.Max(0, x - size); sx <= Math.Min(width - 1, x + size); sx++)
                {
                    counts[luma[(sy * width) + sx]]++;
                    n++;
                }
            }

            return Select(counts, (n - 1) / 2);
        }

        public float TileNoise(int width, int height, int tx, int ty, Span<int> scratch)
        {
            Span<int> counts = scratch[..256];
            int left = tx * Tile, right = Math.Min(width, left + Tile);
            int top = ty * Tile, bottom = Math.Min(height, top + Tile);
            int n = (right - left) * (bottom - top);
            counts.Clear();
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    counts[luma[(y * width) + x]]++;
                }
            }

            int median = Select(counts, (n - 1) / 2);
            counts.Clear();
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    counts[Math.Abs(luma[(y * width) + x] - median)]++;
                }
            }

            return NoiseScale * Select(counts, (n - 1) / 2);
        }
    }

    // 16-bit luma: medians by sorting the window's values.
    private readonly struct WidePlane(ushort[] luma) : ILumaPlane
    {
        public static int Max => ushort.MaxValue;

        public static int ScratchLength(int size) => Math.Max(Tile * Tile, 3 * ((2 * size) + 1) * ((2 * size) + 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Get(int index) => luma[index];

        public int WindowMedian(int width, int height, int x, int y, int size, Span<int> scratch)
        {
            int n = 0;
            for (int sy = Math.Max(0, y - size); sy <= Math.Min(height - 1, y + size); sy++)
            {
                for (int sx = Math.Max(0, x - size); sx <= Math.Min(width - 1, x + size); sx++)
                {
                    scratch[n++] = luma[(sy * width) + sx];
                }
            }

            return Selected(scratch[..n], (n - 1) / 2);
        }

        public float TileNoise(int width, int height, int tx, int ty, Span<int> scratch)
        {
            int left = tx * Tile, right = Math.Min(width, left + Tile);
            int top = ty * Tile, bottom = Math.Min(height, top + Tile);
            int n = 0;
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    scratch[n++] = luma[(y * width) + x];
                }
            }

            Span<int> values = scratch[..n];
            int median = Selected(values, (n - 1) / 2);
            foreach (ref int v in values)
            {
                v = Math.Abs(v - median);
            }

            return NoiseScale * Selected(values, (n - 1) / 2);
        }
    }
}
