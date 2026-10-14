using System.Buffers;
using System.Numerics;

namespace RasterSharp.Formats.WebP.Lossless;

internal readonly record struct Vp8LToken(uint Value, int Length, int Kind)
{
    public static Vp8LToken Literal(uint pixel) => new(pixel, 1, 0);
    public static Vp8LToken Copy(int distance, int length) => new((uint)distance, length, 1);
    public static Vp8LToken Cache(int index) => new((uint)index, 1, 2);
}

internal sealed class Vp8LRefs(int capacity) : IDisposable
{
    private readonly Vp8LToken[] _tokens = ArrayPool<Vp8LToken>.Shared.Rent(capacity);
    public int Count { get; private set; }
    public ReadOnlySpan<Vp8LToken> Tokens => _tokens.AsSpan(0, Count);
    public void Add(Vp8LToken token) => _tokens[Count++] = token;

    public Vp8LRefs Clone()
    {
        var copy = new Vp8LRefs(Math.Max(Count, 1));
        Tokens.CopyTo(copy._tokens);
        copy.Count = Count;
        return copy;
    }
    public void Dispose() => ArrayPool<Vp8LToken>.Shared.Return(_tokens);
}

internal sealed class Vp8LBackwardRefs : IDisposable
{
    private readonly int[] _matches;
    private readonly uint[] _pixels;
    private readonly int _count;
    private readonly int _width;
    private const int MaxLength = 4095;
    private const int Window = (1 << 20) - 120;
    private static ReadOnlySpan<byte> PlaneToCode =>
    [
        96,73,55,39,23,13,5,1,255,255,255,255,255,255,255,255,
        101,78,58,42,26,16,8,2,0,3,9,17,27,43,59,79,
        102,86,62,46,32,20,10,6,4,7,11,21,33,47,63,87,
        105,90,70,52,37,28,18,14,12,15,19,29,38,53,71,91,
        110,99,82,66,48,35,30,24,22,25,31,36,49,67,83,100,
        115,108,94,76,64,50,44,40,34,41,45,51,65,77,95,109,
        118,113,103,92,80,68,60,56,54,57,61,69,81,93,104,114,
        119,116,111,106,97,88,84,74,72,75,85,89,98,107,112,117,
    ];

    // port: src/enc/backward_references_enc.c VP8LDistanceToPlaneCode
    public static int PlaneCode(int width, int distance)
    {
        int y = distance / width, x = distance - y * width;
        if (x <= 8 && y < 8) return PlaneToCode[y * 16 + 8 - x] + 1;
        if (x > width - 8 && y < 7) return PlaneToCode[(y + 1) * 16 + 8 + width - x] + 1;
        return distance + 120;
    }

    // port: src/dsp/lossless_common.h VP8LPrefixEncode
    public static (int Code, int Bits, int Extra) Prefix(int value)
    {
        if (value <= 4) return (value - 1, 0, 0);
        int v = value - 1, highest = BitOperations.Log2((uint)v);
        int bits = highest - 1;
        return (2 * highest + ((v >> bits) & 1), bits, v & ((1 << bits) - 1));
    }

    public Vp8LBackwardRefs(uint[] pixels, int width, int height, int quality, bool lowEffort, bool box = false)
    {
        _pixels = pixels; _width = width; _count = width * height;
        _matches = ArrayPool<int>.Shared.Rent(_count);
        Fill(quality, lowEffort);
        if (box) FillBox();
    }

    private static int Hash(uint first, uint second) => (int)(unchecked(second * 0xc6a4a793u + first * 0x5bd1e996u) >> 14);
    private int Length(int pos) => _matches[pos] & 4095;
    private int Distance(int pos) => (int)((uint)_matches[pos] >> 12);
    private int Match(int a, int b, int best, int limit)
    {
        if (_pixels[a + best] != _pixels[b + best]) return 0;
        int length = 0;
        while (length < limit && _pixels[a + length] == _pixels[b + length]) length++;
        return length;
    }

