namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

/// <summary>
/// Keeps, for every pixel, the cheapest way to reach it as a copy, using intervals of constant cost
/// instead of MAX_LENGTH comparisons per pixel.
/// </summary>
// port: src/enc/backward_references_cost_enc.c CostManager, CostInterval, CostCacheInterval
// Intervals live in index-linked arrays; which free slot is reused does not affect the result.
internal sealed class Vp8LCostManager
{
    private const int CacheIntervalSizeMax = 500, SkipDistance = 10, MaxLength = 4095;

    private readonly long[] _costCache;
    private readonly long[] _cacheCost;
    private readonly int[] _cacheStart, _cacheEnd;
    private readonly int _cacheIntervals;

    private readonly long[] _cost = new long[CacheIntervalSizeMax];
    private readonly int[] _start = new int[CacheIntervalSizeMax], _end = new int[CacheIntervalSizeMax], _index = new int[CacheIntervalSizeMax];
    private readonly int[] _previous = new int[CacheIntervalSizeMax], _next = new int[CacheIntervalSizeMax];
    private readonly int[] _free = new int[CacheIntervalSizeMax];
    private int _freeCount, _head = -1, _count;

    public long[] Costs { get; }
    public ushort[] DistArray { get; }

    // port: src/enc/backward_references_cost_enc.c CostManagerInit
    public Vp8LCostManager(long[] costs, ushort[] distArray, int pixelCount, long[] literalCosts)
    {
        Costs = costs;
        DistArray = distArray;
        for (int i = CacheIntervalSizeMax - 1; i >= 0; i--) _free[_freeCount++] = i;
        int cacheSize = Math.Min(pixelCount, MaxLength);
        _costCache = new long[cacheSize];
        // libwebp caches GetLengthCost(k) for a copy of length k + 1; this off-by-one is part of its cost model.
        for (int i = 0; i < cacheSize; i++)
        {
            var (code, bits, _) = i == 0 ? (0, 0, 0) : Vp8LBackwardRefs.Prefix(i);
            _costCache[i] = literalCosts[256 + code] + ((long)bits << 23);
        }
        int intervals = 1;
        for (int i = 1; i < cacheSize; i++) if (_costCache[i] != _costCache[i - 1]) intervals++;
        _cacheIntervals = intervals;
        _cacheCost = new long[intervals];
        _cacheStart = new int[intervals];
        _cacheEnd = new int[intervals];
        int current = 0;
        _cacheStart[0] = 0; _cacheEnd[0] = 1; _cacheCost[0] = _costCache[0];
        for (int i = 1; i < cacheSize; i++)
        {
            if (_costCache[i] != _cacheCost[current])
            {
                current++;
                _cacheStart[current] = i;
                _cacheCost[current] = _costCache[i];
            }
            _cacheEnd[current] = i + 1;
        }
        costs.AsSpan(0, pixelCount).Fill(long.MaxValue);
    }

    // port: src/enc/backward_references_cost_enc.c UpdateCost
    private void UpdateCost(int i, int position, long cost)
    {
        if (Costs[i] > cost)
        {
            Costs[i] = cost;
            DistArray[i] = (ushort)(i - position + 1);
        }
    }

    // port: src/enc/backward_references_cost_enc.c ConnectIntervals
    private void Connect(int previous, int next)
    {
        if (previous >= 0) _next[previous] = next;
        else _head = next;
        if (next >= 0) _previous[next] = previous;
    }

    // port: src/enc/backward_references_cost_enc.c PopInterval
    private void Pop(int interval)
    {
        if (interval < 0) return;
        Connect(_previous[interval], _next[interval]);
        _free[_freeCount++] = interval;
        _count--;
    }

    // port: src/enc/backward_references_cost_enc.c UpdateCostAtIndex
    // Applies every interval covering i; with 'clean', intervals ending before i are removed.
    public void UpdateCostAtIndex(int i, bool clean)
    {
        int current = _head;
        while (current >= 0 && _start[current] <= i)
        {
            int next = _next[current];
            if (_end[current] <= i)
            {
                if (clean) Pop(current);
            }
            else UpdateCost(i, _index[current], _cost[current]);
            current = next;
        }
    }

    // port: src/enc/backward_references_cost_enc.c PositionOrphanInterval
    private void PositionOrphan(int current, int previous)
    {
        if (previous < 0) previous = _head;
        while (previous >= 0 && _start[current] < _start[previous]) previous = _previous[previous];
        while (previous >= 0 && _next[previous] >= 0 && _start[_next[previous]] < _start[current]) previous = _next[previous];
        Connect(current, previous >= 0 ? _next[previous] : _head);
        Connect(previous, current);
    }

    // port: src/enc/backward_references_cost_enc.c InsertInterval
    private void Insert(int hint, long cost, int position, int start, int end)
    {
        if (start >= end) return;
        if (_count >= CacheIntervalSizeMax)
        {
            // Too many intervals: apply this one right away.
            for (int i = start; i < end; i++) UpdateCost(i, position, cost);
            return;
        }
        int interval = _free[--_freeCount];
        _cost[interval] = cost;
        _index[interval] = position;
        _start[interval] = start;
        _end[interval] = end;
        PositionOrphan(interval, hint);
        _count++;
    }

    // port: src/enc/backward_references_cost_enc.c PushInterval
    // Adds the copies of length 1..len starting at 'position', splitting or removing worse intervals.
    public void PushInterval(long distanceCost, int position, int len)
    {
        if (len < SkipDistance)
        {
            // Small intervals are applied right away.
            for (int j = position; j < position + len; j++)
            {
                int k = j - position;
                long cost = distanceCost + _costCache[k];
                if (Costs[j] > cost)
                {
                    Costs[j] = cost;
                    DistArray[j] = (ushort)(k + 1);
                }
            }
            return;
        }
        int interval = _head;
        for (int i = 0; i < _cacheIntervals && _cacheStart[i] < len; i++)
        {
            int start = position + _cacheStart[i];
            int end = position + Math.Min(_cacheEnd[i], len);
            long cost = distanceCost + _cacheCost[i];
            int next;
            for (; interval >= 0 && _start[interval] < end; interval = next)
            {
                next = _next[interval];
                if (start >= _end[interval]) continue;
                if (cost >= _cost[interval])
                {
                    // Worse than the existing interval: keep what lies before it.
                    int startNew = _end[interval];
                    Insert(interval, cost, position, start, _start[interval]);
                    start = startNew;
                    if (start >= end) break;
                    continue;
                }
                if (start <= _start[interval])
                {
                    if (_end[interval] <= end)
                    {
                        // Fully covered by the new interval.
                        Pop(interval);
                    }
                    else
                    {
                        _start[interval] = end;
                        break;
                    }
                }
                else
                {
                    if (end < _end[interval])
                    {
                        // The old interval contains the new one: split it.
                        int endOriginal = _end[interval];
                        _end[interval] = start;
                        Insert(interval, _cost[interval], _index[interval], end, endOriginal);
                        interval = _next[interval];
                        break;
                    }
                    _end[interval] = start;
                }
            }
            Insert(interval, cost, position, start, end);
        }
    }
}
