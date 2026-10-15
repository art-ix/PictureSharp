namespace PictureSharp.Internal;

/// <summary>Helpers for deterministic parallel loops.</summary>
internal static class Parallelism
{
    /// <summary>Whether work of the given size is worth splitting across threads.</summary>
    public static bool IsWorthwhile(int maxDegreeOfParallelism, long work, long minimumWork) =>
        maxDegreeOfParallelism != 1 && Environment.ProcessorCount > 1 && work >= minimumWork;

    /// <summary>Options for <see cref="Parallel"/> loops; a canceled token stops the loop with <see cref="OperationCanceledException"/>.</summary>
    public static ParallelOptions Options(int maxDegreeOfParallelism, CancellationToken cancellationToken = default) =>
        new() { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken };

    /// <summary>Lets a consumer follow a producer that publishes rows in order (or aborts).</summary>
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

        /// <summary>Waits until <paramref name="row"/> is published; false when the producer aborted first.</summary>
        // Blocks instead of spinning: a spinning consumer would compete with the producer for the core.
        public bool WaitFor(int row)
        {
            lock (_lock)
            {
                while (_ready <= row && !_aborted) Monitor.Wait(_lock);
                return _ready > row;
            }
        }
    }
}