    // port: src/enc/backward_references_enc.c VP8LHashChainFill
    private void Fill(int quality, bool lowEffort, bool box = false)
    {
        _matches.AsSpan(0, _count).Clear();
        if (_count <= 2) return;
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
                        _matches.AsSpan(pos, length - MaxLength).Fill(-1);
                        pos += length - MaxLength; length = MaxLength;
                    }
                    while (length > 0)
                    {
                        int hash = Hash(_pixels[pos], (uint)length--);
                        _matches[pos] = heads[hash]; heads[hash] = pos++;
                    }
                    equal = false;
                }
                else
                {
                    int hash = Hash(_pixels[pos], _pixels[pos + 1]);
                    _matches[pos] = heads[hash]; heads[hash] = pos++; equal = next;
                }
            }
            _matches[pos] = heads[Hash(_pixels[pos], _pixels[pos + 1])];
        }
        finally { ArrayPool<int>.Shared.Return(heads); }
        int window = Math.Min(Window, quality > 75 ? Window : _width << (quality > 50 ? 8 : quality > 25 ? 6 : 4));
        _matches[0] = _matches[_count - 1] = 0;
        for (int start = _count - 2; start > 0;)
        {
            int limit = Math.Min(MaxLength, _count - 1 - start), iterations = 8 + quality * quality / 128;
            int best = 0, distance = 0, min = Math.Max(0, start - window), pos = _matches[start];
            if (!lowEffort)
            {
                if (start >= _width) { best = Match(start, start - _width, 0, limit); if (best > 0) distance = _width; iterations--; }
                int length = Match(start, start - 1, best, limit);
                if (length > best) { best = length; distance = 1; }
                iterations--;
                if (best == MaxLength) pos = min - 1;
            }
            for (; pos >= min && --iterations != 0; pos = _matches[pos])
            {
                int length = Match(start, pos, best, limit);
                if (length > best)
                {
                    best = length; distance = start - pos;
                    if (best >= Math.Min(limit, 256)) break;
                }
            }
            int maxStart = start;
            while (true)
            {
                _matches[start] = (distance << 12) | best;
                start--;
                if (distance == 0 || start == 0 || start < distance || _pixels[start - distance] != _pixels[start]) break;
                if (best == MaxLength && distance != 1 && start + MaxLength < maxStart) break;
                if (best < MaxLength) { best++; maxStart = start; }
            }
        }
    }

    // port: src/enc/backward_references_enc.c BackwardReferencesLz77, BackwardReferencesRle
    public Vp8LRefs Greedy(bool rle)
    {
        var refs = new Vp8LRefs(_count);
        int lastCheck = -1;
        for (int i = 0; i < _count;)
        {
            int length = Length(i), distance = Distance(i);
            if (rle)
            {
                int limit = Math.Min(MaxLength, _count - i);
                int run = i > 0 ? Match(i, i - 1, 0, limit) : 0;
                int above = i >= _width ? Match(i, i - _width, 0, limit) : 0;
                length = Math.Max(run, above); distance = run >= above ? 1 : _width;
                if (length < 4) length = 1;
            }
            else if (length >= 4)
            {
                int last = Math.Min(i + length, _count - 1), reach = 0;
                lastCheck = Math.Max(i, lastCheck);
                for (int j = lastCheck + 1; j <= last; j++)
                {
                    int extent = j + Math.Max(Length(j) >= 4 ? Length(j) : 1, 1);
                    if (extent > reach) { length = j - i; reach = extent; if (reach >= _count) break; }
                }
                lastCheck = i + length - 1;
            }
            else length = 1;
            refs.Add(length == 1 ? Vp8LToken.Literal(_pixels[i]) : Vp8LToken.Copy(distance, length));
            i += length;
        }
        return refs;
    }

    // port: src/enc/backward_references_enc.c BackwardReferencesLz77Box
    private void FillBox()
    {
        if (_count < 2) return;
        ushort[] runs = ArrayPool<ushort>.Shared.Rent(_count);
        Span<int> offsets = stackalloc int[32], newOffsets = stackalloc int[32]; offsets.Clear();
        try
        {
            runs[_count - 1] = 1;
            for (int i = _count - 2; i >= 0; i--) runs[i] = _pixels[i] == _pixels[i + 1] ? (ushort)Math.Min(runs[i + 1] + 1, MaxLength) : (ushort)1;
            for (int y = 0; y <= 6; y++)
            for (int x = -6; x <= 6; x++)
            {
                int offset = y * _width + x;
                if (offset <= 0) continue;
                int code = PlaneCode(_width, offset) - 1;
                if (code < 32) offsets[code] = offset;
            }
            int count = 0, newCount = 0;
            for (int i = 0; i < 32; i++) if (offsets[i] != 0) offsets[count++] = offsets[i];
            for (int i = 0; i < count; i++)
            {
                bool reachable = false;
                for (int j = 0; j < count; j++) reachable |= offsets[i] == offsets[j] + 1;
                if (!reachable) newOffsets[newCount++] = offsets[i];
            }
            int previousOffset = -1, previousLength = -1;
            _matches[0] = 0;
            for (int i = 1; i < _count; i++)
            {
                int bestLength = Length(i), bestOffset = Distance(i);
                bool compute = true;
                if (bestLength >= MaxLength)
                    for (int j = 0; j < count; j++) if (bestOffset == offsets[j]) { compute = false; break; }
                if (compute)
                {
                    bool usePrevious = previousLength > 1 && previousLength < MaxLength;
                    ReadOnlySpan<int> candidates = usePrevious ? newOffsets[..newCount] : offsets[..count];
                    bestLength = usePrevious ? previousLength - 1 : 0; bestOffset = usePrevious ? previousOffset : 0;
                    foreach (int offset in candidates)
                    {
                        int start = i - offset;
                        if (start < 0 || _pixels[start] != _pixels[i]) continue;
                        int length = 0, current = i;
                        do
                        {
                            int a = runs[start], b = runs[current];
                            if (a != b) { length += Math.Min(a, b); break; }
                            length += a; start += a; current += a;
                        } while (length <= MaxLength && current < _count && _pixels[start] == _pixels[current]);
                        if (bestLength < length)
                        {
                            bestOffset = offset; bestLength = Math.Min(length, MaxLength);
                            if (length >= MaxLength) break;
                        }
                    }
                }
                if (bestLength <= 4) { _matches[i] = 0; previousOffset = previousLength = 0; }
                else { _matches[i] = (bestOffset << 12) | bestLength; previousOffset = bestOffset; previousLength = bestLength; }
            }
        }
        finally { ArrayPool<ushort>.Shared.Return(runs); }
    }

    // port: src/enc/backward_references_enc.c BackwardRefsWithLocalCache
    public Vp8LRefs WithCache(Vp8LRefs source, int bits)
    {
        var result = new Vp8LRefs(source.Count);
        Span<uint> cache = stackalloc uint[1 << bits]; cache.Clear();
        int pos = 0;
        foreach (Vp8LToken token in source.Tokens)
        {
            int key = bits == 0 ? 0 : (int)(unchecked(_pixels[pos] * 0x1e35a7bdu) >> (32 - bits));
            result.Add(bits > 0 && token.Kind == 0 && cache[key] == token.Value ? Vp8LToken.Cache(key) : token);
            if (bits > 0)
            {
                for (int i = 0; i < token.Length; i++)
                {
                    uint pixel = _pixels[pos + i];
                    cache[(int)(unchecked(pixel * 0x1e35a7bdu) >> (32 - bits))] = pixel;
                }
            }
            pos += token.Length;
        }
        return result;
    }

    // port: src/enc/backward_references_cost_enc.c VP8LBackwardReferencesTraceBackwards, BackwardReferencesHashChainDistanceOnly,
    // TraceBackwards, BackwardReferencesHashChainFollowChosenPath, CostModelBuild, AddSingleLiteralWithCostModel
    public Vp8LRefs Trace(Vp8LRefs source, int cacheBits)
    {
        var histogram = new Vp8LHistogram(cacheBits);
        foreach (Vp8LToken token in source.Tokens) histogram.Add(token, _width);
        long[][] model = histogram.Costs();
        long[] costs = ArrayPool<long>.Shared.Rent(_count);
        ushort[] distArray = ArrayPool<ushort>.Shared.Rent(_count);
        uint[] cache = new uint[cacheBits > 0 ? 1 << cacheBits : 0];
        try
        {
            var manager = new Vp8LCostManager(costs, distArray, _count, model[0]);
            int offsetPrevious = -1, lengthPrevious = -1, firstOffsetIsConstant = -1, reach = 0;
            long offsetCost = -1;
            distArray[0] = 0;
            AddLiteral(0, 0);
            for (int i = 1; i < _count; i++)
            {
                long previousCost = costs[i - 1];
                int offset = Distance(i), length = Length(i);
                AddLiteral(i, previousCost);
                if (length >= 2)
                {
                    if (offset != offsetPrevious)
                    {
                        offsetCost = DistanceCost(PlaneCode(_width, offset));
                        firstOffsetIsConstant = 1;
                        manager.PushInterval(previousCost + offsetCost, i, length);
                    }
                    else
                    {
                        // Consecutive pixels with the same offset (e.g. constant regions) only need the
                        // contributions that reach further than the previous ones.
                        if (firstOffsetIsConstant == 1)
                        {
                            reach = i - 1 + lengthPrevious - 1;
                            firstOffsetIsConstant = 0;
                        }
                        if (i + length - 1 > reach)
                        {
                            int offsetJ, lengthJ = 0, j;
                            // Find the last consecutive pixel within [i, reach + 1] with the same offset.
                            for (j = i; j <= reach; j++)
                            {
                                offsetJ = Distance(j + 1); lengthJ = Length(j + 1);
                                if (offsetJ != offset)
                                {
                                    lengthJ = Length(j);
                                    break;
                                }
                            }
                            manager.UpdateCostAtIndex(j - 1, false);
                            manager.UpdateCostAtIndex(j, false);
                            manager.PushInterval(costs[j - 1] + offsetCost, j, lengthJ);
                            reach = j + lengthJ - 1;
                        }
                    }
                }
                manager.UpdateCostAtIndex(i, true);
                offsetPrevious = offset;
                lengthPrevious = length;
            }
            return FollowChosenPath(distArray, cacheBits);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(costs);
            ArrayPool<ushort>.Shared.Return(distArray);
        }

        void AddLiteral(int index, long previousCost)
        {
            long cost = previousCost;
            uint color = _pixels[index];
            int key = cacheBits > 0 ? (int)(unchecked(color * 0x1e35a7bdu) >> (32 - cacheBits)) : 0;
            if (cacheBits > 0 && cache[key] == color)
            {
                cost += Vp8LEntropy.DivideRound(model[0][280 + key] * 68, 100);
            }
            else
            {
                if (cacheBits > 0) cache[key] = color;
                cost += Vp8LEntropy.DivideRound((model[3][color >> 24] + model[1][(color >> 16) & 255] + model[0][(color >> 8) & 255] + model[2][color & 255]) * 82, 100);
            }
            if (costs[index] > cost)
            {
                costs[index] = cost;
                distArray[index] = 1;
            }
        }

        long DistanceCost(int distance)
        {
            var (code, bits, _) = Prefix(distance);
            return model[4][code] + ((long)bits << 23);
        }
    }

    // port: src/enc/backward_references_cost_enc.c TraceBackwards, BackwardReferencesHashChainFollowChosenPath
    private Vp8LRefs FollowChosenPath(ushort[] distArray, int cacheBits)
    {
        int[] path = ArrayPool<int>.Shared.Rent(_count);
        try
        {
            int size = 0;
            for (int current = _count - 1; current >= 0; current -= distArray[current]) path[size++] = distArray[current];
            var refs = new Vp8LRefs(size);
            uint[] cache = new uint[cacheBits > 0 ? 1 << cacheBits : 0];
            int i = 0;
            for (int n = size - 1; n >= 0; n--)
            {
                int length = path[n];
                if (length != 1)
                {
                    refs.Add(Vp8LToken.Copy(Distance(i), length));
                    if (cacheBits > 0)
                        for (int k = 0; k < length; k++) cache[(int)(unchecked(_pixels[i + k] * 0x1e35a7bdu) >> (32 - cacheBits))] = _pixels[i + k];
                    i += length;
                }
                else
                {
                    uint pixel = _pixels[i];
                    int key = cacheBits > 0 ? (int)(unchecked(pixel * 0x1e35a7bdu) >> (32 - cacheBits)) : 0;
                    if (cacheBits > 0 && cache[key] == pixel) refs.Add(Vp8LToken.Cache(key));
                    else
                    {
                        if (cacheBits > 0) cache[key] = pixel;
                        refs.Add(Vp8LToken.Literal(pixel));
                    }
                    i++;
                }
            }
            return refs;
        }
        finally { ArrayPool<int>.Shared.Return(path); }
    }

    public void Dispose() => ArrayPool<int>.Shared.Return(_matches);
}
