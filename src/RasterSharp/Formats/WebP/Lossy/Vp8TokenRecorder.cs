using System.Buffers;

namespace RasterSharp.Formats.WebP.Lossy;

/// <summary>
/// Tokens, statistics updates and filter statistics of a run of macroblocks, kept aside so that a parallel pass
/// can replay them in raster order: statistics halve on overflow and filter statistics are floating-point sums,
/// so their result depends on the order of the updates.
/// </summary>
internal sealed class Vp8TokenRecorder : IDisposable
{
    public const ushort NoStatistics = ushort.MaxValue;

    private ushort[] _tokens = ArrayPool<ushort>.Shared.Rent(4096);
    private ushort[] _statistics = ArrayPool<ushort>.Shared.Rent(4096);
    private int _count;
    private readonly List<(int Index, double Value)> _filter = [];

    public void Add(bool bit, int probability, bool constant, int statisticsIndex)
    {
        if (_count == _tokens.Length)
        {
            Grow(ref _tokens);
            Grow(ref _statistics);
        }
        _tokens[_count] = Vp8TokenBuffer.Token(bit, probability, constant);
        _statistics[_count++] = constant ? NoStatistics : (ushort)statisticsIndex;
    }

    public void AddFilterStatistic(int index, double value) => _filter.Add((index, value));

    // Appends the recorded tokens to the pass buffer and applies the deferred updates, then starts over.
    public void Replay(Vp8TokenBuffer tokens, Vp8EncoderProba probabilities, Vp8EncoderFilter filter)
    {
        for (int i = 0; i < _count; i++)
        {
            ushort token = _tokens[i];
            tokens.AddRaw(token);
            if (_statistics[i] != NoStatistics) probabilities.Record((token & 0x8000) != 0, _statistics[i]);
        }
        foreach (var (index, value) in _filter) filter.AddStatistic(index, value);
        _count = 0;
        _filter.Clear();
    }

    private void Grow(ref ushort[] array)
    {
        ushort[] next = ArrayPool<ushort>.Shared.Rent(array.Length * 2);
        array.AsSpan(0, _count).CopyTo(next);
        ArrayPool<ushort>.Shared.Return(array);
        array = next;
    }

    public void Dispose()
    {
        ArrayPool<ushort>.Shared.Return(_tokens);
        ArrayPool<ushort>.Shared.Return(_statistics);
    }
}
