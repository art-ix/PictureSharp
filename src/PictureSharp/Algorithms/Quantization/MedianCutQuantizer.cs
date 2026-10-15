using System.Buffers;
using System.Numerics;

namespace PictureSharp.Algorithms.Quantization;

/// <summary>
/// Median-cut color quantization, ported from Pillow so that palettes and indices equal
/// <c>Image.quantize(colors, method=Quantize.MEDIANCUT)</c> (kmeans = 0). Pixels are packed RGBA with R in the low
/// byte; alpha is ignored.
/// </summary>
// port: libImaging/Quant.c quantize (Pillow 10.2.0)
internal static class MedianCutQuantizer
{
    private const int MaxHashEntries = 65536;

    private sealed class PixelNode
    {
        public readonly PixelNode?[] Next = new PixelNode?[3];
        public readonly PixelNode?[] Prev = new PixelNode?[3];
        public uint Pixel;
        public bool Flag;
        public int Count;

        public int Channel(int axis) => (int)((Pixel >> (8 * axis)) & 0xFF);
    }

    private sealed class BoxNode
    {
        public BoxNode? Left;
        public BoxNode? Right;
        public readonly PixelNode?[] Head = new PixelNode?[3];
        public readonly PixelNode?[] Tail = new PixelNode?[3];
        public int Volume = -1;
        public uint PixelCount;
    }

    /// <summary>Quantizes <paramref name="pixels"/> to at most <paramref name="colors"/> colors.</summary>
    /// <returns>The palette (opaque colors); <paramref name="indices"/> receives the palette index of each pixel.</returns>
    public static Rgba32[] Quantize(ReadOnlySpan<uint> pixels, int colors, Span<byte> indices)
    {
        QuantHashTable hash = CreatePixelHash(pixels);
        PixelNode?[] lists = new PixelNode?[3];

        // port: hash_to_list (each new node is pushed to the front of the three lists)
        int scale = hash.Scale;
        hash.ForEach((key, count) =>
        {
            var node = new PixelNode
            {
                Pixel = ((key & 0xFF) >> scale) | ((((key >> 8) & 0xFF) >> scale) << 8) | ((((key >> 16) & 0xFF) >> scale) << 16),
                Count = (int)count,
            };
            for (int i = 0; i < 3; i++)
            {
                node.Next[i] = lists[i];
                if (lists[i] is { } head)
                {
                    head.Prev[i] = node;
                }

                lists[i] = node;
            }
        });

        for (int i = 0; i < 3; i++)
        {
            lists[i] = MergeSort(lists[i], i);
        }

        BoxNode root = MedianCut(lists, (uint)pixels.Length, colors);
        uint entries = 0;
        AnnotateHashTable(root, hash, ref entries);
        Rgba32[] palette = ComputePaletteFromMedianCut(pixels, hash, (int)entries);
        MapPixelsFromMedianBox(pixels, palette, hash, indices);
        return palette;
    }

    // port: create_pixel_hash
    private static QuantHashTable CreatePixelHash(ReadOnlySpan<uint> pixels)
    {
        var hash = new QuantHashTable(scaled: true);
        foreach (uint pixel in pixels)
        {
            hash.Increment(pixel);
            while (hash.Count > MaxHashEntries)
            {
                hash.Scale++;
                hash.RehashMerging();
            }
        }

        return hash;
    }

    // port: mergesort_pixels (descending by the channel; not stable, ties take from the second half)
    private static PixelNode? MergeSort(PixelNode? head, int i)
    {
        if (head is null || head.Next[i] is null)
        {
            if (head is not null)
            {
                head.Next[i] = null;
                head.Prev[i] = null;
            }

            return head;
        }

        PixelNode? c = head, t = head;
        while (c is not null && t is not null)
        {
            c = c.Next[i];
            t = t.Next[i]?.Next[i];
        }

        if (c is not null)
        {
            if (c.Prev[i] is { } before)
            {
                before.Next[i] = null;
            }

            c.Prev[i] = null;
        }

        PixelNode? a = MergeSort(head, i);
        PixelNode? b = MergeSort(c, i);
        head = null;
        PixelNode? p = null;
        while (a is not null && b is not null)
        {
            if (a.Channel(i) > b.Channel(i))
            {
                c = a;
                a = a.Next[i];
            }
            else
            {
                c = b;
                b = b.Next[i];
            }

            c.Prev[i] = p;
            c.Next[i] = null;
            if (p is not null)
            {
                p.Next[i] = c;
            }

            p = c;
            head ??= c;
        }

        if (a is not null)
        {
            c!.Next[i] = a;
            a.Prev[i] = c;
        }
        else if (b is not null)
        {
            c!.Next[i] = b;
            b.Prev[i] = c;
        }

        return head;
    }

