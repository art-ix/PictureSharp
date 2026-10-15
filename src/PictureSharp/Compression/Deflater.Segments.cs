using System.Buffers;
using PictureSharp.Internal;

namespace PictureSharp.Compression;

internal sealed partial class Deflater
{
    private const int SegmentSize = 1 << 16;
    private const int ParallelMinimumInput = 4 * SegmentSize;

    // A parse step reaches at most 258 positions of lazy literals plus a 258-byte match past its start.
    private const int SegmentOverrun = 2 * MaxMatch;

    /// <summary>
    /// Parses the input in parallel segments, then joins them in order into the same symbols as a
    /// sequential parse.
    /// </summary>
    /// <remarks>
    /// The parse state at a fresh search is only the position (see <see cref="MatchFinder"/>), so a segment
    /// parsed speculatively from its first byte agrees with the true parse from the first fresh position
    /// they share. The join replays the true parse into a segment (sequentially, usually a few symbols)
    /// until it reaches such a position, then copies the rest of the segment.
    /// </remarks>
    private void CompressSegmented()
    {
        int n = _input.Length;
        int segmentCount = (n + SegmentSize - 1) / SegmentSize;
        int degree = _maxDegreeOfParallelism > 0 ? _maxDegreeOfParallelism : Environment.ProcessorCount;
        int wave = Math.Min(segmentCount, degree);

        // Two sets of segments: the next wave is parsed while this thread joins and writes the current one.
        var sets = new Segment[2][];
        for (int s = 0; s < 2; s++)
        {
            sets[s] = new Segment[wave];
            for (int i = 0; i < wave; i++)
            {
                sets[s][i] = new Segment();
            }
        }

        MatchFinder? joinFinder = null;
        int joinPosition = -1;  // position at which joinFinder may continue without a reset
        int pos = 0;
        var sink = new BlockSink(this);
        Task? pending = null;
        try
        {
            pending = ParseWave(sets[0], 0);
            for (int first = 0, set = 0; first < segmentCount; first += wave, set ^= 1)
            {
                pending!.GetAwaiter().GetResult();
                pending = first + wave < segmentCount ? ParseWave(sets[set ^ 1], first + wave) : null;
                Segment[] segments = sets[set];
                for (int i = 0; i < Math.Min(wave, segmentCount - first); i++)
                {
                    Segment segment = segments[i];
                    while (pos < segment.Exit)
                    {
                        int symbol = segment.SymbolAt(pos);
                        if (symbol >= 0)
                        {
                            pos = segment.Replay(symbol, pos, ref sink);
                            break;
                        }

                        if (joinFinder is null)
                        {
                            joinFinder = new MatchFinder(_input, _level);
                            joinFinder.Reset(pos);
                        }
                        else if (joinPosition != pos)
                        {
                            joinFinder.Reset(pos);
                        }

                        pos = joinFinder.Parse(pos, pos + 1, ref sink);
                        joinPosition = pos;
                    }
                }
            }
        }
        finally
        {
            // Rented buffers go back to the pool only after no parse uses them.
            try
            {
                pending?.Wait();
            }
            catch (AggregateException)
            {
            }

            joinFinder?.Release();
            foreach (Segment[] segments in sets)
            {
                foreach (Segment segment in segments)
                {
                    segment.Release();
                }
            }
        }

        Task ParseWave(Segment[] segments, int first)
        {
            int count = Math.Min(segments.Length, segmentCount - first);
            return Task.Run(() => Parallel.For(0, count, Parallelism.Options(_maxDegreeOfParallelism),
                () => new MatchFinder(_input, _level),
                (i, _, finder) =>
                {
                    int start = (first + i) * SegmentSize;
                    segments[i].Parse(finder, start, Math.Min(start + SegmentSize, n));
                    return finder;
                },
                finder => finder.Release()));
        }
    }

    /// <summary>Speculative parse of one segment.</summary>
    private sealed class Segment
    {
        // Literal: the byte; match: length << 16 | distance (length >= 3, so the high half is non-zero).
        private readonly uint[] _symbols = ArrayPool<uint>.Shared.Rent(SegmentSize + SegmentOverrun);

        // Symbol index + 1 of the fresh search at each segment position, 0 where no search started.
        private readonly int[] _fresh = ArrayPool<int>.Shared.Rent(SegmentSize);
        private int _start;
        private int _end;
        private int _count;

        /// <summary>The first fresh search position at or after the segment end.</summary>
        public int Exit { get; private set; }

        public void Parse(MatchFinder finder, int start, int end)
        {
            _start = start;
            _end = end;
            _fresh.AsSpan(0, end - start).Clear();
            finder.Reset(start);
            var sink = new SegmentSink(this);
            Exit = finder.Parse(start, end, ref sink);
            _count = sink.Count;
        }

        /// <summary>Index of the symbol emitted by a fresh search at <paramref name="pos"/>, or -1.</summary>
        public int SymbolAt(int pos)
        {
            int offset = pos - _start;
            return (uint)offset < (uint)(_end - _start) ? _fresh[offset] - 1 : -1;
        }

        /// <summary>Emits symbols from <paramref name="symbol"/> (starting at <paramref name="pos"/>) and returns <see cref="Exit"/>.</summary>
        public int Replay(int symbol, int pos, ref BlockSink sink)
        {
            uint[] symbols = _symbols;
            for (int i = symbol; i < _count; i++)
            {
                uint value = symbols[i];
                int length = (int)(value >> 16);
                if (length == 0)
                {
                    sink.Literal(pos, (byte)value);
                    pos++;
                }
                else
                {
                    sink.Match(pos, length, (int)(value & 0xffff));
                    pos += length;
                }
            }

            return pos;
        }

        public void Release()
        {
            ArrayPool<uint>.Shared.Return(_symbols);
            ArrayPool<int>.Shared.Return(_fresh);
        }

        private struct SegmentSink(Segment segment) : ISymbolSink
        {
            public int Count;

            public readonly void Fresh(int pos) => segment._fresh[pos - segment._start] = Count + 1;

            public void Literal(int pos, byte value) => segment._symbols[Count++] = value;

            public void Match(int pos, int length, int distance) => segment._symbols[Count++] = ((uint)length << 16) | (uint)distance;
        }
    }
}
