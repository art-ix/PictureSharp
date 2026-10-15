using System.Runtime.CompilerServices;

namespace PictureSharp.Processing.Quantization;

/// <summary>
/// Chained hash table keyed by packed RGBA pixels (R in the low byte), ported with its exact bucket order, resizing and
/// comparison rules because the median-cut result depends on the iteration order.
/// </summary>
// port: libImaging/QuantHash.c (Pillow 10.2.0)
internal sealed class QuantHashTable
{
    private const int MinLength = 11;
    private const int ResizeFactor = 3;

    // Nodes live in parallel arrays; -1 terminates a chain.
    private int[] _table;
    private int[] _next = new int[64];
    private uint[] _keys = new uint[64];
    private uint[] _values = new uint[64];
    private int _nodeCount;
    private uint _length;

    /// <param name="scaled">
    /// True for Quant.c's pixel_hash/pixel_cmp (keys compared by the hash of their channels shifted right by
    /// <see cref="Scale"/>); false for unshifted_pixel_hash/unshifted_pixel_cmp.
    /// </param>
    public QuantHashTable(bool scaled)
    {
        Scaled = scaled;
        _length = MinLength;
        _table = NewTable(MinLength);
    }

    public bool Scaled { get; }

    /// <summary>Right shift applied to the channels before hashing (PixelHashData.scale).</summary>
    public int Scale { get; set; }

    public uint Count { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint PixelHash(uint r, uint g, uint b) => (r * 463) ^ ((g << 8) * 10069) ^ ((b << 16) * 64997);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Hash(uint key) => Scaled
        ? PixelHash((key & 0xFF) >> Scale, ((key >> 8) & 0xFF) >> Scale, ((key >> 16) & 0xFF) >> Scale)
        : PixelHash(key & 0xFF, (key >> 8) & 0xFF, (key >> 16) & 0xFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Compare(uint a, uint b)
    {
        if (Scaled)
        {
            uint ha = Hash(a), hb = Hash(b);
            return ha == hb ? 0 : ha < hb ? -1 : 1;
        }

        int ra = (int)(a & 0xFF), rb = (int)(b & 0xFF);
        if (ra != rb)
        {
            return ra - rb;
        }

        int ga = (int)((a >> 8) & 0xFF), gb = (int)((b >> 8) & 0xFF);
        if (ga != gb)
        {
            return ga - gb;
        }

        return (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
    }

    /// <summary>Counts a pixel: inserts it with value 1, or increments the value of an equal key.</summary>
    // port: hashtable_insert_or_update_computed with new_count_func / exists_count_func
    public void Increment(uint key)
    {
        EnsureNodeCapacity();
        ref int link = ref FindLink(key, out int found);
        if (found >= 0)
        {
            _values[found]++;
            return;
        }

        link = NewNode(key, 1, link);
        Count++;
        Resize();
    }

    /// <summary>Inserts a key or replaces the value of an equal key (the stored key is kept).</summary>
    // port: hashtable_insert
    public void Insert(uint key, uint value)
    {
        EnsureNodeCapacity();
        ref int link = ref FindLink(key, out int found);
        if (found >= 0)
        {
            _values[found] = value;
            return;
        }

        link = NewNode(key, value, link);
        Count++;
        Resize();
    }

    // port: hashtable_lookup
    public bool TryGetValue(uint key, out uint value)
    {
        uint hash = Hash(key) % _length;
        for (int n = _table[hash]; n >= 0; n = _next[n])
        {
            int c = Compare(_keys[n], key);
            if (c == 0)
            {
                value = _values[n];
                return true;
            }

            if (c > 0)
            {
                break;
            }
        }

        value = 0;
        return false;
    }

    /// <summary>Rebuilds the table at the same length after <see cref="Scale"/> changed, adding up the values of keys that now collide.</summary>
    // port: hashtable_rehash_compute with rehash_collide
    public void RehashMerging() => Rehash(_length, merge: true);

    /// <summary>Visits all entries in bucket order.</summary>
    // port: hashtable_foreach
    public void ForEach(Action<uint, uint> visitor)
    {
        for (uint x = 0; x < _length; x++)
        {
            for (int n = _table[x]; n >= 0; n = _next[n])
            {
                visitor(_keys[n], _values[n]);
            }
        }
    }

    private ref int FindLink(uint key, out int found)
    {
        uint hash = Hash(key) % _length;
        ref int link = ref _table[hash];
        while (link >= 0)
        {
            int n = link;
            int c = Compare(_keys[n], key);
            if (c == 0)
            {
                found = n;
                return ref link;
            }

            if (c > 0)
            {
                break;
            }

            link = ref _next[n];
        }

        found = -1;
        return ref link;
    }

    // Grows the node arrays before FindLink hands out a reference into them.
    private void EnsureNodeCapacity()
    {
        if (_nodeCount == _keys.Length)
        {
            Array.Resize(ref _keys, _nodeCount * 2);
            Array.Resize(ref _values, _nodeCount * 2);
            Array.Resize(ref _next, _nodeCount * 2);
        }
    }

    private int NewNode(uint key, uint value, int next)
    {
        int n = _nodeCount++;
        _keys[n] = key;
        _values[n] = value;
        _next[n] = next;
        return n;
    }

    // port: _hashtable_resize
    private void Resize()
    {
        uint oldSize = _length;
        uint newSize = oldSize;
        if (Count * ResizeFactor < _length)
        {
            newSize = FindPrime((_length / 2) - 1, -1);
        }
        else if (_length * ResizeFactor < Count)
        {
            newSize = FindPrime((_length * 2) + 1, +1);
        }

        if (newSize < MinLength)
        {
            newSize = oldSize;
        }

        if (newSize != oldSize)
        {
            Rehash(newSize, merge: false);
        }
    }

    // port: _findPrime. The original's primality test never rejects a candidate (it computes `!start % t`),
    // so this returns the first value > 1 whose low four bits are 1, 3, 7, 9, 11 or 13.
    private static uint FindPrime(uint start, int direction)
    {
        ReadOnlySpan<byte> unit = [0, 1, 0, 1, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0];
        while (start > 1 && unit[(int)(start & 0x0F)] == 0)
        {
            start = (uint)(start + direction);
        }

        return start;
    }

    // port: _hashtable_rehash / _hashtable_insert_node (without resizing)
    private void Rehash(uint newSize, bool merge)
    {
        int[] oldTable = _table;
        uint oldSize = _length;
        _table = NewTable(newSize);
        _length = newSize;
        Count = 0;
        for (uint i = 0; i < oldSize; i++)
        {
            int nn;
            for (int node = oldTable[i]; node >= 0; node = nn)
            {
                nn = _next[node];
                ref int link = ref FindLink(_keys[node], out int found);
                if (found >= 0)
                {
                    // An equal key takes over the newer key; values merge (rehash_collide) or are replaced.
                    _keys[found] = _keys[node];
                    _values[found] = merge ? _values[found] + _values[node] : _values[node];
                    continue;
                }

                _next[node] = link;
                link = node;
                Count++;
            }
        }
    }

    private static int[] NewTable(uint length)
    {
        var table = new int[length];
        table.AsSpan().Fill(-1);
        return table;
    }
}
