using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RasterSharp.Formats.WebP.Lossless;

internal sealed partial class Vp8LBackwardRefs
{
    // Below this many pixels per segment the search runs sequentially.
    private const int MinSegmentPixels = 1 << 16;

    private static int Hash(uint first, uint second) => (int)(unchecked(second * 0xc6a4a793u + first * 0x5bd1e996u) >> 14);

    // port: src/enc/backward_references_enc.c VP8LHashChainFill
    // The best match of a position depends only on the position and the chain links below it, which the
    // sequential search, going down, has not overwritten yet; it therefore keeps the links in the result array.
    // The positions are searched from the end; each search also fills the positions below it that extend the
    // same match (propagation), and the next search starts where that stops. With several threads, segments
    // run this loop speculatively from their top and record where they searched. A sequential pass then
    // follows the true order from the top: once it reaches a position a segment also searched from, the rest
    // of that segment is identical, so it jumps to the segment's last search. The result equals the
    // sequential one. The segments overwrite links that others still read, so they need a copy of the chain.
    private void Fill(int quality, bool lowEffort, int maxDegreeOfParallelism)
    {
        _matches.AsSpan(0, _count).Clear();
        if (_count <= 2) return;
        int degree = maxDegreeOfParallelism == -1 ? Environment.ProcessorCount : maxDegreeOfParallelism;
        if (degree <= 1 || _count - 2 < 2 * MinSegmentPixels)
        {
            BuildChain(_matches);
            var sequential = new ChainSearch(this, _matches, quality, lowEffort);
            for (int start = _count - 2; start > 0;) start = sequential.Step(start, 1);
            return;
        }
        int[] chain = ArrayPool<int>.Shared.Rent(_count);
        try
        {
            BuildChain(chain);
            var search = new ChainSearch(this, chain, quality, lowEffort);
            // More segments than threads balance cores of different speed.
            int segments = Math.Min(4 * degree, (_count - 2) / MinSegmentPixels);
            // Segment k holds the positions [k * size, (k + 1) * size): whole 64-position words of 'searched',
            // which threads therefore never share. Positions 1 .. count - 2 are searched.
            int size = ((_count - 1) / segments + 63) & ~63;
            segments = (_count - 2) / size + 1;
            ulong[] searched = ArrayPool<ulong>.Shared.Rent((_count + 63) >> 6);
            int[] lastSearch = new int[segments];
            try
            {
                searched.AsSpan(0, (_count + 63) >> 6).Clear();
                Parallel.For(0, segments, new ParallelOptions { MaxDegreeOfParallelism = degree }, segment =>
                {
                    int low = Math.Max(1, segment * size), start = Math.Min(_count - 2, (segment + 1) * size - 1);
                    while (start >= low)
                    {
                        searched[start >> 6] |= 1UL << start;
                        lastSearch[segment] = start;
                        start = search.Step(start, low);
                    }
                });
                for (int start = _count - 2; start > 0;)
                {
                    if ((searched[start >> 6] & (1UL << start)) != 0) start = lastSearch[start / size];
                    start = search.Step(start, 1);
                }
            }
            finally { ArrayPool<ulong>.Shared.Return(searched); }
        }
        finally { ArrayPool<int>.Shared.Return(chain); }
    }

    // Links each position to the previous one with the same hash of its two pixels (or of a pixel and its run length).
    private void BuildChain(int[] chain)
    {
        int[] heads = ArrayPool<int>.Shared.Rent(1 << 18);
        heads.AsSpan(0, 1 << 18).Fill(-1);
        try
        {
            int pos = 0;
            bool equal = _pixels[0] == _pixels[1];
            while (pos < _count - 2)
            {
                bool next = _pixels[pos + 1] == _pixels[pos + 2];
                if (equal && next)
                {
                    int length = 1;
                    while (pos + length + 2 < _count && _pixels[pos + length + 2] == _pixels[pos]) length++;
                    if (length > MaxLength)
                    {
                        chain.AsSpan(pos, length - MaxLength).Fill(-1);
                        pos += length - MaxLength; length = MaxLength;
                    }
                    while (length > 0)
                    {
                        int hash = Hash(_pixels[pos], (uint)length--);
                        chain[pos] = heads[hash]; heads[hash] = pos++;
                    }
                    equal = false;
                }
                else
                {
                    int hash = Hash(_pixels[pos], _pixels[pos + 1]);
                    chain[pos] = heads[hash]; heads[hash] = pos++; equal = next;
                }
            }
            chain[pos] = heads[Hash(_pixels[pos], _pixels[pos + 1])];
        }
        finally { ArrayPool<int>.Shared.Return(heads); }
        chain[0] = chain[_count - 1] = 0;
    }

    private readonly struct ChainSearch(Vp8LBackwardRefs owner, int[] chain, int quality, bool lowEffort)
    {
        private readonly int _window = Math.Min(Window, quality > 75 ? Window : owner._width << (quality > 50 ? 8 : quality > 25 ? 6 : 4));
        private readonly int _iterations = 8 + quality * quality / 128;

        // Searches 'start', writes its match and the propagated ones at positions >= low,
        // and returns the next position to search.
        public int Step(int start, int low)
        {
            uint[] argb = owner._pixels;
            int[] matches = owner._matches;
            int count = owner._count, width = owner._width;
            int limit = Math.Min(MaxLength, count - 1 - start), iterations = _iterations;
            int best = 0, distance = 0, min = Math.Max(0, start - _window), pos = chain[start];
            if (!lowEffort)
            {
                if (start >= width) { best = owner.Match(start, start - width, 0, limit); if (best > 0) distance = width; iterations--; }
                int length = owner.Match(start, start - 1, best, limit);
                if (length > best) { best = length; distance = 1; }
                iterations--;
                if (best == MaxLength) pos = min - 1;
            }
            // The chain walk is bound by memory latency: compare the pixel that would extend the best match first
            // (best_argb in libwebp) and skip bounds checks; positions on the chain lie in [min, start).
            ref uint pixels = ref MemoryMarshal.GetArrayDataReference(argb);
            ref int links = ref MemoryMarshal.GetArrayDataReference(chain);
            uint bestPixel = Unsafe.Add(ref pixels, start + best);
            int goodLength = Math.Min(limit, 256);
            for (; pos >= min && --iterations != 0; pos = Unsafe.Add(ref links, pos))
            {
                if (Unsafe.Add(ref pixels, pos + best) != bestPixel) continue;
                int length = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref pixels, pos), limit)
                    .CommonPrefixLength(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref pixels, start), limit));
                if (length > best)
                {
                    best = length; distance = start - pos;
                    if (best >= goodLength) break;
                    bestPixel = Unsafe.Add(ref pixels, start + best);
                }
            }
            int maxStart = start;
            while (start >= low)
            {
                matches[start] = (distance << 12) | best;
                start--;
                if (distance == 0 || start == 0 || start < distance || argb[start - distance] != argb[start]) break;
                if (best == MaxLength && distance != 1 && start + MaxLength < maxStart) break;
                if (best < MaxLength) { best++; maxStart = start; }
            }
            return start;
        }
    }
}
