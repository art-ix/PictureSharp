using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PictureSharp.Compression;

internal sealed partial class Deflater
{
    /// <summary>Receives the LZ77 parse in order.</summary>
    private interface ISymbolSink
    {
        /// <summary>A search starts at <paramref name="pos"/> without a pending match (the parse state is the position).</summary>
        void Fresh(int pos);

        void Literal(int pos, byte value);

        void Match(int pos, int length, int distance);
    }

    /// <summary>Hash chains with lazy matching.</summary>
    /// <remarks>
    /// Before a search at a position every earlier position (that has three bytes) is in the chains, whatever
    /// the parse did, so a search result depends only on the position and the pending match length. The chains
    /// reach back <see cref="MaxDistance"/> bytes, so a finder started anywhere with that much history inserted
    /// finds the same matches as one that started at the beginning.
    /// </remarks>
    private sealed class MatchFinder
    {
        private readonly byte[] _data;
        private readonly int _good;
        private readonly int _lazy;
        private readonly int _nice;
        private readonly int _chain;
        private readonly int[] _head = ArrayPool<int>.Shared.Rent(HashSize);
        private readonly int[] _prev = ArrayPool<int>.Shared.Rent(WindowSize);
        private int _inserted;  // positions < _inserted are in the hash chains

        public MatchFinder(byte[] data, int level)
        {
            _data = data;
            (_good, _lazy, _nice, _chain) = Config[level];
            Reset(0);
        }

        public void Release()
        {
            ArrayPool<int>.Shared.Return(_head);
            ArrayPool<int>.Shared.Return(_prev);
        }

        /// <summary>Prepares a parse starting at <paramref name="start"/>; the window before it is inserted lazily.</summary>
        // Slots of _prev are written when their position is inserted, before any read, so they need no clearing.
        public void Reset(int start)
        {
            _head.AsSpan(0, HashSize).Fill(-1);
            _inserted = Math.Max(0, start - WindowSize);
        }

        /// <summary>
        /// Parses from <paramref name="pos"/> (which must not be inside a pending match) and stops at the first
        /// fresh search position at or after <paramref name="limit"/>, which it returns.
        /// </summary>
        public int Parse<TSink>(int pos, int limit, ref TSink sink)
            where TSink : struct, ISymbolSink
        {
            byte[] data = _data;
            int n = data.Length;
            while (pos < n && pos < limit)
            {
                sink.Fresh(pos);
                (int len, int dist) cur = FindMatch(pos, MinMatch - 1);
                while (true)
                {
                    if (cur.len < MinMatch)
                    {
                        sink.Literal(pos, data[pos]);
                        pos++;
                        break;
                    }

                    if (cur.len < _lazy && pos + 1 < n)
                    {
                        (int len, int dist) next = FindMatch(pos + 1, cur.len);
                        if (next.len > cur.len)
                        {
                            sink.Literal(pos, data[pos]);
                            pos++;
                            cur = next;
                            continue;
                        }
                    }

                    sink.Match(pos, cur.len, cur.dist);
                    pos += cur.len;
                    InsertUpTo(Math.Min(pos, n - MinMatch + 1));
                    break;
                }
            }

            return pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Hash(ref byte data, int pos) =>
            (int)(((uint)(Unsafe.Add(ref data, pos) | (Unsafe.Add(ref data, pos + 1) << 8) | (Unsafe.Add(ref data, pos + 2) << 16))
                * 2654435761u) >> (32 - HashBits));

        // Inserts all positions in [_inserted, end) into the hash chains.
        private void InsertUpTo(int end)
        {
            ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
            ref int head = ref MemoryMarshal.GetArrayDataReference(_head);
            ref int prev = ref MemoryMarshal.GetArrayDataReference(_prev);
            int p = _inserted;
            for (; p < end; p++)
            {
                ref int slot = ref Unsafe.Add(ref head, Hash(ref data, p));
                Unsafe.Add(ref prev, p & WindowMask) = slot;
                slot = p;
            }

            if (end > _inserted)
            {
                _inserted = end;
            }
        }

        // Finds the longest match at 'pos' longer than 'prevLength' (inserting 'pos' into the chains).
        private (int Length, int Distance) FindMatch(int pos, int prevLength)
        {
            int n = _data.Length;
            if (pos + MinMatch > n)
            {
                return default;
            }

            InsertUpTo(pos);
            ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
            ref int prev = ref MemoryMarshal.GetArrayDataReference(_prev);
            ref int headSlot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head), Hash(ref data, pos));
            int candidate = headSlot;
            Unsafe.Add(ref prev, pos & WindowMask) = candidate;
            headSlot = pos;
            _inserted = pos + 1;

            int maxLen = Math.Min(MaxMatch, n - pos);
            int bestLen = prevLength;
            if (bestLen >= maxLen)
            {
                return default;
            }

            int bestDist = 0;
            int chain = prevLength >= _good ? _chain >> 2 : _chain;
            int lowest = Math.Max(0, pos - MaxDistance);
            ref byte scan = ref Unsafe.Add(ref data, pos);

            // A candidate can beat bestLen only if bytes 0..bestLen all match (bestLen >= 2), so comparing the
            // two-byte pairs at 0 and bestLen - 1 skips only candidates the full comparison would reject.
            ushort scanStart = Unsafe.ReadUnaligned<ushort>(ref scan);
            ushort scanEnd = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref scan, bestLen - 1));
            while (candidate >= lowest && chain-- > 0)
            {
                ref byte match = ref Unsafe.Add(ref data, candidate);
                if (Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref match, bestLen - 1)) == scanEnd &&
                    Unsafe.ReadUnaligned<ushort>(ref match) == scanStart)
                {
                    int len = MatchLength(ref match, ref scan, maxLen);
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestDist = pos - candidate;
                        if (len >= _nice || len >= maxLen)
                        {
                            break;
                        }

                        scanEnd = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref scan, bestLen - 1));
                    }
                }

                int next = Unsafe.Add(ref prev, candidate & WindowMask);
                if (next >= candidate)
                {
                    break;
                }

                candidate = next;
            }

            // Short matches far away cost more than literals (zlib's TOO_FAR heuristic).
            if (bestLen == MinMatch && bestDist > 4096)
            {
                return default;
            }

            return bestDist == 0 ? default : (bestLen, bestDist);
        }

        // Length of the common prefix of 'a' and 'b', at most maxLen; both have maxLen readable bytes.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MatchLength(ref byte a, ref byte b, int maxLen)
        {
            if (!BitConverter.IsLittleEndian)
            {
                return MemoryMarshal.CreateReadOnlySpan(ref a, maxLen).CommonPrefixLength(MemoryMarshal.CreateReadOnlySpan(ref b, maxLen));
            }

            int len = 0;
            for (; len + sizeof(ulong) <= maxLen; len += sizeof(ulong))
            {
                ulong diff = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, len)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, len));
                if (diff != 0)
                {
                    return len + (BitOperations.TrailingZeroCount(diff) >> 3);
                }
            }

            while (len < maxLen && Unsafe.Add(ref a, len) == Unsafe.Add(ref b, len))
            {
                len++;
            }

            return len;
        }
    }
}
