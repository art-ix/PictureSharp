using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace RasterSharp.Internal;

/// <summary>Helpers for deterministic parallel loops.</summary>
internal static class Parallelism
{
    /// <summary>Whether work of the given size is worth splitting across threads.</summary>
    public static bool IsWorthwhile(int maxDegreeOfParallelism, long work, long minimumWork) =>
        maxDegreeOfParallelism != 1 && Environment.ProcessorCount > 1 && work >= minimumWork;

    public static ParallelOptions Options(int maxDegreeOfParallelism) => new() { MaxDegreeOfParallelism = maxDegreeOfParallelism };

    /// <summary>
    /// Runs <paramref name="action"/>(x, y) for every cell of a width × height grid so that a cell starts only after
    /// its left neighbour and the cell above-right (or above, in the last column) have finished: the order of
    /// conflicting accesses is then the same as in a raster scan.
    /// </summary>
    public static void Wavefront(int width, int height, int maxDegreeOfParallelism, Action<int, int> action, RowGate? gate = null)
    {
        var progress = new int[height];
        bool failed = false;
        // Rows are handed out one at a time in increasing order, so every row a worker waits for is already
        // being processed: the loop cannot deadlock.
        var rows = Partitioner.Create(Enumerable.Range(0, height), EnumerablePartitionerOptions.NoBuffering);
        try
        {
            Parallel.ForEach(rows, Options(maxDegreeOfParallelism), Row);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }

        void Row(int y)
        {
            try
            {
                if (gate is not null && !gate.WaitFor(y, ref failed)) return;
                for (int x = 0; x < width; x++)
                {
                    if (y > 0)
                    {
                        int above = Math.Min(x + 2, width);
                        var spin = new SpinWait();
                        while (Volatile.Read(ref progress[y - 1]) < above)
                        {
                            if (Volatile.Read(ref failed)) return;
                            spin.SpinOnce(-1);
                        }
                    }
                    action(x, y);
                    Volatile.Write(ref progress[y], x + 1);
                }
            }
            catch
            {
                Volatile.Write(ref failed, true);
                throw;
            }
        }
    }

    /// <summary>Lets a wavefront follow a producer that publishes rows in order (or aborts).</summary>
    public sealed class RowGate
    {
        private readonly object _lock = new();
        private int _ready;
        private bool _aborted;

        public void Publish(int rows)
        {
            lock (_lock)
            {
                _ready = rows;
                Monitor.PulseAll(_lock);
            }
        }

        public void Abort()
        {
            lock (_lock)
            {
                _aborted = true;
                Monitor.PulseAll(_lock);
            }
        }

        // Blocks instead of spinning: waiting workers would otherwise compete with the producer for the core.
        public bool WaitFor(int row, ref bool failed)
        {
            lock (_lock)
            {
                while (_ready <= row && !_aborted && !Volatile.Read(ref failed)) Monitor.Wait(_lock, 10);
                return _ready > row;
            }
        }
    }
}
