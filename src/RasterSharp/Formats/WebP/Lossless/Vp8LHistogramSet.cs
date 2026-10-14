using System.Buffers;

namespace RasterSharp.Formats.WebP.Lossless;

// port: src/enc/histogram_enc.c VP8LAllocateHistogramSet
// The histograms share one pooled slab of counts. The histogram objects come from a pooled array as well and
// are rebound to the new slab, so none of them may be used after Dispose.
internal sealed class Vp8LHistogramSet : IDisposable
{
    private readonly int _cacheBits;
    private readonly int _size;
    private readonly int _capacity;
    private uint[]? _slab;
    private Vp8LHistogram?[]? _histograms;
    private int _count;

    public Vp8LHistogramSet(int capacity, int cacheBits)
    {
        _cacheBits = cacheBits;
        _size = Vp8LHistogram.Size(cacheBits);
        _capacity = capacity;
        _slab = ArrayPool<uint>.Shared.Rent(Math.Max(1, checked(capacity * _size)));
        _histograms = ArrayPool<Vp8LHistogram?>.Shared.Rent(Math.Max(1, capacity));
    }

    public Vp8LHistogram Create()
    {
        ObjectDisposedException.ThrowIf(_slab is null || _histograms is null, this);
        if (_count == _capacity) throw new InvalidOperationException("The histogram set is full.");
        Vp8LHistogram histogram = Vp8LHistogram.Bind(_histograms[_count], _cacheBits, _slab, _count * _size);
        _histograms[_count++] = histogram;
        return histogram;
    }

    public void Dispose()
    {
        if (_slab is null || _histograms is null) return;
        ArrayPool<uint>.Shared.Return(_slab);
        // The objects stay in the array for the next set; they hold no counts of their own.
        for (int i = 0; i < _count; i++) _histograms[i]!.Release();
        ArrayPool<Vp8LHistogram?>.Shared.Return(_histograms);
        _slab = null;
        _histograms = null;
    }
}