    // port: compute_box_volume
    private static int BoxVolume(BoxNode b)
    {
        if (b.Volume >= 0)
        {
            return b.Volume;
        }

        if (b.Head[0] is null)
        {
            b.Volume = 0;
        }
        else
        {
            int rh = b.Head[0]!.Channel(0), rl = b.Tail[0]!.Channel(0);
            int gh = b.Head[1]!.Channel(1), gl = b.Tail[1]!.Channel(1);
            int bh = b.Head[2]!.Channel(2), bl = b.Tail[2]!.Channel(2);
            b.Volume = (rh - rl + 1) * (gh - gl + 1) * (bh - bl + 1);
        }

        return b.Volume;
    }

    // port: median_cut with the QuantHeap.c priority queue (largest pixel count first)
    private static BoxNode MedianCut(PixelNode?[] heads, uint pixelCount, int colors)
    {
        var root = new BoxNode { PixelCount = pixelCount };
        for (int i = 0; i < 3; i++)
        {
            PixelNode? tail = heads[i];
            while (tail?.Next[i] is not null)
            {
                tail = tail.Next[i];
            }

            root.Head[i] = heads[i];
            root.Tail[i] = tail;
        }

        var heap = new BoxHeap();
        heap.Add(root);
        while (--colors > 0)
        {
            BoxNode node;
            do
            {
                if (!heap.TryRemove(out node))
                {
                    return root;
                }
            }
            while (BoxVolume(node) == 1);

            Split(node);
            heap.Add(node.Left!);
            heap.Add(node.Right!);
        }

        return root;
    }

    // port: split
    private static void Split(BoxNode node)
    {
        int rh = node.Head[0]!.Channel(0), rl = node.Tail[0]!.Channel(0);
        int gh = node.Head[1]!.Channel(1), gl = node.Tail[1]!.Channel(1);
        int bh = node.Head[2]!.Channel(2), bl = node.Tail[2]!.Channel(2);
        Span<int> f = [(rh - rl) * 77, (gh - gl) * 150, (bh - bl) * 29];
        int best = f[0];
        int axis = 0;
        for (int i = 1; i < 3; i++)
        {
            if (best < f[i])
            {
                best = f[i];
                axis = i;
            }
        }

        SplitLists(node, axis, out PixelNode?[] leftHeads, out PixelNode?[] leftTails, out PixelNode?[] rightHeads,
            out PixelNode?[] rightTails, out uint leftCount, out uint rightCount);
        var left = new BoxNode { PixelCount = leftCount };
        var right = new BoxNode { PixelCount = rightCount };
        for (int i = 0; i < 3; i++)
        {
            left.Head[i] = leftHeads[i];
            left.Tail[i] = leftTails[i];
            right.Head[i] = rightHeads[i];
            right.Tail[i] = rightTails[i];
            node.Head[i] = null;
            node.Tail[i] = null;
        }

        node.Left = left;
        node.Right = right;
    }

    // port: splitlists
    private static void SplitLists(BoxNode node, int axis, out PixelNode?[] leftHeads, out PixelNode?[] leftTails,
        out PixelNode?[] rightHeads, out PixelNode?[] rightTails, out uint leftCount, out uint rightCount)
    {
        PixelNode?[] h = node.Head;
        leftCount = rightCount = 0;
        int nRight = 0;
        uint left = 0;
        PixelNode? c;
        for (c = h[axis]; c is not null;)
        {
            left += (uint)c.Count;
            leftCount += (uint)c.Count;
            c.Flag = false;
            c = c.Next[axis];
            if (left * 2 > node.PixelCount)
            {
                break;
            }
        }

        if (c is not null)
        {
            int splitValue = c.Prev[axis]!.Channel(axis);
            for (; c is not null; c = c.Next[axis])
            {
                if (splitValue != c.Channel(axis))
                {
                    break;
                }

                c.Flag = false;
                leftCount += (uint)c.Count;
            }
        }

        for (; c is not null; c = c.Next[axis])
        {
            c.Flag = true;
            nRight++;
            rightCount += (uint)c.Count;
        }

        if (nRight == 0)
        {
            int splitValue = node.Tail[axis]!.Channel(axis);
            for (c = node.Tail[axis]; c is not null; c = c.Prev[axis])
            {
                if (splitValue != c.Channel(axis))
                {
                    break;
                }

                c.Flag = true;
                leftCount -= (uint)c.Count;
                rightCount += (uint)c.Count;
            }
        }

        leftHeads = new PixelNode?[3];
        leftTails = new PixelNode?[3];
        rightHeads = new PixelNode?[3];
        rightTails = new PixelNode?[3];
        for (int i = 0; i < 3; i++)
        {
            PixelNode? l = null, r = null, n;
            for (c = h[i]; c is not null; c = n)
            {
                n = c.Next[i];
                if (c.Flag)
                {
                    if (r is not null)
                    {
                        r.Next[i] = c;
                    }
                    else
                    {
                        rightHeads[i] = c;
                    }

                    c.Prev[i] = r;
                    r = c;
                }
                else
                {
                    if (l is not null)
                    {
                        l.Next[i] = c;
                    }
                    else
                    {
                        leftHeads[i] = c;
                    }

                    c.Prev[i] = l;
                    l = c;
                }
            }

            if (l is not null)
            {
                l.Next[i] = null;
            }

            if (r is not null)
            {
                r.Next[i] = null;
            }

            leftTails[i] = l;
            rightTails[i] = r;
        }
    }

    // port: annotate_hash_table (left subtree first; leaves number their boxes in that order)
    private static void AnnotateHashTable(BoxNode n, QuantHashTable hash, ref uint box)
    {
        if (n.Left is not null && n.Right is not null)
        {
            AnnotateHashTable(n.Left, hash, ref box);
            AnnotateHashTable(n.Right, hash, ref box);
            return;
        }

        int scale = hash.Scale;
        for (PixelNode? p = n.Head[0]; p is not null; p = p.Next[0])
        {
            uint q = ((p.Pixel & 0xFF) << scale) | ((((p.Pixel >> 8) & 0xFF) << scale) << 8) | ((((p.Pixel >> 16) & 0xFF) << scale) << 16);
            hash.Insert(q & 0x00FFFFFF, box);
        }

        if (n.Head[0] is not null)
        {
            box++;
        }
    }

    // port: compute_palette_from_median_cut (32-bit sums wrap around as in C)
    private static Rgba32[] ComputePaletteFromMedianCut(ReadOnlySpan<uint> pixels, QuantHashTable hash, int entries)
    {
        var count = new uint[entries];
        var sums = new uint[3 * entries];
        foreach (uint pixel in pixels)
        {
            if (!hash.TryGetValue(pixel, out uint entry) || entry >= entries)
            {
                throw new InvalidOperationException("Median cut lookup failed.");
            }

            sums[3 * entry] += pixel & 0xFF;
            sums[(3 * entry) + 1] += (pixel >> 8) & 0xFF;
            sums[(3 * entry) + 2] += (pixel >> 16) & 0xFF;
            count[entry]++;
        }

        var palette = new Rgba32[entries];
        for (int i = 0; i < entries; i++)
        {
            palette[i] = new Rgba32(Average(sums[3 * i], count[i]), Average(sums[(3 * i) + 1], count[i]), Average(sums[(3 * i) + 2], count[i]));
        }

        return palette;
    }

    private static byte Average(uint sum, uint count) => (byte)(int)(.5 + ((double)sum / count));

    // port: build_distance_tables + map_image_pixels_from_median_box
    private static void MapPixelsFromMedianBox(ReadOnlySpan<uint> pixels, Rgba32[] palette, QuantHashTable boxes, Span<byte> indices)
    {
        int n = palette.Length;
        var distance = new uint[n * n];
        var sortKey = new int[n * n];   // sortKey[i * n + k] = the k-th nearest entry to entry i
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < i; j++)
            {
                distance[(j * n) + i] = distance[(i * n) + j] = DistanceSquared(palette[i], palette[j]);
            }
        }

        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                order[j] = j;
            }

            // _distance_index_cmp is a total order (distance, then index), so any sort gives the same result.
            int row = i * n;
            Array.Sort(order, (a, b) => distance[row + a] != distance[row + b] ? distance[row + a].CompareTo(distance[row + b]) : a.CompareTo(b));
            order.CopyTo(sortKey, row);
        }

        // The match depends only on the pixel, so a fixed direct-mapped cache gives the same indices as Pillow's
        // per-color cache while its memory stays bounded (a collision only recomputes the match).
        // Sized to the image (2^12–2^20 entries): large photos have millions of colors and recompute misses.
        int cacheBits = Math.Clamp(BitOperations.Log2((uint)Math.Max(1, pixels.Length)) + 1, 12, 20);
        int cacheSize = 1 << cacheBits;
        uint[] cacheKeys = ArrayPool<uint>.Shared.Rent(cacheSize);
        byte[] cacheValues = ArrayPool<byte>.Shared.Rent(cacheSize);
        cacheKeys.AsSpan(0, cacheSize).Fill(uint.MaxValue);   // never a 24-bit pixel
        try
        {
            MapPixels(pixels, palette, boxes, indices, distance, sortKey, cacheBits, cacheKeys, cacheValues);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(cacheKeys);
            ArrayPool<byte>.Shared.Return(cacheValues);
        }
    }

    private static void MapPixels(ReadOnlySpan<uint> pixels, Rgba32[] palette, QuantHashTable boxes, Span<byte> indices,
        uint[] distance, int[] sortKey, int cacheBits, uint[] cacheKeys, byte[] cacheValues)
    {
        int n = palette.Length;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint pixel = pixels[i] & 0x00FFFFFF;
            int slot = (int)((pixel * 2654435761u) >> (32 - cacheBits));
            if (cacheKeys[slot] == pixel)
            {
                indices[i] = cacheValues[slot];
                continue;
            }

            if (!boxes.TryGetValue(pixel, out uint box))
            {
                throw new InvalidOperationException("Median cut lookup failed.");
            }

            var color = new Rgba32((byte)pixel, (byte)(pixel >> 8), (byte)(pixel >> 16));
            uint initial = DistanceSquared(palette[box], color);
            uint bestDistance = initial;
            int bestMatch = (int)box;
            initial <<= 2;
            int row = (int)box * n;
            for (int j = 0; j < n; j++)
            {
                int index = sortKey[row + j];
                if (distance[row + index] > initial)
                {
                    break;
                }

                uint d = DistanceSquared(palette[index], color);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestMatch = index;
                }
            }

            indices[i] = (byte)bestMatch;
            cacheKeys[slot] = pixel;
            cacheValues[slot] = (byte)bestMatch;
        }
    }

    internal static uint DistanceSquared(Rgba32 a, Rgba32 b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return (uint)((dr * dr) + (dg * dg) + (db * db));
    }

    /// <summary>Binary max-heap on the pixel count, with QuantHeap.c's exact sift rules.</summary>
    // port: libImaging/QuantHeap.c
    private sealed class BoxHeap
    {
        private BoxNode[] _heap = new BoxNode[256];
        private int _count;

        private static int Compare(BoxNode a, BoxNode b) => (int)a.PixelCount - (int)b.PixelCount;

        public void Add(BoxNode value)
        {
            if (_count == _heap.Length - 1)
            {
                Array.Resize(ref _heap, _heap.Length * 2);
            }

            int k = ++_count;
            while (k != 1)
            {
                if (Compare(value, _heap[k / 2]) <= 0)
                {
                    break;
                }

                _heap[k] = _heap[k / 2];
                k >>= 1;
            }

            _heap[k] = value;
        }

        public bool TryRemove(out BoxNode result)
        {
            if (_count == 0)
            {
                result = null!;
                return false;
            }

            result = _heap[1];
            BoxNode v = _heap[_count--];
            int k, l;
            for (k = 1; k * 2 <= _count; k = l)
            {
                l = k * 2;
                if (l < _count && Compare(_heap[l], _heap[l + 1]) < 0)
                {
                    l++;
                }

                if (Compare(v, _heap[l]) > 0)
                {
                    break;
                }

                _heap[k] = _heap[l];
            }

            _heap[k] = v;
            return true;
        }
    }
}
